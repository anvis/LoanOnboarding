using LoanOnboardService.Auth;
using LoanOnboardService.Models;
using LoanOnboardService.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LoanOnboardService.Controllers;

/// <summary>Approval API for the agent console.</summary>
[ApiController]
public class ApprovalController(ApprovalService approvals) : ControllerBase
{
    /// <summary>Applications waiting for an agent, oldest first.</summary>
    [HttpGet("v1/approvals/queue")]
    [Authorize(Policy = Policies.Agent)]
    public async Task<IActionResult> Queue([FromQuery] int take = 50, CancellationToken ct = default) =>
        Ok(await approvals.GetQueueAsync(take, ct));

    /// <summary>
    /// Approve and book the loan. All rules are re-checked here: stages passed and fresh, documents clean and unexpired,
    /// consent evidence, confirmed terms, no duplicate application, and a second approver above the threshold.
    /// </summary>
    [HttpPost("v1/applications/{ackId}/approve")]
    [Authorize(Policy = Policies.Approver)]
    [ProducesResponseType<ApproveResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Approve(string ackId, [FromBody] ApproveRequest request, CancellationToken ct)
    {
        var outcome = await approvals.ApproveAsync(ackId, User.GetUserId(), request, ct);

        return outcome switch
        {
            ApprovalOutcome.NotFound => NotFound(),
            ApprovalOutcome.Conflict c => Problem(c.Message, statusCode: StatusCodes.Status409Conflict),
            ApprovalOutcome.ChecksFailed f => UnprocessableEntity(new { title = "Approval checks failed", failures = f.Failures }),
            ApprovalOutcome.AwaitingSecondApprover a => Accepted(new ApproveResponse(
                a.AckId, "PENDING_AGENT_REVIEW", null, "First approval recorded. A second, different approver must approve.")),
            ApprovalOutcome.Approved ok => Ok(new ApproveResponse(
                ok.AckId, "LOAN_BOOKED", ok.LoanNumber, ok.AlreadyBooked ? "Already booked." : "Loan booked.")),
            _ => Problem("Unexpected result.", statusCode: 500)
        };
    }
}
