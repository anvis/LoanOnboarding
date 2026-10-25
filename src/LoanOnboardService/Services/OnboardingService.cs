using System.Text.RegularExpressions;
using LoanOnboard.Core.Common;
using LoanOnboard.Core.Data;
using LoanOnboard.Core.Domain;
using LoanOnboard.Core.Services;
using LoanOnboardService.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace LoanOnboardService.Services;

public class ValidationFailedException(IDictionary<string, string[]> errors) : Exception("Validation failed")
{
    public IDictionary<string, string[]> Errors { get; } = errors;
}

public class OnboardingService(
    LoanDbContext db, IDocumentStore store, LoanWorkflow workflow,
    IOptions<LoanOptions> options, IConfiguration config, ILogger<OnboardingService> log)
{
    private static readonly Regex DocTypePattern = new("^[A-Z][A-Z0-9_]{2,39}$", RegexOptions.Compiled);
    private static readonly Regex EmailPattern = new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled);
    private readonly LoanOptions _options = options.Value;

    public async Task<SubmitResponse> SubmitAsync(
        string dealerId, string idempotencyKey, OnboardingPayload payload,
        IReadOnlyList<IFormFile> files, CancellationToken ct)
    {
        Validate(payload, files);

        // A retry with the same key returns the original ACK-ID instead of creating a second application.
        var existing = await FindByKeyAsync(dealerId, idempotencyKey, ct);
        if (existing is not null) return ToResponse(existing, replayed: true);

        var now = workflow.UtcNow;
        var ackId = AckIdGenerator.New(now);
        var customerId = Guid.NewGuid();

        var app = new LoanApplication
        {
            AckId = ackId,
            DealerId = dealerId,
            IdempotencyKey = idempotencyKey,
            ProductType = string.IsNullOrWhiteSpace(payload.ProductType) ? "DEFAULT" : payload.ProductType!,
            CustomerId = customerId,
            RequestedAmount = payload.RequestedAmount,
            TermMonths = payload.TermMonths,
            Status = AppStatus.Received,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Applications.Add(app);

        var c = payload.Customer;
        db.Customers.Add(new Customer
        {
            CustomerId = customerId,
            AckId = ackId,
            FirstName = c.FirstName.Trim(),
            LastName = c.LastName.Trim(),
            Email = c.Email.Trim(),
            Phone = c.Phone.Trim(),
            DateOfBirth = c.DateOfBirth,
            IdentifierHash = IdentifierHasher.Hash(c.NationalId, _options.IdentifierHashKey),
            IdentifierLast4 = IdentifierHasher.Last4(c.NationalId)
        });

        await AddDocumentsAsync(dealerId, ackId, files, ct);

        workflow.AddHistory(app, "Application received", "api");
        await workflow.ScheduleNextAsync(app, afterStage: null, source: "api", ct);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // Two concurrent requests with the same key: the unique index lets one win. Return the winner's ACK-ID.
            db.ChangeTracker.Clear();
            var winner = await FindByKeyAsync(dealerId, idempotencyKey, ct);
            if (winner is null) throw;
            log.LogInformation(ex, "Concurrent duplicate submit for key {Key}; returning {AckId}", idempotencyKey, winner.AckId);
            return ToResponse(winner, replayed: true);
        }

        log.LogInformation("Application {AckId} accepted for dealer {DealerId}", ackId, dealerId);
        return ToResponse(app, replayed: false);
    }

    /// <summary>Dealer uploads missing documents for an application in NEEDS_INFO. The stage is re-run.</summary>
    public async Task<SubmitResponse?> UploadMissingDocumentsAsync(
        string ackId, string? dealerScope, IReadOnlyList<IFormFile> files, CancellationToken ct)
    {
        if (files.Count == 0)
            throw new ValidationFailedException(new Dictionary<string, string[]> { ["files"] = ["At least one document is required."] });

        var app = await db.Applications.FirstOrDefaultAsync(a => a.AckId == ackId, ct);
        if (app is null || (dealerScope is not null && app.DealerId != dealerScope)) return null;

        if (app.Status != AppStatus.NeedsInfo)
            throw new InvalidStateTransitionException(app.Status, "document upload");

        await AddDocumentsAsync(app.DealerId, ackId, files, ct);

        var stage = app.CurrentStage!;
        var plan = await db.StagePlans.AsNoTracking()
            .FirstAsync(p => p.ProductType == app.ProductType && p.Stage == stage, ct);
        var lastAttempt = await db.StageExecutions.Where(e => e.AckId == ackId && e.Stage == stage)
            .MaxAsync(e => (int?)e.Attempt, ct) ?? 0;

        workflow.RequestStage(app, plan, lastAttempt + 1, TimeSpan.Zero, "api");
        await db.SaveChangesAsync(ct);
        return ToResponse(app, replayed: false);
    }

    private async Task AddDocumentsAsync(string dealerId, string ackId, IReadOnlyList<IFormFile> files, CancellationToken ct)
    {
        foreach (var file in files)
        {
            var docType = file.Name.ToUpperInvariant();
            if (!DocTypePattern.IsMatch(docType))
                throw new ValidationFailedException(new Dictionary<string, string[]>
                {
                    ["files"] = [$"Form field name '{file.Name}' is not a valid document type. Use e.g. ID_PROOF."]
                });

            StoredDocument stored;
            try
            {
                stored = await store.SaveAsync(dealerId, ackId, docType, file, ct);
            }
            catch (DocumentRejectedException ex)
            {
                throw new ValidationFailedException(new Dictionary<string, string[]> { ["files"] = [ex.Message] });
            }

            db.Documents.Add(new ApplicationDocument
            {
                DocId = Guid.NewGuid(),
                AckId = ackId,
                Type = stored.DocType,
                FileName = stored.FileName,
                ContentType = stored.ContentType,
                SizeBytes = stored.SizeBytes,
                BlobPath = stored.BlobPath,
                Sha256 = stored.Sha256,
                // The store already verified size, declared type and file signature.
                ScanStatus = ScanStatus.Clean,
                UploadedAt = workflow.UtcNow
            });
        }
    }

    private void Validate(OnboardingPayload p, IReadOnlyList<IFormFile> files)
    {
        var errors = new Dictionary<string, string[]>();
        void Fail(string key, string message) => errors[key] = [message];

        if (string.IsNullOrWhiteSpace(p.DealerId)) Fail(nameof(p.DealerId), "Required.");
        if (p.RequestedAmount <= 0 || p.RequestedAmount > 10_000_000m) Fail(nameof(p.RequestedAmount), "Must be between 0 and 10,000,000.");
        if (p.TermMonths is < 1 or > 120) Fail(nameof(p.TermMonths), "Must be between 1 and 120.");
        if (string.IsNullOrWhiteSpace(p.Customer.FirstName)) Fail("Customer.FirstName", "Required.");
        if (string.IsNullOrWhiteSpace(p.Customer.LastName)) Fail("Customer.LastName", "Required.");
        if (!EmailPattern.IsMatch(p.Customer.Email ?? "")) Fail("Customer.Email", "Invalid email address.");
        if (string.IsNullOrWhiteSpace(p.Customer.Phone)) Fail("Customer.Phone", "Required.");
        if (string.IsNullOrWhiteSpace(p.Customer.NationalId)) Fail("Customer.NationalId", "Required.");
        if (p.Customer.DateOfBirth > DateOnly.FromDateTime(workflow.UtcNow.AddYears(-18)))
            Fail("Customer.DateOfBirth", "Applicant must be at least 18.");
        var min = config.GetValue("Documents:MinRequired", 1);
        if (files.Count < min) Fail("files", $"At least {min} document(s) required.");

        if (errors.Count > 0) throw new ValidationFailedException(errors);
    }

    private Task<LoanApplication?> FindByKeyAsync(string dealerId, string key, CancellationToken ct) =>
        db.Applications.AsNoTracking().FirstOrDefaultAsync(a => a.DealerId == dealerId && a.IdempotencyKey == key, ct);

    private static SubmitResponse ToResponse(LoanApplication app, bool replayed) =>
        new(app.AckId, app.Status, AppStatus.DealerLabel(app.Status), replayed);
}
