using System.Text.Json;
using LoanOnboard.Core.Common;
using LoanOnboard.Core.Data;
using LoanOnboard.Core.Domain;
using LoanOnboard.Core.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace LoanOnboard.Core.Services;

public class InvalidStateTransitionException(string from, string to)
    : InvalidOperationException($"Transition {from} -> {to} is not allowed.")
{
    public string From { get; } = from;
    public string To { get; } = to;
}

/// <summary>
/// Shared state-change logic. It only adds entities to the DbContext. The caller calls SaveChanges once, so the
/// status change, history row, stage execution and outbox row always commit in one SQL transaction.
/// </summary>
public class LoanWorkflow(LoanDbContext db, TimeProvider clock, IOptions<LoanOptions> options)
{
    private readonly LoanOptions _options = options.Value;

    public DateTime UtcNow => clock.GetUtcNow().UtcDateTime;

    public void AddHistory(LoanApplication app, string? reason, string source)
    {
        db.StatusHistory.Add(new LoanStatusHistory
        {
            AckId = app.AckId,
            Stage = app.CurrentStage,
            Status = app.Status,
            Reason = reason,
            EventId = Guid.NewGuid(),
            CorrelationId = app.AckId,
            OccurredAt = UtcNow,
            Source = source
        });
    }

    /// <summary>Moves the application to a new status if the transition table allows it.</summary>
    public void SetStatus(LoanApplication app, string to, string? stage, string? reason, string source)
    {
        if (!AppStatus.CanMove(app.Status, to)) throw new InvalidStateTransitionException(app.Status, to);

        app.Status = to;
        if (AppStatus.IsStageStatus(to)) app.CurrentStage = stage;
        else if (to is AppStatus.PendingAgentReview or AppStatus.LoanBooked or AppStatus.Synced or AppStatus.Active)
            app.CurrentStage = null;
        app.UpdatedAt = UtcNow;
        AddHistory(app, reason, source);
    }

    /// <summary>
    /// Starts the stage after <paramref name="afterStage"/> (or the first stage when null). When no stage is left the
    /// application goes to PENDING_AGENT_REVIEW.
    /// </summary>
    public async Task ScheduleNextAsync(LoanApplication app, string? afterStage, string source, CancellationToken ct)
    {
        var plans = await db.StagePlans.AsNoTracking()
            .Where(p => p.ProductType == app.ProductType)
            .OrderBy(p => p.Sequence)
            .ToListAsync(ct);

        StagePlan? next;
        if (afterStage is null)
        {
            next = plans.FirstOrDefault();
        }
        else
        {
            var current = plans.FirstOrDefault(p => p.Stage == afterStage);
            next = current is null ? null : plans.FirstOrDefault(p => p.Sequence > current.Sequence);
        }

        if (next is null)
        {
            SetStatus(app, AppStatus.PendingAgentReview, null, "All stages passed", source);
            return;
        }

        RequestStage(app, next, attempt: 1, delay: TimeSpan.Zero, source);
    }

    /// <summary>Records a stage execution and queues the command for the owning team.</summary>
    public void RequestStage(LoanApplication app, StagePlan plan, int attempt, TimeSpan delay, string source)
    {
        var now = UtcNow;
        var dueBy = now + delay + TimeSpan.FromMinutes(plan.DueMinutes);

        db.StageExecutions.Add(new StageExecution
        {
            AckId = app.AckId,
            Stage = plan.Stage,
            Attempt = attempt,
            StartedAt = now + delay,
            DueBy = dueBy
        });

        SetStatus(app, plan.StatusName, plan.Stage, attempt == 1 ? null : $"Retry attempt {attempt}", source);

        var command = new StageCommand(
            EventId: Guid.NewGuid().ToString(),
            AckId: app.AckId,
            CorrelationId: app.AckId,
            Stage: plan.Stage,
            Attempt: attempt,
            CustomerRef: CustomerRef(app),
            RequestedAmount: app.RequestedAmount,
            TermMonths: app.TermMonths,
            DueBy: dueBy);

        Enqueue(app.AckId, MessageTypes.StageRequested, Topics.StageCommands, plan.Stage, command, now + delay);
    }

    public void QueueNotification(LoanApplication app, string templateId, string[] recipients,
        string? reasonCategory = null, string? loanNumber = null)
    {
        var message = new NotificationMessage(
            Guid.NewGuid().ToString(), app.AckId, templateId, recipients, app.DealerId,
            CustomerRef(app), reasonCategory, loanNumber);
        Enqueue(app.AckId, MessageTypes.EmailRequested, Topics.Notifications, null, message, UtcNow);
    }

    public void QueueLoanBooked(LoanApplication app, string loanNumber)
    {
        var message = new LoanBookedEvent(Guid.NewGuid().ToString(), app.AckId, loanNumber, UtcNow);
        Enqueue(app.AckId, MessageTypes.LoanBooked, Topics.Events, null, message, UtcNow);
    }

    public static string CustomerRef(LoanApplication app) => $"cust-{app.CustomerId:N}";

    private void Enqueue(string ackId, string type, string topic, string? stage, object payload, DateTime availableAt)
    {
        var now = UtcNow;
        db.Outbox.Add(new OutboxMessage
        {
            AckId = ackId,
            Type = type,
            Topic = topic,
            Stage = stage,
            Payload = JsonSerializer.Serialize(payload, Json.Options),
            CreatedAt = now,
            AvailableAt = availableAt
        });
    }
}
