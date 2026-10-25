using System.Text.Json;
using LoanOnboard.Core.Common;
using LoanOnboard.Core.Data;
using LoanOnboard.Core.Domain;
using LoanOnboard.Core.Messaging;
using LoanOnboard.Core.Services;
using LoanOnboardService.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace LoanOnboardService.Services;

public abstract record ApprovalOutcome
{
    public record NotFound : ApprovalOutcome;
    public record Conflict(string Message) : ApprovalOutcome;
    public record ChecksFailed(IReadOnlyList<string> Failures) : ApprovalOutcome;
    public record AwaitingSecondApprover(string AckId) : ApprovalOutcome;
    public record Approved(string AckId, string LoanNumber, bool AlreadyBooked) : ApprovalOutcome;
}

/// <summary>
/// Enforces every approval rule on the server. The agent screen guides the agent, but nobody can book a loan by
/// calling the endpoint directly.
/// </summary>
public class ApprovalService(
    LoanDbContext db, LoanWorkflow workflow, IOptions<LoanOptions> options, ILogger<ApprovalService> log)
{
    private readonly LoanOptions _options = options.Value;

    public async Task<IReadOnlyList<QueueItem>> GetQueueAsync(int take, CancellationToken ct) =>
        await db.Applications.AsNoTracking()
            .Where(a => a.Status == AppStatus.PendingAgentReview)
            .OrderBy(a => a.UpdatedAt)
            .Take(Math.Clamp(take, 1, 200))
            .Select(a => new QueueItem(a.AckId, a.DealerId, a.RequestedAmount, a.TermMonths, a.UpdatedAt))
            .ToListAsync(ct);

    public async Task<ApprovalOutcome> ApproveAsync(string ackId, string approverId, ApproveRequest request, CancellationToken ct)
    {
        var app = await db.Applications.FirstOrDefaultAsync(a => a.AckId == ackId, ct);
        if (app is null) return new ApprovalOutcome.NotFound();

        // Approving twice never creates a second loan.
        if (app.LoanNumber is not null) return new ApprovalOutcome.Approved(ackId, app.LoanNumber, AlreadyBooked: true);

        if (app.Status != AppStatus.PendingAgentReview)
            return new ApprovalOutcome.Conflict($"Application is {app.Status}, not {AppStatus.PendingAgentReview}.");

        var failures = await CheckRulesAsync(app, request, ct);
        if (failures.Count > 0) return new ApprovalOutcome.ChecksFailed(failures);

        var previous = await db.AgentReviews
            .Where(r => r.AckId == ackId && r.Decision == "APPROVED")
            .Select(r => r.AgentId).ToListAsync(ct);
        if (previous.Contains(approverId))
            return new ApprovalOutcome.Conflict("You have already approved this application. A second approver is required.");

        db.AgentReviews.Add(new AgentReview
        {
            AckId = ackId,
            AgentId = approverId,
            Decision = "APPROVED",
            ChecklistJson = JsonSerializer.Serialize(new
            {
                request.ConfirmedAmount,
                request.ConfirmedTermMonths,
                request.DisclosuresDelivered
            }),
            ConsentRef = request.ConsentRef,
            ScriptVersion = request.ScriptVersion,
            CallReference = request.CallReference,
            Comment = request.Comment,
            DecidedAt = workflow.UtcNow
        });

        // Maker-checker: high amounts need two different approvers.
        var required = app.RequestedAmount > _options.SecondApproverThreshold ? 2 : 1;
        if (previous.Count + 1 < required)
        {
            await db.SaveChangesAsync(ct);
            log.LogInformation("{AckId} first approval recorded by {Approver}; second approver required", ackId, approverId);
            return new ApprovalOutcome.AwaitingSecondApprover(ackId);
        }

        // Loan number, status, history and both outbox rows commit together.
        var sequence = await db.Database.SqlQuery<long>($"SELECT NEXT VALUE FOR dbo.LoanNumberSeq AS [Value]").SingleAsync(ct);
        var loanNumber = LoanNumber.Format(sequence);

        app.LoanNumber = loanNumber;
        workflow.SetStatus(app, AppStatus.LoanBooked, null, $"Approved by {approverId}", "approval-api");
        workflow.QueueNotification(app, EmailTemplates.LoanApprovedDealer, ["DEALER"], loanNumber: loanNumber);
        workflow.QueueLoanBooked(app, loanNumber);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new ApprovalOutcome.Conflict("The application was changed by someone else. Reload and try again.");
        }

        log.LogInformation("Loan {LoanNumber} booked for {AckId} by {Approver}", loanNumber, ackId, approverId);
        return new ApprovalOutcome.Approved(ackId, loanNumber, AlreadyBooked: false);
    }

    private async Task<List<string>> CheckRulesAsync(LoanApplication app, ApproveRequest request, CancellationToken ct)
    {
        var failures = new List<string>();
        var now = workflow.UtcNow;

        if (string.IsNullOrWhiteSpace(request.ConsentRef)) failures.Add("Consent reference is required.");
        if (string.IsNullOrWhiteSpace(request.ScriptVersion)) failures.Add("Call script version is required.");
        if (string.IsNullOrWhiteSpace(request.CallReference)) failures.Add("Call reference is required.");
        if (!request.DisclosuresDelivered) failures.Add("Disclosures must be delivered to the customer.");
        if (request.ConfirmedAmount != app.RequestedAmount || request.ConfirmedTermMonths != app.TermMonths)
            failures.Add("Confirmed terms do not match the application.");

        // Every required stage must have passed, and recently enough.
        var requiredStages = await db.StagePlans.AsNoTracking()
            .Where(p => p.ProductType == app.ProductType && p.Required)
            .Select(p => p.Stage).ToListAsync(ct);
        var executions = await db.StageExecutions.AsNoTracking()
            .Where(e => e.AckId == app.AckId && e.Outcome != null)
            .ToListAsync(ct);
        var validFrom = now.AddHours(-_options.StageValidityHours);
        foreach (var stage in requiredStages)
        {
            var latest = executions.Where(e => e.Stage == stage).OrderByDescending(e => e.Attempt).FirstOrDefault();
            if (latest is null || latest.Outcome != StageOutcome.Passed)
                failures.Add($"Stage {stage} has not passed.");
            else if (latest.CompletedAt < validFrom)
                failures.Add($"Stage {stage} result is older than {_options.StageValidityHours} hours and must be re-run.");
        }

        // Documents: present, scanned clean and not expired.
        var documents = await db.Documents.AsNoTracking().Where(d => d.AckId == app.AckId).ToListAsync(ct);
        if (documents.Count == 0) failures.Add("No documents on file.");
        if (documents.Any(d => d.ScanStatus != ScanStatus.Clean)) failures.Add("A document has not been scanned clean.");
        if (documents.Any(d => d.ExpiresAt is not null && d.ExpiresAt < now)) failures.Add("A document has expired.");

        // The same person must not have another live application.
        var hash = await db.Customers.AsNoTracking().Where(c => c.AckId == app.AckId)
            .Select(c => c.IdentifierHash).FirstOrDefaultAsync(ct);
        if (hash is not null)
        {
            var deadEnds = AppStatus.DeadEnds;
            var duplicate = await (from c in db.Customers
                                   join a in db.Applications on c.AckId equals a.AckId
                                   where c.IdentifierHash == hash && a.AckId != app.AckId && !deadEnds.Contains(a.Status)
                                   select a.AckId).AnyAsync(ct);
            if (duplicate) failures.Add("The customer has another active application.");
        }

        return failures;
    }
}
