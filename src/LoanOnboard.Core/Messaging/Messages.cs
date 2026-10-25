using System.Text.Json;

namespace LoanOnboard.Core.Messaging;

public static class Topics
{
    public const string StageCommands = "loan.stage.commands";
    public const string StageResults = "loan.stage.results";
    public const string Notifications = "loan.notifications";
    public const string Events = "loan.events";

    public const string OrchestratorSubscription = "sub-orchestrator";
    public const string OriginationSubscription = "sub-origination";
}

public static class MessageTypes
{
    public const string StageRequested = "StageRequested";
    public const string StageCompleted = "StageCompleted";
    public const string EmailRequested = "EmailRequested";
    public const string LoanBooked = "LoanBooked";
    public const string SyncCompleted = "SyncCompleted";
}

public static class EmailTemplates
{
    public const string LoanApprovedDealer = "LOAN_APPROVED_DEALER";
    public const string ApplicationRejected = "APPLICATION_REJECTED";
    public const string DocumentsNeeded = "DOCUMENTS_NEEDED";
    public const string Welcome = "WELCOME";
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}

/// <summary>loan.stage.commands. Carries ids and references, never personal data or documents.</summary>
public record StageCommand(
    string EventId, string AckId, string CorrelationId, string Stage, int Attempt,
    string CustomerRef, decimal RequestedAmount, int TermMonths, DateTime DueBy, int SchemaVersion = 1);

/// <summary>loan.stage.results, sent by stage teams.</summary>
public record StageResult(
    string EventId, string AckId, string? CorrelationId, string Stage, int Attempt,
    string Outcome, string? ReasonCode, DateTime OccurredAt, int SchemaVersion = 1);

/// <summary>loan.notifications. EDoc looks up addresses and names itself.</summary>
public record NotificationMessage(
    string EventId, string AckId, string TemplateId, string[] Recipients, string DealerId,
    string CustomerRef, string? ReasonCategory, string? LoanNumber, int SchemaVersion = 1);

/// <summary>loan.events, published by us when a loan is booked.</summary>
public record LoanBookedEvent(string EventId, string AckId, string LoanNumber, DateTime BookedAt, int SchemaVersion = 1);

/// <summary>loan.events, published by the DBA sync job when servicing has the loan.</summary>
public record SyncCompletedEvent(
    string EventId, string LoanNumber, Dictionary<string, long>? RowCounts, string? Checksum, int SchemaVersion = 1);
