using LoanOnboard.Core.Common;
using LoanOnboard.Core.Data;
using LoanOnboard.Core.Domain;
using LoanOnboard.Core.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LoanOnboard.Core.Services;

public enum HandlingResult
{
    Processed,
    /// <summary>Already handled earlier (inbox hit). Safe to complete the message.</summary>
    Duplicate,
    /// <summary>Stale or out-of-order message. Recorded and skipped.</summary>
    Ignored,
    /// <summary>Message can never be handled. Dead-letter it.</summary>
    Poison
}

/// <summary>
/// The only writer of application status after intake. Every handler is idempotent: it records the eventId in the
/// inbox table in the same SaveChanges as its state change.
/// </summary>
public class OrchestratorService(LoanDbContext db, LoanWorkflow workflow, Microsoft.Extensions.Options.IOptions<LoanOptions> options,
    ILogger<OrchestratorService> log)
{
    private const string Consumer = "orchestrator";
    private readonly LoanOptions _options = options.Value;

    public async Task<HandlingResult> HandleStageResultAsync(StageResult result, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(result.EventId) || string.IsNullOrWhiteSpace(result.AckId)
            || !StageOutcome.All.Contains(result.Outcome))
        {
            log.LogError("Invalid stage result {@Result}", result);
            return HandlingResult.Poison;
        }

        if (await AlreadyProcessedAsync(result.EventId, ct)) return HandlingResult.Duplicate;

        var app = await db.Applications.SingleOrDefaultAsync(a => a.AckId == result.AckId, ct);
        if (app is null)
        {
            log.LogError("Stage result for unknown application {AckId}", result.AckId);
            return HandlingResult.Poison;
        }

        MarkProcessed(result.EventId);

        var inFlight = app.CurrentStage == result.Stage
                       && (AppStatus.IsStageStatus(app.Status) || app.Status == AppStatus.Delayed);
        var execution = inFlight
            ? await db.StageExecutions
                .Where(e => e.AckId == app.AckId && e.Stage == result.Stage && e.Outcome == null)
                .OrderByDescending(e => e.Attempt)
                .FirstOrDefaultAsync(ct)
            : null;

        if (execution is null || execution.Attempt != result.Attempt)
        {
            log.LogWarning("Ignoring stale result {EventId} for {AckId} stage {Stage} attempt {Attempt} (status {Status})",
                result.EventId, app.AckId, result.Stage, result.Attempt, app.Status);
            await db.SaveChangesAsync(ct);
            return HandlingResult.Ignored;
        }

        execution.Outcome = result.Outcome;
        execution.ReasonCode = result.ReasonCode;
        execution.CompletedAt = workflow.UtcNow;
        const string source = "orchestrator";

        switch (result.Outcome)
        {
            case StageOutcome.Passed:
                await workflow.ScheduleNextAsync(app, result.Stage, source, ct);
                break;

            case StageOutcome.Rejected:
                workflow.SetStatus(app, AppStatus.Rejected, result.Stage, result.ReasonCode, source);
                workflow.QueueNotification(app, EmailTemplates.ApplicationRejected, ["DEALER", "CUSTOMER"], result.ReasonCode);
                break;

            case StageOutcome.NeedsInfo:
                workflow.SetStatus(app, AppStatus.NeedsInfo, result.Stage, result.ReasonCode, source);
                workflow.QueueNotification(app, EmailTemplates.DocumentsNeeded, ["DEALER"], result.ReasonCode);
                break;

            case StageOutcome.Error:
                await HandleStageErrorAsync(app, result, source, ct);
                break;
        }

        await db.SaveChangesAsync(ct);
        return HandlingResult.Processed;
    }

    /// <summary>Transient failure: retry with exponential backoff, then fail technically (no decline email).</summary>
    private async Task HandleStageErrorAsync(LoanApplication app, StageResult result, string source, CancellationToken ct)
    {
        if (result.Attempt < _options.MaxStageAttempts)
        {
            var plan = await db.StagePlans.AsNoTracking()
                .FirstAsync(p => p.ProductType == app.ProductType && p.Stage == result.Stage, ct);
            var backoff = TimeSpan.FromMinutes(Math.Pow(2, result.Attempt));
            workflow.RequestStage(app, plan, result.Attempt + 1, backoff, source);
            return;
        }

        workflow.SetStatus(app, AppStatus.FailedTechnical, result.Stage,
            $"Stage {result.Stage} failed after {result.Attempt} attempts: {result.ReasonCode}", source);
        log.LogCritical("ALERT: {AckId} failed technically at stage {Stage} after {Attempts} attempts",
            app.AckId, result.Stage, result.Attempt);
    }

    /// <summary>The DBA sync job reports that servicing has the loan: SYNCED, then request the welcome email.</summary>
    public async Task<HandlingResult> HandleSyncCompletedAsync(SyncCompletedEvent evt, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(evt.EventId) || string.IsNullOrWhiteSpace(evt.LoanNumber))
        {
            log.LogError("Invalid SyncCompleted event {@Event}", evt);
            return HandlingResult.Poison;
        }

        if (await AlreadyProcessedAsync(evt.EventId, ct)) return HandlingResult.Duplicate;

        var app = await db.Applications.SingleOrDefaultAsync(a => a.LoanNumber == evt.LoanNumber, ct);
        if (app is null)
        {
            log.LogError("SyncCompleted for unknown loan number {LoanNumber}", evt.LoanNumber);
            return HandlingResult.Poison;
        }

        MarkProcessed(evt.EventId);

        if (app.Status != AppStatus.LoanBooked)
        {
            log.LogWarning("Ignoring SyncCompleted for {AckId} in status {Status}", app.AckId, app.Status);
            await db.SaveChangesAsync(ct);
            return HandlingResult.Ignored;
        }

        const string source = "datasync";
        workflow.SetStatus(app, AppStatus.Synced, null, $"Checksum {evt.Checksum}", source);
        workflow.QueueNotification(app, EmailTemplates.Welcome, ["CUSTOMER"], loanNumber: app.LoanNumber);
        workflow.SetStatus(app, AppStatus.Active, null, "Welcome email requested", "orchestrator");

        await db.SaveChangesAsync(ct);
        return HandlingResult.Processed;
    }

    private Task<bool> AlreadyProcessedAsync(string eventId, CancellationToken ct) =>
        db.ProcessedMessages.AnyAsync(p => p.EventId == eventId && p.Consumer == Consumer, ct);

    private void MarkProcessed(string eventId) =>
        db.ProcessedMessages.Add(new ProcessedMessage
        {
            EventId = eventId,
            Consumer = Consumer,
            ProcessedAt = workflow.UtcNow
        });
}
