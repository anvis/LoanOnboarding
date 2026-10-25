namespace LoanOnboard.Core.Domain;

/// <summary>Application statuses and the allowed-transition rules.</summary>
public static class AppStatus
{
    public const string Received = "RECEIVED";
    public const string Verification = "VERIFICATION";
    public const string CustomerCheck = "CUSTOMER_CHECK";
    public const string CreditCheck = "CREDIT_CHECK";
    public const string Underwriting = "UNDERWRITING";
    public const string PendingAgentReview = "PENDING_AGENT_REVIEW";
    public const string LoanBooked = "LOAN_BOOKED";
    public const string Synced = "SYNCED";
    public const string Active = "ACTIVE";
    public const string Rejected = "REJECTED";
    public const string FailedTechnical = "FAILED_TECHNICAL";
    public const string Expired = "EXPIRED";
    public const string NeedsInfo = "NEEDS_INFO";
    public const string Delayed = "DELAYED";

    /// <summary>Everything that is not "a stage is running". Stage statuses come from StagePlan.</summary>
    public static readonly string[] NonStage =
    [
        Received, NeedsInfo, Delayed, PendingAgentReview, LoanBooked, Synced, Active,
        Rejected, FailedTechnical, Expired
    ];

    public static readonly string[] Terminal = [Rejected, FailedTechnical, Expired, Active];
    public static readonly string[] DeadEnds = [Rejected, FailedTechnical, Expired];

    public static bool IsStageStatus(string status) => !NonStage.Contains(status);

    public static bool CanMove(string from, string to)
    {
        if (Terminal.Contains(from)) return false;

        if (from == Received)
            return IsStageStatus(to) || to is PendingAgentReview or Expired;

        if (IsStageStatus(from) || from == Delayed)
            return IsStageStatus(to) || to is PendingAgentReview or Rejected or NeedsInfo
                or FailedTechnical or Delayed or Expired;

        return from switch
        {
            NeedsInfo => IsStageStatus(to) || to == Expired,
            PendingAgentReview => to is LoanBooked or Rejected or Expired,
            LoanBooked => to == Synced,
            Synced => to == Active,
            _ => false
        };
    }

    /// <summary>Simple label for the dealer dashboard. Technical states are not exposed.</summary>
    public static string DealerLabel(string status) => status switch
    {
        Received => "Received",
        PendingAgentReview => "Final review",
        NeedsInfo => "Action required",
        Delayed => "In review (delayed)",
        LoanBooked or Synced => "Approved",
        Active => "Active",
        Rejected => "Declined",
        FailedTechnical => "In review",
        Expired => "Expired",
        _ => "In review"
    };
}

public static class StageOutcome
{
    public const string Passed = "PASSED";
    public const string Rejected = "REJECTED";
    public const string NeedsInfo = "NEEDS_INFO";
    public const string Error = "ERROR";

    public static readonly string[] All = [Passed, Rejected, NeedsInfo, Error];
}

public static class ScanStatus
{
    public const string Pending = "PENDING";
    public const string Clean = "CLEAN";
    public const string Infected = "INFECTED";
}
