using System.ComponentModel;
using ModelContextProtocol.Server;
using SqlInvestigator.Mcp.Sql;

namespace SqlInvestigator.Mcp.Tools;

[McpServerToolType]
public sealed class ConnectionTools(SqlSessionFactory sessions)
{
    public sealed record ConnectionInfo(
        string ServerName,
        string ProductVersion,
        string Edition,
        string Database,
        string LoginName,
        string EffectiveDbUser,
        bool IsImpersonating,
        IReadOnlyList<string> WritePermissionsDetected,
        string SafetyVerdict);

    [McpServerTool(Name = "test_connection", Title = "Test SQL connection",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Connects to the configured SQL Server database and returns the server version, edition, " +
                 "current database, the login and effective database user, and whether that identity has " +
                 "any database-level write permissions. Call this first to verify connectivity.")]
    public async Task<ConnectionInfo> TestConnection(CancellationToken cancellationToken)
    {
        await using var session = await sessions.OpenAsync(cancellationToken);

        // Fixed query, no user input.
        await using var cmd = session.CreateCommand("""
            SELECT
                CAST(SERVERPROPERTY('ServerName')     AS nvarchar(256)),
                CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(128)),
                CAST(SERVERPROPERTY('Edition')        AS nvarchar(128)),
                DB_NAME(),
                ORIGINAL_LOGIN(),
                USER_NAME();

            -- Database-level permissions the *effective* user holds that could change data or schema.
            SELECT permission_name
            FROM fn_my_permissions(NULL, 'DATABASE')
            WHERE permission_name IN ('INSERT','UPDATE','DELETE','EXECUTE','ALTER','CONTROL',
                                      'CREATE TABLE','CREATE PROCEDURE','CREATE VIEW','CREATE FUNCTION',
                                      'ALTER ANY SCHEMA','TAKE OWNERSHIP')
            ORDER BY permission_name;
            """);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        await reader.ReadAsync(cancellationToken);
        var server = reader.GetString(0);
        var version = reader.GetString(1);
        var edition = reader.GetString(2);
        var database = reader.GetString(3);
        var login = reader.GetString(4);
        var dbUser = reader.GetString(5);

        await reader.NextResultAsync(cancellationToken);
        var writePerms = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            writePerms.Add(reader.GetString(0));
        }

        var verdict = writePerms.Count == 0
            ? "OK: effective user has no database-level write permissions."
            : "WARNING: effective user CAN modify data or schema. Use a read-only login or set MSSQL_IMPERSONATE_USER.";

        return new ConnectionInfo(server, version, edition, database, login, dbUser,
            session.IsImpersonating, writePerms, verdict);
    }
}
