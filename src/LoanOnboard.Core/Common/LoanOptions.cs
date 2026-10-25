namespace LoanOnboard.Core.Common;

/// <summary>Bound from the "Loan" configuration section.</summary>
public class LoanOptions
{
    public const string SectionName = "Loan";

    /// <summary>How long a PASSED stage result stays valid for approval.</summary>
    public int StageValidityHours { get; set; } = 72;

    /// <summary>Approvals above this amount need two different approvers (maker-checker).</summary>
    public decimal SecondApproverThreshold { get; set; } = 50_000m;

    public int MaxStageAttempts { get; set; } = 3;
    public int NeedsInfoExpiryDays { get; set; } = 14;

    public int OutboxBatchSize { get; set; } = 50;
    public int OutboxLockSeconds { get; set; } = 60;

    /// <summary>Key used to HMAC national identifiers for duplicate detection.</summary>
    public string IdentifierHashKey { get; set; } = "change-me-in-configuration";
}
