namespace SqlInvestigator.Mcp.Sql;

/// <summary>Settings for the SQL connection, read from environment variables at startup.</summary>
public sealed class SqlOptions
{
    /// <summary>Hard cap on rows any tool may return.</summary>
    public const int MaxRows = 200;

    /// <summary>Command timeout applied to every query.</summary>
    public const int CommandTimeoutSeconds = 30;

    /// <summary>From MSSQL_CONN. Never hardcode or log this value.</summary>
    public string? ConnectionString { get; init; }

    /// <summary>
    /// From MSSQL_IMPERSONATE_USER (optional). A read-only database user that every
    /// session switches into with EXECUTE AS, so the login's own write rights are not usable.
    /// </summary>
    public string? ImpersonateUser { get; init; }

    /// <summary>From MSSQL_TABLE_ALLOWLIST (optional). Null = run_select may read any table.</summary>
    public TableAllowlist? TableAllowlist { get; init; }
}
