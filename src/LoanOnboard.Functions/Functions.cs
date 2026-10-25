using System.Text.Json;
using Azure.Messaging.ServiceBus;
using LoanOnboard.Core.Messaging;
using LoanOnboard.Core.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace LoanOnboard.Functions;

/// <summary>
/// Step 2-4 of the outbox: claim pending rows, send them to Service Bus, mark them published.
/// A crash between send and mark means a re-send; Service Bus duplicate detection (same MessageId) drops it.
/// </summary>
public class OutboxDispatcherFunction(OutboxRepository outbox, OutboxPublisher publisher, ILogger<OutboxDispatcherFunction> log)
{
    [Function("OutboxDispatcher")]
    public async Task Run([TimerTrigger("*/5 * * * * *")] TimerInfo timer, CancellationToken ct)
    {
        var rows = await outbox.ClaimBatchAsync(ct);
        if (rows.Count == 0) return;

        foreach (var row in rows)
        {
            try
            {
                await publisher.SendAsync(row, ct);
                await outbox.MarkPublishedAsync(row.Id, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The lock expires after LockedUntil and the row is claimed again. Stop here to keep ordering.
                log.LogError(ex, "Failed to publish outbox row {OutboxId} ({Type}) attempt {Attempts}",
                    row.Id, row.Type, row.Attempts);
                if (row.Attempts >= 10)
                    log.LogCritical("ALERT: outbox row {OutboxId} has failed {Attempts} times", row.Id, row.Attempts);
                break;
            }
        }

        var age = await outbox.OldestPendingAgeSecondsAsync(ct);
        if (age > 120) log.LogWarning("ALERT: oldest pending outbox row is {AgeSeconds}s old", age);
    }
}

/// <summary>Reads stage results from stage teams and drives the workflow.</summary>
public class StageResultFunction(OrchestratorService orchestrator, ILogger<StageResultFunction> log)
{
    [Function("StageResultOrchestrator")]
    public async Task Run(
        [ServiceBusTrigger(Topics.StageResults, Topics.OrchestratorSubscription,
            Connection = "ServiceBusConnection", IsSessionsEnabled = true)]
        ServiceBusReceivedMessage message,
        ServiceBusMessageActions actions,
        CancellationToken ct)
    {
        StageResult? result;
        try
        {
            result = JsonSerializer.Deserialize<StageResult>(message.Body.ToString(), Json.Options);
        }
        catch (JsonException ex)
        {
            await actions.DeadLetterMessageAsync(message, deadLetterReason: "InvalidJson",
                deadLetterErrorDescription: ex.Message, cancellationToken: ct);
            return;
        }

        if (result is null)
        {
            await actions.DeadLetterMessageAsync(message, deadLetterReason: "EmptyBody", cancellationToken: ct);
            return;
        }

        // An exception here abandons the message. Service Bus redelivers it and finally dead-letters it.
        var outcome = await orchestrator.HandleStageResultAsync(result, ct);
        log.LogInformation("Stage result {EventId} for {AckId} stage {Stage}: {Outcome} -> {Handling}",
            result.EventId, result.AckId, result.Stage, result.Outcome, outcome);

        if (outcome == HandlingResult.Poison)
            await actions.DeadLetterMessageAsync(message, deadLetterReason: "Unprocessable",
                deadLetterErrorDescription: "Unknown application, outcome or malformed result.", cancellationToken: ct);
        else
            await actions.CompleteMessageAsync(message, ct);
    }
}

/// <summary>The DBA sync job reports that servicing has the loan. Moves SYNCED -> ACTIVE and requests the welcome email.</summary>
public class SyncCompletedFunction(OrchestratorService orchestrator, ILogger<SyncCompletedFunction> log)
{
    [Function("SyncCompletedHandler")]
    public async Task Run(
        [ServiceBusTrigger(Topics.Events, Topics.OriginationSubscription, Connection = "ServiceBusConnection")]
        ServiceBusReceivedMessage message,
        ServiceBusMessageActions actions,
        CancellationToken ct)
    {
        SyncCompletedEvent? evt;
        try
        {
            evt = JsonSerializer.Deserialize<SyncCompletedEvent>(message.Body.ToString(), Json.Options);
        }
        catch (JsonException ex)
        {
            await actions.DeadLetterMessageAsync(message, deadLetterReason: "InvalidJson",
                deadLetterErrorDescription: ex.Message, cancellationToken: ct);
            return;
        }

        if (evt is null)
        {
            await actions.DeadLetterMessageAsync(message, deadLetterReason: "EmptyBody", cancellationToken: ct);
            return;
        }

        var outcome = await orchestrator.HandleSyncCompletedAsync(evt, ct);
        log.LogInformation("SyncCompleted {EventId} for loan {LoanNumber}: {Handling}", evt.EventId, evt.LoanNumber, outcome);

        if (outcome == HandlingResult.Poison)
            await actions.DeadLetterMessageAsync(message, deadLetterReason: "Unprocessable",
                deadLetterErrorDescription: "Unknown loan number or malformed event.", cancellationToken: ct);
        else
            await actions.CompleteMessageAsync(message, ct);
    }
}

/// <summary>Marks overdue stages DELAYED and expires applications that waited too long for documents.</summary>
public class StageTimeoutFunction(StageSweeper sweeper, ILogger<StageTimeoutFunction> log)
{
    [Function("StageTimeoutSweeper")]
    public async Task Run([TimerTrigger("0 * * * * *")] TimerInfo timer, CancellationToken ct)
    {
        var (delayed, expired) = await sweeper.SweepAsync(ct);
        if (delayed + expired > 0)
            log.LogInformation("Sweeper: {Delayed} stage(s) marked DELAYED, {Expired} application(s) expired", delayed, expired);
    }
}
