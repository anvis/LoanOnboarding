using System.Text.Json;
using LoanOnboard.Core.Messaging;
using LoanOnboard.Core.Services;
using LoanOnboardService.Auth;
using LoanOnboardService.Models;
using LoanOnboardService.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LoanOnboardService.Controllers;

/// <summary>Onboarding API: validate, persist, return an ACK-ID. All slow work happens after the ACK.</summary>
[ApiController]
[Authorize]
[Route("v1/applications")]
public class OnboardingController(OnboardingService onboarding) : ControllerBase
{
    /// <summary>
    /// Submit an application. Multipart form: field "payload" is JSON (see OnboardingPayload); every file part is a
    /// document and its form field name is the document type, e.g. ID_PROOF.
    /// Requires an Idempotency-Key header. A repeat with the same key returns the same ACK-ID.
    /// </summary>
    [HttpPost]
    [Authorize(Policy = Policies.Submit)]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(60_000_000)]
    [ProducesResponseType<SubmitResponse>(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> Submit(
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromForm] string? payload,
        IFormFileCollection files,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 100)
            return Problem("Idempotency-Key header is required (max 100 characters).", statusCode: 400);
        if (string.IsNullOrWhiteSpace(payload))
            return Problem("Form field 'payload' is required.", statusCode: 400);

        OnboardingPayload? body;
        try
        {
            body = JsonSerializer.Deserialize<OnboardingPayload>(payload, Json.Options);
        }
        catch (JsonException ex)
        {
            return Problem($"Payload is not valid JSON: {ex.Message}", statusCode: 400);
        }
        if (body is null) return Problem("Payload is empty.", statusCode: 400);

        // The caller may only submit for the dealer in its validated token claim.
        var claimDealer = User.GetDealerId();
        if (string.IsNullOrWhiteSpace(claimDealer) || !string.Equals(claimDealer, body.DealerId, StringComparison.Ordinal))
            return Problem("Dealer in the payload does not match the caller's dealer.", statusCode: 403);

        try
        {
            var result = await onboarding.SubmitAsync(claimDealer, idempotencyKey, body, files.ToList(), ct);
            return Accepted($"/v1/applications/{result.AckId}/status", result);
        }
        catch (ValidationFailedException ex)
        {
            return ValidationProblem(new ValidationProblemDetails(ex.Errors));
        }
    }

    /// <summary>Upload documents for an application in NEEDS_INFO. The failed stage runs again.</summary>
    [HttpPost("{ackId}/documents")]
    [Authorize(Policy = Policies.Submit)]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(60_000_000)]
    public async Task<IActionResult> AddDocuments(string ackId, IFormFileCollection files, CancellationToken ct)
    {
        var dealer = User.GetDealerId();
        if (string.IsNullOrWhiteSpace(dealer)) return Problem("Caller has no dealer claim.", statusCode: 403);

        try
        {
            var result = await onboarding.UploadMissingDocumentsAsync(ackId, dealer, files.ToList(), ct);
            return result is null ? NotFound() : Accepted(result);
        }
        catch (ValidationFailedException ex)
        {
            return ValidationProblem(new ValidationProblemDetails(ex.Errors));
        }
        catch (InvalidStateTransitionException)
        {
            return Problem("Documents can only be added while the application is waiting for information.", statusCode: 409);
        }
    }
}
