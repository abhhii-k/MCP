using System.ComponentModel;
using System.Data;
using Microsoft.Data.SqlClient;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using SqlInvestigator.Mcp.Sql;

namespace SqlInvestigator.Mcp.Tools;

/// <summary>
/// Live server diagnostics from DMVs. These tools only run the fixed queries below and
/// therefore use the non-impersonated diagnostics session (see SqlSessionFactory).
/// </summary>
[McpServerToolType]
public sealed class DiagnosticsTools(SqlSessionFactory sessions)
{
    private const int MaxSqlTextChars = 1000;

    // ---------------------------------------------------------------- blocking_sessions

    public sealed record BlockingSession(
        int SessionId,
        int? BlockedBy,
        string Role,
        int SessionsBlocked,
        string Status,
        string? Command,
        string? WaitType,
        long? WaitTimeMs,
        string? WaitResource,
        string? Database,
        string LoginName,
        string? HostName,
        string? ProgramName,
        int OpenTransactions,
        DateTime? StartedAt,
        string? SqlText);

    public sealed record BlockingResult(
        string Summary,
        int Count,
        bool Truncated,
        IReadOnlyList<BlockingSession> Sessions);

    [McpServerTool(Name = "blocking_sessions", Title = "Current blocking",
        ReadOnly = true, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Shows sessions that are blocked or blocking others RIGHT NOW: head blockers first, then " +
                 "blocked sessions with wait type, wait time, wait resource and SQL text (current statement, or " +
                 "the last batch for idle sessions). A head blocker with status 'sleeping' and OpenTransactions > 0 " +
                 "usually means an application left a transaction open. Empty result = no blocking at this moment. " +
                 "Requires VIEW SERVER STATE (SQL Server) or VIEW DATABASE STATE (Azure SQL Database).")]
    public async Task<BlockingResult> BlockingSessions(CancellationToken cancellationToken)
    {
        await using var session = await sessions.OpenForServerDiagnosticsAsync(cancellationToken);
        await EnsureServerStatePermissionAsync(session, cancellationToken);

        await using var cmd = session.CreateCommand("""
            WITH blockers AS (
                SELECT DISTINCT blocking_session_id AS session_id
                FROM sys.dm_exec_requests
                WHERE blocking_session_id > 0
            )
            SELECT TOP (@max + 1)
                s.session_id,
                NULLIF(r.blocking_session_id, 0),
                CASE WHEN r.blocking_session_id > 0 AND b.session_id IS NOT NULL THEN 'blocked and blocking'
                     WHEN r.blocking_session_id > 0 THEN 'blocked'
                     ELSE 'head blocker' END,
                (SELECT COUNT(*) FROM sys.dm_exec_requests AS x WHERE x.blocking_session_id = s.session_id) AS sessions_blocked,
                s.status,
                r.command,
                r.wait_type,
                r.wait_time,
                r.wait_resource,
                DB_NAME(COALESCE(r.database_id, s.database_id)),
                s.login_name,
                s.host_name,
                s.program_name,
                s.open_transaction_count,
                COALESCE(r.start_time, s.last_request_start_time),
                CASE WHEN r.sql_handle IS NOT NULL
                     THEN SUBSTRING(rt.text, r.statement_start_offset / 2 + 1,
                          (IIF(r.statement_end_offset = -1, DATALENGTH(rt.text), r.statement_end_offset)
                           - r.statement_start_offset) / 2 + 1)
                     ELSE lt.text END
            FROM sys.dm_exec_sessions AS s
            LEFT JOIN sys.dm_exec_requests AS r ON r.session_id = s.session_id
            LEFT JOIN blockers AS b ON b.session_id = s.session_id
            OUTER APPLY (SELECT TOP (1) c.most_recent_sql_handle
                         FROM sys.dm_exec_connections AS c
                         WHERE c.session_id = s.session_id AND c.parent_connection_id IS NULL) AS c
            OUTER APPLY sys.dm_exec_sql_text(r.sql_handle) AS rt
            OUTER APPLY sys.dm_exec_sql_text(c.most_recent_sql_handle) AS lt
            WHERE r.blocking_session_id > 0 OR b.session_id IS NOT NULL
            ORDER BY IIF(r.blocking_session_id > 0, 1, 0), sessions_blocked DESC, r.wait_time DESC;
            """);
        cmd.Parameters.Add("@max", SqlDbType.Int).Value = SqlOptions.MaxRows;

        var rows = new List<BlockingSession>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new BlockingSession(
                SessionId: Int(reader, 0)!.Value,
                BlockedBy: Int(reader, 1),
                Role: reader.GetString(2),
                SessionsBlocked: Int(reader, 3)!.Value,
                Status: reader.GetString(4),
                Command: Str(reader, 5),
                WaitType: Str(reader, 6),
                WaitTimeMs: Int(reader, 7),
                WaitResource: Str(reader, 8),
                Database: Str(reader, 9),
                LoginName: reader.GetString(10),
                HostName: Str(reader, 11),
                ProgramName: Str(reader, 12),
                OpenTransactions: Int(reader, 13) ?? 0,
                StartedAt: reader.IsDBNull(14) ? null : reader.GetDateTime(14),
                SqlText: Truncate(Str(reader, 15))));
        }

        var truncated = SchemaTools.TrimToLimit(rows, SqlOptions.MaxRows);
        var heads = rows.Count(r => r.Role == "head blocker");
        var summary = rows.Count == 0
            ? "No blocking at this moment."
            : $"{heads} head blocker(s), {rows.Count - heads} blocked session(s).";

        return new BlockingResult(summary, rows.Count, truncated, rows);
    }

    // ---------------------------------------------------------------- slow_queries

    private static readonly string[] SortOptions =
        ["avg_duration", "total_duration", "avg_cpu", "avg_reads", "executions"];

    public sealed record SlowQuery(
        string? ObjectName,
        long ExecutionCount,
        double AvgDurationMs,
        double MaxDurationMs,
        double TotalDurationMs,
        double AvgCpuMs,
        long AvgLogicalReads,
        long AvgRows,
        DateTime LastExecution,
        DateTime CachedSince,
        string QueryText);

    public sealed record SlowQueriesResult(
        string Source,
        string OrderedBy,
        int Count,
        IReadOnlyList<SlowQuery> Queries);

    [McpServerTool(Name = "slow_queries", Title = "Most expensive queries",
        ReadOnly = true, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Lists the most expensive statements for the CURRENT database from the plan cache " +
                 "(sys.dm_exec_query_stats): execution count, average/max/total duration, CPU, logical reads, " +
                 "rows, and the owning procedure if any. Statistics only cover the time since each plan was " +
                 "cached (lost on restart or plan eviction). Use 'textFilter' to focus on a table or procedure. " +
                 "Requires VIEW SERVER STATE (SQL Server) or VIEW DATABASE STATE (Azure SQL Database).")]
    public async Task<SlowQueriesResult> SlowQueries(
        [Description("Sort order: 'avg_duration' (default), 'total_duration', 'avg_cpu', 'avg_reads' or 'executions'.")]
        string orderBy = "avg_duration",
        [Description("Optional text that must appear in the query or procedure source, e.g. 'sales.Orders'.")]
        string? textFilter = null,
        [Description("Number of statements to return, 1-100. Default 20.")]
        int top = 20,
        CancellationToken cancellationToken = default)
    {
        orderBy = orderBy.Trim().ToLowerInvariant();
        if (!SortOptions.Contains(orderBy))
        {
            throw new McpException($"Unknown orderBy '{orderBy}'. Use one of: {string.Join(", ", SortOptions)}.");
        }

        if (textFilter is { Length: > 200 })
        {
            throw new McpException("textFilter must be 200 characters or fewer.");
        }

        await using var session = await sessions.OpenForServerDiagnosticsAsync(cancellationToken);
        await EnsureServerStatePermissionAsync(session, cancellationToken);

        // The sort column is chosen with CASE on a parameter: no dynamic SQL needed.
        await using var cmd = session.CreateCommand("""
            SELECT TOP (@top)
                OBJECT_SCHEMA_NAME(st.objectid, st.dbid) + '.' + OBJECT_NAME(st.objectid, st.dbid),
                qs.execution_count,
                qs.total_elapsed_time / 1000.0 / qs.execution_count,
                qs.max_elapsed_time / 1000.0,
                qs.total_elapsed_time / 1000.0,
                qs.total_worker_time / 1000.0 / qs.execution_count,
                qs.total_logical_reads / qs.execution_count,
                qs.total_rows / qs.execution_count,
                qs.last_execution_time,
                qs.creation_time,
                SUBSTRING(st.text, qs.statement_start_offset / 2 + 1,
                          (IIF(qs.statement_end_offset = -1, DATALENGTH(st.text), qs.statement_end_offset)
                           - qs.statement_start_offset) / 2 + 1)
            FROM sys.dm_exec_query_stats AS qs
            CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) AS st
            CROSS APPLY (SELECT CONVERT(int, pa.value) AS dbid
                         FROM sys.dm_exec_plan_attributes(qs.plan_handle) AS pa
                         WHERE pa.attribute = 'dbid') AS pdb
            WHERE pdb.dbid = DB_ID()
              AND (@filter IS NULL OR CHARINDEX(@filter, st.text) > 0)
              AND st.text NOT LIKE N'%sys.dm[_]exec[_]%'   -- hide DMV queries (incl. these tools' own)
            ORDER BY CASE @orderBy
                         WHEN 'avg_duration'   THEN qs.total_elapsed_time * 1.0 / qs.execution_count
                         WHEN 'total_duration' THEN qs.total_elapsed_time * 1.0
                         WHEN 'avg_cpu'        THEN qs.total_worker_time * 1.0 / qs.execution_count
                         WHEN 'avg_reads'      THEN qs.total_logical_reads * 1.0 / qs.execution_count
                         WHEN 'executions'     THEN qs.execution_count * 1.0
                     END DESC;
            """);
        cmd.Parameters.Add("@top", SqlDbType.Int).Value = Math.Clamp(top, 1, 100);
        cmd.Parameters.Add("@orderBy", SqlDbType.VarChar, 20).Value = orderBy;
        cmd.Parameters.Add("@filter", SqlDbType.NVarChar, 200).Value =
            string.IsNullOrWhiteSpace(textFilter) ? DBNull.Value : textFilter.Trim();

        var queries = new List<SlowQuery>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            queries.Add(new SlowQuery(
                ObjectName: Str(reader, 0),
                ExecutionCount: reader.GetInt64(1),
                AvgDurationMs: Ms(reader, 2),
                MaxDurationMs: Ms(reader, 3),
                TotalDurationMs: Ms(reader, 4),
                AvgCpuMs: Ms(reader, 5),
                AvgLogicalReads: reader.GetInt64(6),
                AvgRows: reader.GetInt64(7),
                LastExecution: reader.GetDateTime(8),
                CachedSince: reader.GetDateTime(9),
                QueryText: Truncate(Str(reader, 10)) ?? ""));
        }

        return new SlowQueriesResult("plan cache (sys.dm_exec_query_stats)", orderBy, queries.Count, queries);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// Without the right permission many DMVs return only your own session instead of failing,
    /// which would look like "no problems". So check explicitly and explain what is missing.
    /// </summary>
    private static async Task EnsureServerStatePermissionAsync(SqlSession session, CancellationToken cancellationToken)
    {
        await using var cmd = session.CreateCommand("""
            SELECT CAST(CASE
                WHEN CAST(SERVERPROPERTY('EngineEdition') AS int) = 5      -- Azure SQL Database
                    THEN HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'VIEW DATABASE STATE')
                WHEN HAS_PERMS_BY_NAME(NULL, NULL, 'VIEW SERVER STATE') = 1 THEN 1
                WHEN COALESCE(HAS_PERMS_BY_NAME(NULL, NULL, 'VIEW SERVER PERFORMANCE STATE'), 0) = 1 THEN 1  -- SQL 2022+
                ELSE 0 END AS bit);
            """);
        if (await cmd.ExecuteScalarAsync(cancellationToken) is not true)
        {
            throw new McpException(
                "Missing permission to read server state. The connecting login needs VIEW SERVER STATE " +
                "(SQL Server 2019), VIEW SERVER PERFORMANCE STATE (SQL Server 2022+) or VIEW DATABASE STATE " +
                "(Azure SQL Database). Ask a DBA, e.g.: GRANT VIEW SERVER STATE TO [your_login];");
        }
    }

    private static string? Str(SqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static int? Int(SqlDataReader r, int i) => r.IsDBNull(i) ? null : Convert.ToInt32(r.GetValue(i));

    private static double Ms(SqlDataReader r, int i) => Math.Round(Convert.ToDouble(r.GetValue(i)), 2);

    private static string? Truncate(string? s) =>
        s is null || s.Length <= MaxSqlTextChars ? s?.Trim() : s[..MaxSqlTextChars].Trim() + " ...";
}
