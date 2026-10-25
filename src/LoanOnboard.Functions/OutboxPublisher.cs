using System.Collections.Concurrent;
using Azure.Messaging.ServiceBus;
using LoanOnboard.Core.Services;

namespace LoanOnboard.Functions;

/// <summary>Turns one claimed outbox row into a Service Bus message.</summary>
public class OutboxPublisher(ServiceBusClient client)
{
    private readonly ConcurrentDictionary<string, ServiceBusSender> _senders = new();

    public async Task SendAsync(OutboxRow row, CancellationToken ct)
    {
        var sender = _senders.GetOrAdd(row.Topic, client.CreateSender);

        var message = new ServiceBusMessage(row.Payload)
        {
            ContentType = "application/json",
            MessageId = row.Id.ToString(),   // duplicate detection key: a re-send of the same row is dropped
            SessionId = row.AckId,           // keeps one application's messages in order
            Subject = row.Type,
            CorrelationId = row.AckId
        };
        if (row.Stage is not null) message.ApplicationProperties["stage"] = row.Stage;

        await sender.SendMessageAsync(message, ct);
    }
}
