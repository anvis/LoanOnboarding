using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using LoanOnboard.Core.Common;

namespace LoanOnboard.Core.Services;

public record OutboxRow(long Id, string AckId, string Type, string Topic, string? Stage, string Payload, int Attempts);

/// <summary>
/// Claims and marks outbox rows with plain ADO.NET so several dispatcher instances can run side by side.
/// UPDLOCK + READPAST make a second instance skip rows the first one holds instead of waiting.
/// </summary>
public class OutboxRepository(string connectionString, IOptions<LoanOptions> options)
{
    private readonly LoanOptions _options = options.Value;

    private const string ClaimSql = """
        WITH next AS (
          SELECT TOP (@batch) *
          FROM dbo.Outbox WITH (UPDLOCK, READPAST, ROWLOCK)
          WHERE PublishedAt IS NULL
            AND AvailableAt <= SYSUTCDATETIME()
            AND (LockedUntil IS NULL OR LockedUntil < SYSUTCDATETIME())
          ORDER BY Id
        )
        UPDATE next
        SET LockedUntil = DATEADD(SECOND, @lockSeconds, SYSUTCDATETIME()), Attempts = Attempts + 1
        OUTPUT inserted.Id, inserted.AckId, inserted.Type, inserted.Topic, inserted.Stage, inserted.Payload, inserted.Attempts;
        """;

    public async Task<IReadOnlyList<OutboxRow>> ClaimBatchAsync(CancellationToken ct)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(ClaimSql, conn);
        cmd.Parameters.AddWithValue("@batch", _options.OutboxBatchSize);
        cmd.Parameters.AddWithValue("@lockSeconds", _options.OutboxLockSeconds);

        var rows = new List<OutboxRow>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new OutboxRow(
                reader.GetInt64(0), reader.GetString(1).Trim(), reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5), reader.GetInt32(6)));
        }
        // OUTPUT order is not guaranteed, so restore insertion order.
        return rows.OrderBy(r => r.Id).ToList();
    }

    public async Task MarkPublishedAsync(long id, CancellationToken ct)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(
            "UPDATE dbo.Outbox SET PublishedAt = SYSUTCDATETIME(), LockedUntil = NULL WHERE Id = @id", conn);
        cmd.Parameters.AddWithValue("@id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Age in seconds of the oldest unpublished row. Alert on this value.</summary>
    public async Task<double> OldestPendingAgeSecondsAsync(CancellationToken ct)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(
            "SELECT ISNULL(DATEDIFF(SECOND, MIN(CreatedAt), SYSUTCDATETIME()), 0) FROM dbo.Outbox WHERE PublishedAt IS NULL AND AvailableAt <= SYSUTCDATETIME()",
            conn);
        return Convert.ToDouble(await cmd.ExecuteScalarAsync(ct));
    }
}
