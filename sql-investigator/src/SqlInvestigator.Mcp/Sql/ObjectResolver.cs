using System.Data;
using ModelContextProtocol;

namespace SqlInvestigator.Mcp.Sql;

public sealed record ResolvedObject(int ObjectId, string Schema, string Name, string TypeCode);

/// <summary>
/// Turns an LLM-supplied name ("Orders", "sales.Orders", "[sales].[Orders]") into exactly one object.
/// PARSENAME splits the name safely on the server; the name is only ever a parameter.
/// </summary>
public static class ObjectResolver
{
    /// <param name="typeCodes">Comma-separated sys.objects type codes, e.g. "U,V". Supplied by our code, never by the LLM.</param>
    /// <param name="kind">Human wording for error messages, e.g. "table or view".</param>
    /// <param name="hint">What the LLM should do if nothing matches.</param>
    public static async Task<ResolvedObject> ResolveAsync(
        SqlSession session, string name, string typeCodes, string kind, string hint, CancellationToken cancellationToken)
    {
        await using var cmd = session.CreateCommand("""
            SELECT TOP (10) o.object_id, s.name, o.name, RTRIM(o.type)
            FROM sys.objects AS o
            JOIN sys.schemas AS s ON s.schema_id = o.schema_id
            WHERE CHARINDEX(',' + RTRIM(o.type) + ',', ',' + @types + ',') > 0
              AND o.is_ms_shipped = 0
              AND o.name = PARSENAME(@name, 1)
              AND (PARSENAME(@name, 2) IS NULL OR s.name = PARSENAME(@name, 2))
              AND (PARSENAME(@name, 3) IS NULL OR PARSENAME(@name, 3) = DB_NAME())
              AND PARSENAME(@name, 4) IS NULL
            ORDER BY s.name;
            """);
        cmd.Parameters.Add("@name", SqlDbType.NVarChar, 300).Value = name;
        cmd.Parameters.Add("@types", SqlDbType.VarChar, 50).Value = typeCodes;

        var matches = new List<ResolvedObject>();
        await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                matches.Add(new ResolvedObject(
                    reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
            }
        }

        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new McpException(
                $"No {kind} named '{name}' was found (or you lack permission to see it). {hint}"),
            _ => throw new McpException(
                $"'{name}' is ambiguous. Matches: {string.Join(", ", matches.Select(m => $"{m.Schema}.{m.Name}"))}. " +
                "Call again with a schema-qualified name."),
        };
    }
}
