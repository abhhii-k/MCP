using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlInvestigator.Mcp.Sql;

/// <summary>
/// Decides whether LLM-written SQL is a single, plain, read-only SELECT.
/// This is a second line of defense: the read-only database user is the real protection.
/// </summary>
public static class SelectValidator
{
    public const int MaxSqlLength = 20_000;

    /// <summary>
    /// Keywords that never belong in a read-only SELECT. Checked on the parser's token stream,
    /// so the same words inside string literals or comments do not cause false alarms.
    /// </summary>
    private static readonly HashSet<TSqlTokenType> ForbiddenTokens =
    [
        TSqlTokenType.Insert, TSqlTokenType.Update, TSqlTokenType.Delete, TSqlTokenType.Merge,
        TSqlTokenType.Truncate, TSqlTokenType.Into, TSqlTokenType.Bulk,
        TSqlTokenType.Create, TSqlTokenType.Alter, TSqlTokenType.Drop,
        TSqlTokenType.Exec, TSqlTokenType.Execute,
        TSqlTokenType.Grant, TSqlTokenType.Revoke, TSqlTokenType.Deny,
        TSqlTokenType.Begin, TSqlTokenType.Commit, TSqlTokenType.Rollback, TSqlTokenType.Save,
        TSqlTokenType.Declare, TSqlTokenType.Set, TSqlTokenType.Use, TSqlTokenType.Open,
        TSqlTokenType.Backup, TSqlTokenType.Restore, TSqlTokenType.Dbcc, TSqlTokenType.Kill,
        TSqlTokenType.Shutdown, TSqlTokenType.Reconfigure, TSqlTokenType.Checkpoint,
        TSqlTokenType.WaitFor, TSqlTokenType.WriteText, TSqlTokenType.UpdateText,
        TSqlTokenType.OpenRowSet, TSqlTokenType.OpenQuery, TSqlTokenType.OpenDataSource,
    ];

    /// <summary>Returns null if the SQL is allowed, otherwise a reason the LLM can act on.</summary>
    /// <param name="allowlist">Optional table allowlist; null means all tables may be read.</param>
    public static string? Validate(string? sql, TableAllowlist? allowlist = null)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return "SQL is empty.";
        }

        if (sql.Length > MaxSqlLength)
        {
            return $"SQL is longer than {MaxSqlLength} characters.";
        }

        // 1) Parse with Microsoft's own T-SQL parser (SQL Server 2025 grammar, accepts older syntax).
        var parser = new TSql170Parser(initialQuotedIdentifiers: true);
        TSqlFragment fragment;
        using (var reader = new StringReader(sql))
        {
            fragment = parser.Parse(reader, out var errors);
            if (errors.Count > 0)
            {
                return "SQL syntax error: " + string.Join("; ",
                    errors.Take(3).Select(e => $"line {e.Line}, col {e.Column}: {e.Message}"));
            }
        }

        // 2) Exactly one batch with exactly one statement (no "SELECT 1; DELETE ...", no GO).
        var script = (TSqlScript)fragment;
        if (script.Batches.Count != 1 || script.Batches[0].Statements.Count != 1)
        {
            return "Exactly one statement is allowed (no ';'-separated statements or GO batches).";
        }

        // 3) That statement must be a SELECT. "WITH cte AS (...) SELECT" is a SelectStatement too,
        //    but "WITH cte AS (...) DELETE" parses as a DeleteStatement and is rejected here.
        if (script.Batches[0].Statements[0] is not SelectStatement select)
        {
            var kind = script.Batches[0].Statements[0].GetType().Name.Replace("Statement", "");
            return $"Only SELECT queries (optionally starting with WITH) are allowed; this is a {kind} statement.";
        }

        // 4) SELECT ... INTO creates a table.
        if (select.Into is not null)
        {
            return "SELECT ... INTO is not allowed because it creates a table.";
        }

        // 5) Walk the whole syntax tree for read-but-dangerous constructs.
        var visitor = new ForbiddenConstructVisitor();
        fragment.Accept(visitor);
        if (visitor.Violation is not null)
        {
            return visitor.Violation;
        }

        // 6) Backstop: forbidden keywords anywhere in the token stream.
        var bad = fragment.ScriptTokenStream.FirstOrDefault(t => ForbiddenTokens.Contains(t.TokenType));
        if (bad is not null)
        {
            return $"Keyword '{bad.Text.ToUpperInvariant()}' is not allowed (line {bad.Line}).";
        }

        // 7) Optional policy: every table/view referenced must be on the allowlist.
        return allowlist is null ? null : CheckAllowlist(visitor, allowlist);
    }

    private static string? CheckAllowlist(ForbiddenConstructVisitor visitor, TableAllowlist allowlist)
    {
        foreach (var obj in visitor.ReferencedObjects)
        {
            var schema = obj.SchemaIdentifier?.Value;
            var name = obj.BaseIdentifier.Value;

            // An unqualified name that matches a CTE is not a real table.
            if (schema is null && visitor.CteNames.Contains(name))
            {
                continue;
            }

            if (obj.DatabaseIdentifier is not null)
            {
                return "Database-qualified names (db.schema.table) are not allowed while a table allowlist is active.";
            }

            if (!allowlist.IsAllowed(schema, name))
            {
                return $"Table '{schema ?? "dbo"}.{name}' is not in the allowlist (allowed: {allowlist}).";
            }
        }

        return null;
    }

    private sealed class ForbiddenConstructVisitor : TSqlFragmentVisitor
    {
        private static readonly HashSet<TableHintKind> LockingHints =
        [
            TableHintKind.UpdLock, TableHintKind.XLock, TableHintKind.TabLock, TableHintKind.TabLockX,
            TableHintKind.HoldLock, TableHintKind.Serializable, TableHintKind.RepeatableRead,
        ];

        public string? Violation { get; private set; }

        /// <summary>Tables, views and table-valued functions used in FROM/JOIN (for the allowlist).</summary>
        public List<SchemaObjectName> ReferencedObjects { get; } = [];

        public HashSet<string> CteNames { get; } = new(StringComparer.OrdinalIgnoreCase);

        private void Reject(string reason) => Violation ??= reason;

        public override void Visit(NamedTableReference node) => ReferencedObjects.Add(node.SchemaObject);

        public override void Visit(SchemaObjectFunctionTableReference node) => ReferencedObjects.Add(node.SchemaObject);

        public override void Visit(CommonTableExpression node) => CteNames.Add(node.ExpressionName.Value);

        // Reading files or other servers.
        public override void Visit(OpenRowsetTableReference node) => Reject("OPENROWSET is not allowed.");
        public override void Visit(BulkOpenRowset node) => Reject("OPENROWSET(BULK ...) is not allowed.");
        public override void Visit(InternalOpenRowset node) => Reject("OPENROWSET is not allowed.");
        public override void Visit(OpenQueryTableReference node) => Reject("OPENQUERY is not allowed.");
        public override void Visit(AdHocTableReference node) => Reject("OPENDATASOURCE is not allowed.");

        // server.database.schema.object = linked server call.
        public override void Visit(SchemaObjectName node)
        {
            if (node.ServerIdentifier is not null)
            {
                Reject("Four-part names (linked servers) are not allowed.");
            }
        }

        // NEXT VALUE FOR changes a sequence's state, even inside a SELECT.
        public override void Visit(NextValueForExpression node) =>
            Reject("NEXT VALUE FOR is not allowed because it advances a sequence.");

        // Hints that take long-lived or exclusive locks could block production writers.
        public override void Visit(TableHint node)
        {
            if (LockingHints.Contains(node.HintKind))
            {
                Reject($"Locking hint {node.HintKind.ToString().ToUpperInvariant()} is not allowed.");
            }
        }
    }
}
