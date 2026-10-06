using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using SqlInvestigator.Mcp.Sql;

namespace SqlInvestigator.Mcp.Tools;

[McpServerToolType]
public sealed class QueryTools(SqlSessionFactory sessions, SqlOptions options, ILogger<QueryTools> logger)
{
    private const int MaxCellChars = 500;
    private const int MaxTotalChars = 100_000;

    public sealed record ColumnMeta(string Name, string Type);

    public sealed record RunSelectResult(
        IReadOnlyList<ColumnMeta> Columns,
        IReadOnlyList<object?[]> Rows,
        int RowCount,
        bool Truncated,
        string? TruncatedReason,
        long ElapsedMs);

    [McpServerTool(Name = "run_select", Title = "Run read-only SELECT",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Runs ONE read-only SELECT statement (optionally starting with WITH ... AS (...)) and returns " +
                 "columns and rows. Rejected: anything other than a single SELECT, SELECT INTO, EXEC, " +
                 "INSERT/UPDATE/DELETE/MERGE, DDL, OPENROWSET/OPENQUERY, linked-server names, NEXT VALUE FOR, " +
                 "locking hints. Returns at most 200 rows (cells over 500 chars are cut); 30 s timeout. " +
                 "Always select explicit columns and filter with WHERE/TOP; use describe_table first to get " +
                 "exact column names. Runs as a read-only user inside a transaction that is always rolled back. " +
                 "If a table allowlist is configured, only those tables (and sys/INFORMATION_SCHEMA) may be queried; " +
                 "a rejection message lists the allowed tables.")]
    public async Task<RunSelectResult> RunSelect(
        [Description("A single T-SQL SELECT statement, e.g. " +
                     "\"SELECT TOP 20 OrderId, Status FROM sales.Orders WHERE CustomerId = 1 ORDER BY OrderId DESC\".")]
        string sql,
        [Description("Maximum rows to return, 1-200. Default 100.")]
        int maxRows = 100,
        CancellationToken cancellationToken = default)
    {
        // Layer 2: parse and validate before anything reaches the database.
        var rejection = SelectValidator.Validate(sql, options.TableAllowlist);
        if (rejection is not null)
        {
            logger.LogWarning("run_select rejected: {Reason}", rejection);
            throw new McpException($"Query rejected: {rejection}");
        }

        var take = Math.Clamp(maxRows, 1, SqlOptions.MaxRows);

        // Layer 4 (inside OpenAsync): the session runs as the read-only impersonated user.
        await using var session = await sessions.OpenAsync(cancellationToken);

        // Layer 3: everything runs in a transaction that is ALWAYS rolled back.
        var tx = (SqlTransaction)await session.Connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, cancellationToken);
        try
        {
            // Don't sit waiting behind other sessions' locks for the full 30 s.
            await using (var setup = session.CreateCommand("SET LOCK_TIMEOUT 5000;"))
            {
                setup.Transaction = tx;
                await setup.ExecuteNonQueryAsync(cancellationToken);
            }

            await using var cmd = session.CreateCommand(sql);
            cmd.Transaction = tx;

            var stopwatch = Stopwatch.StartNew();
            var columns = new List<ColumnMeta>();
            var rows = new List<object?[]>();
            string? truncatedReason = null;
            var totalChars = 0;

            await using (var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SingleResult, cancellationToken))
            {
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    var name = reader.GetName(i);
                    columns.Add(new ColumnMeta(string.IsNullOrEmpty(name) ? $"column{i + 1}" : name,
                        reader.GetDataTypeName(i)));
                }

                while (await reader.ReadAsync(cancellationToken))
                {
                    if (rows.Count == take)
                    {
                        truncatedReason = $"more than {take} rows; add WHERE/TOP or raise maxRows (max 200)";
                        break;
                    }

                    var row = new object?[reader.FieldCount];
                    for (var i = 0; i < row.Length; i++)
                    {
                        row[i] = ToJsonValue(reader.GetValue(i));
                        totalChars += row[i] is string s ? s.Length : 8;
                    }

                    rows.Add(row);
                    if (totalChars > MaxTotalChars)
                    {
                        truncatedReason = $"result exceeded ~{MaxTotalChars} characters; select fewer columns or rows";
                        break;
                    }
                }

                // If we stopped early, tell the server to stop streaming; otherwise disposing the
                // reader would silently read (and discard) every remaining row.
                if (truncatedReason is not null)
                {
                    cmd.Cancel();
                }
            }

            stopwatch.Stop();
            logger.LogInformation("run_select returned {Rows} rows in {Ms} ms", rows.Count, stopwatch.ElapsedMilliseconds);

            return new RunSelectResult(columns, rows, rows.Count, truncatedReason is not null,
                truncatedReason, stopwatch.ElapsedMilliseconds);
        }
        catch (SqlException ex)
        {
            // SQL errors (bad column, permission denied, timeout) help the LLM fix its query.
            throw new McpException($"SQL error {ex.Number}: {ex.Message}");
        }
        finally
        {
            try
            {
                await tx.RollbackAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                // e.g. the server already rolled back after an error or cancel: nothing left to undo.
                logger.LogDebug(ex, "Rollback after run_select did not run");
            }

            await tx.DisposeAsync();
        }
    }

    /// <summary>Converts SQL values to something JSON-friendly and size-bounded.</summary>
    private static object? ToJsonValue(object value) => value switch
    {
        DBNull => null,
        string s => s.Length <= MaxCellChars ? s : s[..MaxCellChars] + $"...(+{s.Length - MaxCellChars} chars)",
        byte[] b => "0x" + Convert.ToHexString(b, 0, Math.Min(b.Length, 32)) + (b.Length > 32 ? $"...({b.Length} bytes)" : ""),
        bool or byte or short or int or long or decimal or double or float or Guid or DateTime or DateTimeOffset => value,
        _ => value.ToString(), // TimeSpan, spatial types, hierarchyid, sql_variant, ...
    };
}
