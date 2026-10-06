namespace SqlInvestigator.Mcp.Sql;

/// <summary>
/// Optional policy for run_select, from MSSQL_TABLE_ALLOWLIST, e.g. "sales.*,dbo.Customers".
/// Only listed tables/views may be queried. This is a second layer: the real enforcement is
/// granting SELECT only on these objects to the read-only database user.
/// </summary>
public sealed class TableAllowlist
{
    /// <summary>Catalog views stay readable so the LLM can still explore metadata.</summary>
    private static readonly HashSet<string> AlwaysAllowedSchemas =
        new(StringComparer.OrdinalIgnoreCase) { "sys", "INFORMATION_SCHEMA" };

    private readonly HashSet<string> _wildcardSchemas = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _tables = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _patterns = [];

    private TableAllowlist()
    {
    }

    /// <summary>Returns null when the setting is empty (feature off). Throws on malformed entries.</summary>
    public static TableAllowlist? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var allowlist = new TableAllowlist();
        foreach (var raw in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var entry = raw.Replace("[", "").Replace("]", "");
            var parts = entry.Split('.');
            if (parts.Length != 2 || parts.Any(string.IsNullOrWhiteSpace))
            {
                throw new ArgumentException(
                    $"Invalid MSSQL_TABLE_ALLOWLIST entry '{raw}'. Use 'schema.table' or 'schema.*'.");
            }

            if (parts[1] == "*")
            {
                allowlist._wildcardSchemas.Add(parts[0]);
            }
            else
            {
                allowlist._tables.Add(entry);
            }

            allowlist._patterns.Add(entry);
        }

        return allowlist._patterns.Count == 0 ? null : allowlist;
    }

    /// <param name="schema">Schema from the query; null/empty means unqualified, assumed 'dbo'.</param>
    public bool IsAllowed(string? schema, string name)
    {
        schema = string.IsNullOrEmpty(schema) ? "dbo" : schema;
        return AlwaysAllowedSchemas.Contains(schema)
            || _wildcardSchemas.Contains(schema)
            || _tables.Contains($"{schema}.{name}");
    }

    public override string ToString() => string.Join(", ", _patterns);
}
