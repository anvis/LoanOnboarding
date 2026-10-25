using LoanOnboard.Core.Common;
using LoanOnboard.Core.Data;
using LoanOnboard.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LoanOnboard.Core.Services;

/// <summary>Finds stages that missed their DueBy time and applications stuck waiting for documents.</summary>
public class StageSweeper(LoanDbContext db, LoanWorkflow workflow, IOptions<LoanOptions> options, ILogger<StageSweeper> log)
{
    private readonly LoanOptions _options = options.Value;

    public async Task<(int Delayed, int Expired)> SweepAsync(CancellationToken ct)
    {
        var now = workflow.UtcNow;
        var nonStage = AppStatus.NonStage;

        // Stage is running (not DELAYED yet) and the open execution is past its due time.
        var overdue = await db.Applications
            .Where(a => a.CurrentStage != null
                        && !nonStage.Contains(a.Status)
                        && db.StageExecutions.Any(e => e.AckId == a.AckId && e.Stage == a.CurrentStage
                                                       && e.Outcome == null && e.DueBy < now))
            .Take(200)
            .ToListAsync(ct);

        foreach (var app in overdue)
        {
            var stage = app.CurrentStage;
            workflow.SetStatus(app, AppStatus.Delayed, stage, $"Stage {stage} is overdue", "sweeper");
            log.LogWarning("ALERT: {AckId} stage {Stage} is overdue. Notify the owning team.", app.AckId, stage);
        }

        var cutoff = now.AddDays(-_options.NeedsInfoExpiryDays);
        var stale = await db.Applications
            .Where(a => a.Status == AppStatus.NeedsInfo && a.UpdatedAt < cutoff)
            .Take(200)
            .ToListAsync(ct);

        foreach (var app in stale)
            workflow.SetStatus(app, AppStatus.Expired, app.CurrentStage, "No documents received in time", "sweeper");

        if (overdue.Count + stale.Count > 0) await db.SaveChangesAsync(ct);
        return (overdue.Count, stale.Count);
    }
}
