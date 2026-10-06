using System.ComponentModel;
using System.Data;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using SqlInvestigator.Mcp.Sql;

namespace SqlInvestigator.Mcp.Tools;

[McpServerToolType]
public sealed class SchemaTools(SqlSessionFactory sessions)
{
    public sealed record TableInfo(
        string Schema,
        string Name,
        string Type,
        long? ApproxRowCount,
        DateTime ModifiedAt);

    public sealed record ListTablesResult(
        int Count,
        bool Truncated,
        IReadOnlyList<TableInfo> Tables);

    [McpServerTool(Name = "list_tables", Title = "List tables",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Lists user tables (and optionally views) in the current database with schema, name, " +
                 "approximate row count and last schema-modification time. Use 'filter' to narrow results " +
                 "by a case-insensitive substring of 'schema.name' (e.g. 'order' or 'sales.'). " +
                 "Returns at most 200 rows; if 'truncated' is true, refine the filter.")]
    public async Task<ListTablesResult> ListTables(
        [Description("Optional substring to match against 'schema.name', e.g. 'order'. Wildcards are not needed.")]
        string? filter = null,
        [Description("Also include views. Default false.")]
        bool includeViews = false,
        [Description("Maximum rows to return, 1-200. Default 200.")]
        int limit = SqlOptions.MaxRows,
        CancellationToken cancellationToken = default)
    {
        if (filter is { Length: > 128 })
        {
            throw new McpException("filter must be 128 characters or fewer.");
        }

        var take = Math.Clamp(limit, 1, SqlOptions.MaxRows);

        await using var session = await sessions.OpenAsync(cancellationToken);

        // Fixed SQL; the LLM's input only ever arrives as typed parameters.
        // CHARINDEX = plain substring search, so '%' or '_' in the filter have no special meaning.
        // Row counts come from sys.partitions (metadata, approximate, no table scan).
        await using var cmd = session.CreateCommand("""
            SELECT TOP (@take + 1)
                s.name, o.name, o.type_desc, rc.row_count, o.modify_date
            FROM sys.objects AS o
            JOIN sys.schemas AS s ON s.schema_id = o.schema_id
            OUTER APPLY (
                SELECT SUM(p.rows) AS row_count
                FROM sys.partitions AS p
                WHERE p.object_id = o.object_id AND p.index_id IN (0, 1)
            ) AS rc
            WHERE o.is_ms_shipped = 0
              AND (o.type = 'U' OR (@includeViews = 1 AND o.type = 'V'))
              AND (@filter IS NULL OR CHARINDEX(@filter, s.name + N'.' + o.name) > 0)
            ORDER BY s.name, o.name;
            """);
        cmd.Parameters.Add("@take", SqlDbType.Int).Value = take;
        cmd.Parameters.Add("@includeViews", SqlDbType.Bit).Value = includeViews;
        cmd.Parameters.Add("@filter", SqlDbType.NVarChar, 128).Value =
            string.IsNullOrWhiteSpace(filter) ? DBNull.Value : filter.Trim();

        var tables = new List<TableInfo>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            tables.Add(new TableInfo(
                Schema: reader.GetString(0),
                Name: reader.GetString(1),
                Type: reader.GetString(2) == "USER_TABLE" ? "table" : "view",
                ApproxRowCount: reader.IsDBNull(3) ? null : reader.GetInt64(3),
                ModifiedAt: reader.GetDateTime(4)));
        }

        var truncated = TrimToLimit(tables, take);
        return new ListTablesResult(tables.Count, truncated, tables);
    }

    // ---------------------------------------------------------------- describe_table

    public sealed record ColumnInfo(
        int Ordinal,
        string Name,
        string DataType,
        bool Nullable,
        bool IsPrimaryKey,
        bool IsIdentity,
        string? Default,
        string? ComputedAs);

    public sealed record IndexInfo(
        string Name,
        string Type,
        bool IsUnique,
        bool IsPrimaryKey,
        string? KeyColumns,
        string? IncludedColumns,
        string? Filter);

    public sealed record ForeignKeyInfo(
        string Name,
        string Direction,
        string FromTable,
        string FromColumns,
        string ToTable,
        string ToColumns,
        string OnDelete,
        string OnUpdate,
        bool IsDisabled);

    public sealed record DescribeTableResult(
        string Schema,
        string Name,
        string Type,
        bool Truncated,
        IReadOnlyList<ColumnInfo> Columns,
        IReadOnlyList<IndexInfo> Indexes,
        IReadOnlyList<ForeignKeyInfo> ForeignKeys);

    [McpServerTool(Name = "describe_table", Title = "Describe table",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Describes one table or view: columns (type, nullability, primary key, identity, default, " +
                 "computed expression), indexes (key and included columns, uniqueness, filter) and foreign keys " +
                 "in both directions ('outgoing' = this table references another, 'incoming' = another table " +
                 "references this one). Call list_tables first if you don't know the exact name.")]
    public async Task<DescribeTableResult> DescribeTable(
        [Description("Table or view name, ideally schema-qualified, e.g. 'sales.Orders'. " +
                     "Brackets are allowed: '[sales].[Orders]'. An unqualified name works if it is unique.")]
        string table,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(table) || table.Length > 300)
        {
            throw new McpException("table must be a non-empty name of at most 300 characters.");
        }

        await using var session = await sessions.OpenAsync(cancellationToken);

        var obj = await ObjectResolver.ResolveAsync(session, table.Trim(), "U,V", "table or view",
            "Use list_tables to find the exact name.", cancellationToken);
        var (objectId, schema, name) = (obj.ObjectId, obj.Schema, obj.Name);
        var type = obj.TypeCode == "U" ? "table" : "view";

        // One round trip, three result sets. The object id is a typed parameter.
        await using var cmd = session.CreateCommand("""
            -- 1) Columns
            SELECT TOP (@max + 1)
                c.column_id,
                c.name,
                CASE
                    WHEN t.name IN ('varchar','char','varbinary','binary')
                        THEN t.name + '(' + IIF(c.max_length = -1, 'max', CAST(c.max_length AS varchar(10))) + ')'
                    WHEN t.name IN ('nvarchar','nchar')
                        THEN t.name + '(' + IIF(c.max_length = -1, 'max', CAST(c.max_length / 2 AS varchar(10))) + ')'
                    WHEN t.name IN ('decimal','numeric')
                        THEN t.name + '(' + CAST(c.precision AS varchar(3)) + ',' + CAST(c.scale AS varchar(3)) + ')'
                    WHEN t.name IN ('datetime2','time','datetimeoffset')
                        THEN t.name + '(' + CAST(c.scale AS varchar(3)) + ')'
                    ELSE t.name
                END,
                c.is_nullable,
                CAST(IIF(pk.column_id IS NULL, 0, 1) AS bit),
                c.is_identity,
                dc.definition,
                cc.definition
            FROM sys.columns AS c
            JOIN sys.types AS t ON t.user_type_id = c.user_type_id
            LEFT JOIN sys.default_constraints AS dc ON dc.object_id = c.default_object_id
            LEFT JOIN sys.computed_columns AS cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id
            LEFT JOIN (
                SELECT ic.column_id
                FROM sys.indexes AS i
                JOIN sys.index_columns AS ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                WHERE i.object_id = @id AND i.is_primary_key = 1
            ) AS pk ON pk.column_id = c.column_id
            WHERE c.object_id = @id
            ORDER BY c.column_id;

            -- 2) Indexes (heaps, type 0, have no name and are skipped)
            SELECT TOP (@max + 1)
                i.name,
                i.type_desc,
                i.is_unique,
                i.is_primary_key,
                (SELECT STRING_AGG(c.name + IIF(ic.is_descending_key = 1, ' DESC', ''), ', ')
                        WITHIN GROUP (ORDER BY ic.key_ordinal)
                 FROM sys.index_columns AS ic
                 JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                 WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id
                   AND ic.is_included_column = 0 AND ic.key_ordinal > 0),
                (SELECT STRING_AGG(c.name, ', ') WITHIN GROUP (ORDER BY ic.index_column_id)
                 FROM sys.index_columns AS ic
                 JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                 WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id
                   AND ic.is_included_column = 1),
                i.filter_definition
            FROM sys.indexes AS i
            WHERE i.object_id = @id AND i.type > 0
            ORDER BY i.index_id;

            -- 3) Foreign keys, both directions
            SELECT TOP (@max + 1)
                fk.name,
                IIF(fk.parent_object_id = @id, 'outgoing', 'incoming'),
                OBJECT_SCHEMA_NAME(fk.parent_object_id) + '.' + OBJECT_NAME(fk.parent_object_id),
                (SELECT STRING_AGG(c.name, ', ') WITHIN GROUP (ORDER BY fkc.constraint_column_id)
                 FROM sys.foreign_key_columns AS fkc
                 JOIN sys.columns AS c ON c.object_id = fkc.parent_object_id AND c.column_id = fkc.parent_column_id
                 WHERE fkc.constraint_object_id = fk.object_id),
                OBJECT_SCHEMA_NAME(fk.referenced_object_id) + '.' + OBJECT_NAME(fk.referenced_object_id),
                (SELECT STRING_AGG(c.name, ', ') WITHIN GROUP (ORDER BY fkc.constraint_column_id)
                 FROM sys.foreign_key_columns AS fkc
                 JOIN sys.columns AS c ON c.object_id = fkc.referenced_object_id AND c.column_id = fkc.referenced_column_id
                 WHERE fkc.constraint_object_id = fk.object_id),
                fk.delete_referential_action_desc,
                fk.update_referential_action_desc,
                fk.is_disabled
            FROM sys.foreign_keys AS fk
            WHERE fk.parent_object_id = @id OR fk.referenced_object_id = @id
            ORDER BY IIF(fk.parent_object_id = @id, 0, 1), fk.name;
            """);
        cmd.Parameters.Add("@id", SqlDbType.Int).Value = objectId;
        cmd.Parameters.Add("@max", SqlDbType.Int).Value = SqlOptions.MaxRows;

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        var columns = new List<ColumnInfo>();
        while (await reader.ReadAsync(cancellationToken))
        {
            columns.Add(new ColumnInfo(
                Ordinal: reader.GetInt32(0),
                Name: reader.GetString(1),
                DataType: reader.GetString(2),
                Nullable: reader.GetBoolean(3),
                IsPrimaryKey: reader.GetBoolean(4),
                IsIdentity: reader.GetBoolean(5),
                Default: StringOrNull(reader, 6),
                ComputedAs: StringOrNull(reader, 7)));
        }

        await reader.NextResultAsync(cancellationToken);
        var indexes = new List<IndexInfo>();
        while (await reader.ReadAsync(cancellationToken))
        {
            indexes.Add(new IndexInfo(
                Name: reader.GetString(0),
                Type: reader.GetString(1),
                IsUnique: reader.GetBoolean(2),
                IsPrimaryKey: reader.GetBoolean(3),
                KeyColumns: StringOrNull(reader, 4),
                IncludedColumns: StringOrNull(reader, 5),
                Filter: StringOrNull(reader, 6)));
        }

        await reader.NextResultAsync(cancellationToken);
        var foreignKeys = new List<ForeignKeyInfo>();
        while (await reader.ReadAsync(cancellationToken))
        {
            foreignKeys.Add(new ForeignKeyInfo(
                Name: reader.GetString(0),
                Direction: reader.GetString(1),
                FromTable: reader.GetString(2),
                FromColumns: reader.GetString(3),
                ToTable: reader.GetString(4),
                ToColumns: reader.GetString(5),
                OnDelete: reader.GetString(6),
                OnUpdate: reader.GetString(7),
                IsDisabled: reader.GetBoolean(8)));
        }

        // Non-short-circuit '|' so every list gets trimmed.
        var truncated = TrimToLimit(columns, SqlOptions.MaxRows)
                      | TrimToLimit(indexes, SqlOptions.MaxRows)
                      | TrimToLimit(foreignKeys, SqlOptions.MaxRows);

        return new DescribeTableResult(schema, name, type, truncated, columns, indexes, foreignKeys);
    }

    /// <summary>
    /// Queries fetch limit+1 rows; if the extra row arrived, drop it and report truncation.
    /// </summary>
    internal static bool TrimToLimit<T>(List<T> rows, int limit)
    {
        if (rows.Count <= limit)
        {
            return false;
        }

        rows.RemoveRange(limit, rows.Count - limit);
        return true;
    }

    private static string? StringOrNull(Microsoft.Data.SqlClient.SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
}
