namespace LoanOnboardService.Models;

public class OnboardingPayload
{
    public string DealerId { get; set; } = "";
    public string? ProductType { get; set; }
    public decimal RequestedAmount { get; set; }
    public int TermMonths { get; set; }
    public CustomerPayload Customer { get; set; } = new();
}

public class CustomerPayload
{
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public string Email { get; set; } = "";
    public string Phone { get; set; } = "";
    public DateOnly DateOfBirth { get; set; }
    /// <summary>National identifier. Only an HMAC and the last four characters are stored.</summary>
    public string NationalId { get; set; } = "";
}

public record SubmitResponse(string AckId, string Status, string DealerStatus, bool Replayed);

public record TimelineEntry(string Status, string DealerStatus, string? Stage, DateTime OccurredAt);

public record StatusResponse(
    string AckId, string Status, string DealerStatus, string? CurrentStage, string? LoanNumber,
    DateTime CreatedAt, DateTime UpdatedAt, IReadOnlyList<TimelineEntry> Timeline);

public record ApplicationSummary(
    string AckId, string Status, string DealerStatus, decimal RequestedAmount, DateTime CreatedAt, DateTime UpdatedAt);

public record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total);

public class ApproveRequest
{
    /// <summary>Reference to the stored consent (call recording id, signed form id, ...).</summary>
    public string ConsentRef { get; set; } = "";
    public string ScriptVersion { get; set; } = "";
    public string CallReference { get; set; } = "";
    /// <summary>The terms the agent confirmed with the customer. Must equal what is on the application.</summary>
    public decimal ConfirmedAmount { get; set; }
    public int ConfirmedTermMonths { get; set; }
    public bool DisclosuresDelivered { get; set; }
    public string? Comment { get; set; }
}

public record ApproveResponse(string AckId, string Status, string? LoanNumber, string Message);

public record QueueItem(string AckId, string DealerId, decimal RequestedAmount, int TermMonths, DateTime WaitingSince);
