using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;

namespace SqlInvestigator.Mcp.Sql;

/// <summary>
/// The single place where SQL connections are opened. Every tool goes through here,
/// so connection settings and the read-only sandbox are applied consistently.
/// </summary>
public sealed partial class SqlSessionFactory(SqlOptions options, ILogger<SqlSessionFactory> logger)
{
    private const string AppName = "SqlInvestigatorMcp";

    /// <summary>Normal session: runs as the read-only impersonated user when one is configured.</summary>
    public Task<SqlSession> OpenAsync(CancellationToken cancellationToken) =>
        OpenCoreAsync(impersonate: true, cancellationToken);

    /// <summary>
    /// Session for server-level DMVs (blocking, plan cache). EXECUTE AS USER is sandboxed to one
    /// database and hides server state, so this runs as the login itself. ONLY use it for fixed,
    /// read-only queries written in this codebase; never for SQL supplied by the LLM.
    /// </summary>
    public Task<SqlSession> OpenForServerDiagnosticsAsync(CancellationToken cancellationToken) =>
        OpenCoreAsync(impersonate: false, cancellationToken);

    private async Task<SqlSession> OpenCoreAsync(bool impersonate, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            throw new McpException(
                "MSSQL_CONN environment variable is not set. Configure it where the MCP server is registered.");
        }

        SqlConnectionStringBuilder builder;
        try
        {
            builder = new SqlConnectionStringBuilder(options.ConnectionString);
        }
        catch (ArgumentException ex)
        {
            // Don't echo the connection string itself: it may contain a password.
            throw new McpException($"MSSQL_CONN is not a valid connection string: {ex.Message}");
        }

        // Tag our sessions so DBAs can see them in sys.dm_exec_sessions / Profiler.
        if (builder.ApplicationName == new SqlConnectionStringBuilder().ApplicationName)
        {
            builder.ApplicationName = AppName;
        }

        var connection = new SqlConnection(builder.ConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);

            byte[]? cookie = null;
            if (impersonate && !string.IsNullOrWhiteSpace(options.ImpersonateUser))
            {
                cookie = await ImpersonateAsync(connection, options.ImpersonateUser, cancellationToken);
            }

            return new SqlSession(connection, cookie, logger);
        }
        catch (SqlException ex)
        {
            await connection.DisposeAsync();
            logger.LogWarning(ex, "Failed to open SQL session");
            throw new McpException($"Could not open SQL session: {ex.Message}");
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Switches the session to a read-only database user. WITH COOKIE means only we
    /// (holding the cookie) can switch back; a plain REVERT inside a query fails.
    /// </summary>
    private static async Task<byte[]> ImpersonateAsync(SqlConnection connection, string user, CancellationToken ct)
    {
        // SQL Server only allows WITH COOKIE in an ad hoc batch, not via sp_executesql,
        // so this statement cannot be parameterized. The value comes from our own config
        // (not from the LLM), and we still validate it strictly and escape quotes.
        if (!SafeIdentifier().IsMatch(user))
        {
            throw new McpException("MSSQL_IMPERSONATE_USER contains invalid characters.");
        }

        await using var cmd = connection.CreateCommand();
        cmd.CommandTimeout = SqlOptions.CommandTimeoutSeconds;
        cmd.CommandText = $"""
            DECLARE @cookie varbinary(8000);
            EXECUTE AS USER = N'{user.Replace("'", "''")}' WITH COOKIE INTO @cookie;
            SELECT @cookie;
            """;
        return (byte[])(await cmd.ExecuteScalarAsync(ct))!;
    }

    [GeneratedRegex(@"^[A-Za-z0-9_.\-\\]{1,128}$")]
    private static partial Regex SafeIdentifier();
}

/// <summary>An open connection (possibly impersonating a read-only user). Dispose to revert and close.</summary>
public sealed class SqlSession(SqlConnection connection, byte[]? cookie, ILogger logger) : IAsyncDisposable
{
    public SqlConnection Connection => connection;

    public bool IsImpersonating => cookie is not null;

    /// <summary>Creates a command with our standard timeout. Always add values as parameters.</summary>
    public SqlCommand CreateCommand(string sql)
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = SqlOptions.CommandTimeoutSeconds;
        return cmd;
    }

    public async ValueTask DisposeAsync()
    {
        if (cookie is not null && connection.State == System.Data.ConnectionState.Open)
        {
            try
            {
                // Ad hoc batch again (REVERT WITH COOKIE has the same restriction).
                await using var cmd = connection.CreateCommand();
                cmd.CommandText = $"DECLARE @c varbinary(8000) = 0x{Convert.ToHexString(cookie)}; REVERT WITH COOKIE = @c;";
                await cmd.ExecuteNonQueryAsync();
            }
            catch (Exception ex)
            {
                // Couldn't revert: make sure this connection is never reused from the pool.
                logger.LogWarning(ex, "REVERT failed; clearing connection pool");
                SqlConnection.ClearPool(connection);
            }
        }

        await connection.DisposeAsync();
    }
}
