using LoanOnboard.Core.Data;
using LoanOnboard.Core.Domain;
using LoanOnboardService.Auth;
using LoanOnboardService.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LoanOnboardService.Controllers;

/// <summary>Status API for the dealer dashboard. Always reads the latest status by ACK-ID.</summary>
[ApiController]
[Authorize(Policy = Policies.Read)]
public class StatusController(LoanDbContext db) : ControllerBase
{
    [HttpGet("v1/applications/{ackId}/status")]
    [ProducesResponseType<StatusResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetStatus(string ackId, CancellationToken ct)
    {
        var app = await db.Applications.AsNoTracking().FirstOrDefaultAsync(a => a.AckId == ackId, ct);

        // Someone else's application looks exactly like one that does not exist.
        if (app is null || !CanSee(app.DealerId)) return NotFound();

        var timeline = (await db.StatusHistory.AsNoTracking()
                .Where(h => h.AckId == ackId)
                .OrderBy(h => h.OccurredAt).ThenBy(h => h.Id)
                .ToListAsync(ct))
            .Select(h => new TimelineEntry(h.Status, AppStatus.DealerLabel(h.Status), h.Stage, h.OccurredAt))
            .ToList();

        return Ok(new StatusResponse(
            app.AckId, app.Status, AppStatus.DealerLabel(app.Status), app.CurrentStage,
            app.LoanNumber, app.CreatedAt, app.UpdatedAt, timeline));
    }

    [HttpGet("v1/dealers/{dealerId}/applications")]
    [ProducesResponseType<PagedResult<ApplicationSummary>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(
        string dealerId, [FromQuery] string? status, [FromQuery] int page = 1, [FromQuery] int pageSize = 25,
        CancellationToken ct = default)
    {
        if (!CanSee(dealerId)) return NotFound();

        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = db.Applications.AsNoTracking().Where(a => a.DealerId == dealerId);
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(a => a.Status == status);

        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(a => a.CreatedAt)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(a => new { a.AckId, a.Status, a.RequestedAmount, a.CreatedAt, a.UpdatedAt })
            .ToListAsync(ct);

        var result = items.Select(a => new ApplicationSummary(
            a.AckId, a.Status, AppStatus.DealerLabel(a.Status), a.RequestedAmount, a.CreatedAt, a.UpdatedAt)).ToList();
        return Ok(new PagedResult<ApplicationSummary>(result, page, pageSize, total));
    }

    private bool CanSee(string dealerId) =>
        User.IsStaff() || string.Equals(User.GetDealerId(), dealerId, StringComparison.Ordinal);
}
