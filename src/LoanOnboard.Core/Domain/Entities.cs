namespace LoanOnboard.Core.Domain;

/// <summary>Current state of one loan application (table: Application).</summary>
public class LoanApplication
{
    public string AckId { get; set; } = "";
    public string DealerId { get; set; } = "";
    public string IdempotencyKey { get; set; } = "";
    public string ProductType { get; set; } = "DEFAULT";
    public Guid CustomerId { get; set; }
    public decimal RequestedAmount { get; set; }
    public int TermMonths { get; set; }
    public string Status { get; set; } = AppStatus.Received;
    public string? CurrentStage { get; set; }
    public string? LoanNumber { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public class Customer
{
    public Guid CustomerId { get; set; }
    public string AckId { get; set; } = "";
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public string Email { get; set; } = "";
    public string Phone { get; set; } = "";
    public DateOnly DateOfBirth { get; set; }
    /// <summary>HMAC of the national identifier. The raw identifier is never stored.</summary>
    public string IdentifierHash { get; set; } = "";
    public string IdentifierLast4 { get; set; } = "";
}

/// <summary>Append-only audit trail and dealer timeline.</summary>
public class LoanStatusHistory
{
    public long Id { get; set; }
    public string AckId { get; set; } = "";
    public string? Stage { get; set; }
    public string Status { get; set; } = "";
    public string? Reason { get; set; }
    public Guid EventId { get; set; }
    public string CorrelationId { get; set; } = "";
    public DateTime OccurredAt { get; set; }
    public string Source { get; set; } = "";
}

/// <summary>One attempt at one stage. Drives retries, timeouts and DELAYED.</summary>
public class StageExecution
{
    public long Id { get; set; }
    public string AckId { get; set; } = "";
    public string Stage { get; set; } = "";
    public int Attempt { get; set; }
    public string? Outcome { get; set; }
    public string? ReasonCode { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime DueBy { get; set; }
    public DateTime? CompletedAt { get; set; }
}

/// <summary>Stage order as data. Adding a stage is a row, not a code change.</summary>
public class StagePlan
{
    public string ProductType { get; set; } = "DEFAULT";
    public int Sequence { get; set; }
    public string Stage { get; set; } = "";
    /// <summary>Application status while this stage runs, e.g. CREDIT_CHECK.</summary>
    public string StatusName { get; set; } = "";
    public bool Required { get; set; } = true;
    public int DueMinutes { get; set; } = 60;
}

public class ApplicationDocument
{
    public Guid DocId { get; set; }
    public string AckId { get; set; } = "";
    public string Type { get; set; } = "";
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long SizeBytes { get; set; }
    public string BlobPath { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string ScanStatus { get; set; } = "PENDING";
    public DateTime UploadedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
}

/// <summary>Reliable hand-off from SQL to Service Bus.</summary>
public class OutboxMessage
{
    public long Id { get; set; }
    public string AckId { get; set; } = "";
    public string Type { get; set; } = "";
    public string Topic { get; set; } = "";
    public string? Stage { get; set; }
    public string Payload { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    /// <summary>Not published before this time (used for retry backoff).</summary>
    public DateTime AvailableAt { get; set; }
    public DateTime? LockedUntil { get; set; }
    public DateTime? PublishedAt { get; set; }
    public int Attempts { get; set; }
}

/// <summary>Inbox table: makes duplicate deliveries harmless.</summary>
public class ProcessedMessage
{
    public string EventId { get; set; } = "";
    public string Consumer { get; set; } = "";
    public DateTime ProcessedAt { get; set; }
}

public class AgentReview
{
    public long Id { get; set; }
    public string AckId { get; set; } = "";
    public string AgentId { get; set; } = "";
    public string Decision { get; set; } = "";
    public string ChecklistJson { get; set; } = "{}";
    public string ConsentRef { get; set; } = "";
    public string ScriptVersion { get; set; } = "";
    public string CallReference { get; set; } = "";
    public string? Comment { get; set; }
    public DateTime DecidedAt { get; set; }
}
