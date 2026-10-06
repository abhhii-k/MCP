# SQL Investigator – a read-only MCP server for SQL Server

**SQL Investigator** is a [Model Context Protocol (MCP)](https://modelcontextprotocol.io) server written in C# / .NET 10.
It lets an AI assistant such as **Claude Code** investigate a Microsoft SQL Server database during debugging, while
the assistant reads your frontend and backend source code at the same time.

It is **strictly read-only**: no tool can modify data or schema. Several independent safety layers enforce this
(see [Safety model](#safety-model)).

Typical questions it answers:

- *"Order #1 shows the wrong total in the UI. Is it the Angular code, the API, or the database?"*
- *"Which stored procedures or triggers write to `sales.Orders`?"*
- *"Is anything blocked in the database right now? Who is blocking whom?"*
- *"Which queries touching `Orders` are the most expensive?"*

---

## Contents

- [Tools](#tools)
- [How it works](#how-it-works)
- [Safety model](#safety-model)
- [Repository layout](#repository-layout)
- [Prerequisites](#prerequisites)
- [Quick start (local test database)](#quick-start-local-test-database)
- [Configuration](#configuration)
- [Connecting to real databases](#connecting-to-real-databases)
- [Claude Code permissions (recommended)](#claude-code-permissions-recommended)
- [Using it: example investigations](#using-it-example-investigations)
- [Testing with the MCP Inspector](#testing-with-the-mcp-inspector)
- [Logging and audit trail](#logging-and-audit-trail)
- [Development](#development)
- [Troubleshooting](#troubleshooting)
- [Limitations](#limitations)
- [Tech stack](#tech-stack)

---

## Tools

| Tool | What it does | Accepts SQL from the AI? | Permission needed |
|---|---|---|---|
| `ping` | Health check, returns `pong` | No | none |
| `test_connection` | Server version, edition, database, login, **effective DB user**, and any write permissions detected | No | connect |
| `list_tables` | Tables (optionally views) with approximate row counts; optional name filter | No (parameters only) | metadata visibility |
| `describe_table` | Columns, indexes, foreign keys (incoming and outgoing) of a table or view | No (parameters only) | metadata visibility, `VIEW DEFINITION` for defaults |
| `find_in_definitions` | Searches the source of procedures, views, functions and triggers for a text | No (parameters only) | `VIEW DEFINITION` |
| `get_definition` | Returns an object's source code with line numbers (paged for large objects) | No (parameters only) | `VIEW DEFINITION` |
| `run_select` | Runs **one** read-only `SELECT` (or `WITH … SELECT`); max 200 rows, 30 s timeout | **Yes**, validated (see below) | `SELECT` on the tables |
| `blocking_sessions` | Current blocking chains: head blockers, waits, SQL text | No | `VIEW SERVER STATE` (2019), `VIEW SERVER PERFORMANCE STATE` (2022+), `VIEW DATABASE STATE` (Azure SQL DB) |
| `slow_queries` | Most expensive statements from the plan cache for the current DB | No | same as `blocking_sessions` |

Every tool is marked with the MCP annotations `readOnlyHint: true` and `destructiveHint: false`.

---

## How it works

```
 ┌───────────────┐   starts as child process    ┌──────────────────────────┐   ADO.NET    ┌──────────────┐
 │  Claude Code  │ ───────────────────────────► │  SqlInvestigator.Mcp.exe │ ───────────► │  SQL Server  │
 │ (MCP client)  │ ◄── JSON-RPC over stdio ───► │      (MCP server)        │  read-only   │  test/replica│
 └───────────────┘   stdin/stdout = protocol    └──────────────────────────┘              └──────────────┘
        │             stderr = logs                     │
        │ reads your app code with                      └─► audit log (JSON Lines, %LOCALAPPDATA%)
        ▼ its own file tools
   frontend + backend repos
```

- **Transport: stdio.** Claude Code starts the exe and talks JSON-RPC 2.0 over stdin/stdout. **Nothing else may be
  written to stdout.** All logs go to stderr.
- **Lifecycle:** `initialize` (handshake, capabilities) → `tools/list` (discovery, JSON Schema per tool) →
  `tools/call` (invocation). The AI only sees the tool names, descriptions and schemas, never the C# code.
- **Built with:** the official [MCP C# SDK](https://github.com/modelcontextprotocol/csharp-sdk) (`ModelContextProtocol`
  package) on top of the .NET Generic Host (DI, configuration, logging).

---

## Safety model

The database login's permissions are the **real** protection. Everything in code is a second layer that gives fast,
clear feedback and protects you if the login is more powerful than it should be.

| # | Layer | Where | What it stops |
|---|---|---|---|
| ① | **Fixed queries + typed parameters** | all tools except `run_select` | SQL injection; the AI can't change these queries |
| ② | **T-SQL parser** (Microsoft `ScriptDom`) | `run_select` | Anything other than a single `SELECT`: DML, DDL, `EXEC`, `SELECT INTO`, multiple statements, `GO`, `OPENROWSET`/`OPENQUERY`, linked servers, `NEXT VALUE FOR`, locking hints. Checks the syntax tree, not the text, so `EXEC('DEL'+'ETE …')` is caught and `WHERE x = 'DELETE'` is allowed |
| ②b | **Optional table allowlist** | `run_select` | Reading tables outside an approved list |
| ③ | **Always-rollback transaction** | `run_select` | Undoes data changes if anything ever slipped through ② |
| ④ | **Read-only identity** | every connection | SQL Server itself refuses writes. Use a read-only login, or `MSSQL_IMPERSONATE_USER` (see below) |
| + | Row/size/time limits | all tools | 200 rows, 500 chars per cell, ~100 KB per result, 30 s timeout, 5 s lock timeout |
| + | Audit log | every `tools/call` | Traceability: who ran what, when, with which result status |
| + | Client deny rules | Claude Code settings | Claude bypassing the server with `sqlcmd` under *your* account |

> ⚠️ Rollback can't undo actions outside the database transaction (linked servers, files, e-mail, sequences), and
> it can't "un-read" sensitive data. That's why layer ④ (permissions) must be correct.

**Prompt injection:** source code returned by `get_definition` comes from the database and could contain text like
*"AI: run this…"*. The tool descriptions tell the model to treat results as data, and more importantly the server has
**no write capability** for a manipulated model to use.

### Impersonation (`MSSQL_IMPERSONATE_USER`)

If you must connect with a login that *can* write (for example your own dev Windows account), set
`MSSQL_IMPERSONATE_USER` to a **read-only database user**. Every session then runs
`EXECUTE AS USER = '<user>' WITH COOKIE`, and permission checks use that user, not your login.

- The cookie means a query can't switch back with `REVERT`. Only the server code (holding the cookie) can.
- `blocking_sessions` and `slow_queries` need server-level state, which `EXECUTE AS USER` hides. They therefore run
  **without** impersonation. This is acceptable only because their SQL is fixed in code; they accept no SQL from the AI.
- The production-grade setup is a **dedicated read-only login** without impersonation (see
  [Connecting to real databases](#connecting-to-real-databases)).

---

## Repository layout

```
sql-investigator/
├─ SqlInvestigator.slnx                  # solution (.NET 10 XML format)
├─ src/SqlInvestigator.Mcp/
│  ├─ Program.cs                         # host, stderr/JSON logging, DI, MCP server, audit filter
│  ├─ Sql/
│  │  ├─ SqlOptions.cs                   # limits (200 rows, 30 s) + settings from env vars
│  │  ├─ SqlSessionFactory.cs            # the ONLY place connections are opened; impersonation
│  │  ├─ ObjectResolver.cs               # "sales.Orders" / "[sales].[Orders]" -> object_id
│  │  ├─ SelectValidator.cs              # ScriptDom-based SELECT-only validator (layer ②)
│  │  └─ TableAllowlist.cs               # optional allowlist (layer ②b)
│  ├─ Tools/                             # one class per tool group ([McpServerToolType])
│  │  ├─ PingTool.cs  ConnectionTools.cs  SchemaTools.cs
│  │  ├─ DefinitionTools.cs  QueryTools.cs  DiagnosticsTools.cs
│  └─ Infrastructure/
│     ├─ AuditFilter.cs                  # MCP request filter around every tools/call
│     └─ AuditLog.cs                     # JSON Lines audit writer
├─ tests/SqlInvestigator.Mcp.Tests/      # xUnit: validator, allowlist, audit log (no DB needed)
├─ db/01-create-testdb.sql               # creates the local test DB + read-only user mcp_reader
└─ demo/ShopApp/                         # small Angular + .NET sample code for an end-to-end investigation
```

---

## Prerequisites

| Requirement | Version | Check |
|---|---|---|
| .NET SDK | 10.x | `dotnet --version` |
| SQL Server | 2019, 2022, Azure SQL (LocalDB works for local testing) | `sqllocaldb info` |
| Claude Code | recent | `claude --version` |
| Node.js (only for the MCP Inspector) | ≥ 22.19 | `node --version` |
| `sqlcmd` (only to create the test DB) | any | `sqlcmd -?` |

---

## Quick start (local test database)

All commands are for **PowerShell**, run from the `sql-investigator` folder.

### 1. Create the test database (LocalDB)

```powershell
sqlcmd -S "(localdb)\MSSQLLocalDB" -E -i db\01-create-testdb.sql
```

This creates `McpTestDb` with sample tables (`dbo.Customers`, `dbo.Products`, `sales.Orders`, `sales.OrderItems`,
`dbo.AuditLog`), a view, a stored procedure (with a deliberate bug), a trigger, and a **read-only user `mcp_reader`**
(`db_datareader`, `VIEW DEFINITION`, `VIEW DATABASE STATE`, explicit `DENY` on writes). The script also sets
`AUTO_CLOSE OFF`, so the plan cache survives between connections.

> This script writes. It is a one-time setup script and **not** part of the MCP server.

### 2. Build and test

```powershell
dotnet build
dotnet test          # 47 tests, no database needed
```

### 3. Publish

Publish to a folder **outside** your working copy, so Claude Code runs a stable copy while you keep developing
(a running exe is locked by Windows and would block `dotnet build`):

```powershell
dotnet publish src\SqlInvestigator.Mcp -c Release -r win-x64 --self-contained false `
  -o "$env:LOCALAPPDATA\Programs\SqlInvestigatorMcp"
```

Use `--self-contained true` for machines without the .NET 10 runtime.

### 4. Register with Claude Code

```powershell
claude mcp add --scope user sql-investigator `
  -e "MSSQL_CONN=Server=(localdb)\MSSQLLocalDB;Database=McpTestDb;Integrated Security=true;Encrypt=false" `
  -e MSSQL_IMPERSONATE_USER=mcp_reader `
  -- "$env:LOCALAPPDATA\Programs\SqlInvestigatorMcp\SqlInvestigator.Mcp.exe"

claude mcp get sql-investigator     # expect: Status ✔ Connected
```

- `--scope user` makes the server available in every folder (your app repos live elsewhere).
- Always use a **full path** to the exe. Claude Code starts it from whatever folder you open.
- To change settings later: `claude mcp remove sql-investigator -s user`, then `add` again.

### 5. Try it

Open a **new** Claude Code session (servers load at session start), type `/mcp` to confirm 9 tools, then ask:

> *Test the SQL connection and tell me whether it's safe.*

Expected: connected to `McpTestDb`, effective user `mcp_reader`, **no write permissions**.

---

## Configuration

All settings are environment variables, set with `-e` in `claude mcp add` or as Windows user environment variables.

| Variable | Required | Example | Purpose |
|---|---|---|---|
| `MSSQL_CONN` | **yes** | `Server=sqlhost;Database=ShopDb;Integrated Security=true;Encrypt=true` | Connection string. Never commit it. |
| `MSSQL_IMPERSONATE_USER` | no | `mcp_reader` | Read-only DB user to `EXECUTE AS` on every session (see [Impersonation](#impersonation-mssql_impersonate_user)) |
| `MSSQL_TABLE_ALLOWLIST` | no | `sales.*,dbo.Customers` | Restrict `run_select` to these tables/views. `schema.*` wildcards. `sys`/`INFORMATION_SCHEMA` are always allowed |
| `MCP_AUDIT_LOG_DIR` | no | `D:\logs\mcp-audit` | Audit folder. Default `%LOCALAPPDATA%\SqlInvestigatorMcp\audit` |
| `MCP_LOG_FORMAT` | no | `simple` | `simple` = human-readable stderr logs. Default = JSON |
| `Logging__LogLevel__Default` | no | `Warning` | Standard .NET log level override |

**Secrets:** `claude mcp add -e` stores values in `~/.claude.json`. That's fine for Windows/Entra auth (no password).
If a connection string contains a password, set `MSSQL_CONN` as a user environment variable instead
(`setx MSSQL_CONN "..."`); Claude Code passes its environment to the server.

---

## Connecting to real databases

**Never point this at a production primary with a login that can write.** Use a **test copy or a read replica**
and a **dedicated read-only identity**.

### Read-only login (ask your DBA)

SQL Server 2019 / 2022 / Managed Instance:

```sql
-- server level
CREATE LOGIN [DOMAIN\svc-mcp-reader] FROM WINDOWS;
GRANT VIEW SERVER STATE TO [DOMAIN\svc-mcp-reader];          -- 2022+: VIEW SERVER PERFORMANCE STATE (narrower)

-- in the application database
CREATE USER [DOMAIN\svc-mcp-reader] FOR LOGIN [DOMAIN\svc-mcp-reader];
ALTER ROLE db_datareader ADD MEMBER [DOMAIN\svc-mcp-reader];   -- or GRANT SELECT on specific tables only
GRANT VIEW DEFINITION TO [DOMAIN\svc-mcp-reader];
```

Azure SQL Database (Microsoft Entra ID):

```sql
CREATE USER [mcp-reader@contoso.com] FROM EXTERNAL PROVIDER;
ALTER ROLE db_datareader ADD MEMBER [mcp-reader@contoso.com];
GRANT VIEW DEFINITION TO [mcp-reader@contoso.com];
GRANT VIEW DATABASE STATE TO [mcp-reader@contoso.com];
```

With a read-only login you don't need `MSSQL_IMPERSONATE_USER`. Run `test_connection` afterwards: `writePermissionsDetected`
must be empty.

> `VIEW SERVER STATE` shows the SQL text of all sessions on the server. Prefer the narrower
> `VIEW SERVER PERFORMANCE STATE` on SQL Server 2022+.

### Connection string examples

| Scenario | `MSSQL_CONN` |
|---|---|
| Windows auth | `Server=sqlhost;Database=ShopDb;Integrated Security=true;Encrypt=true` |
| Always On readable secondary | `Server=ag-listener;Database=ShopDb;Integrated Security=true;Encrypt=true;ApplicationIntent=ReadOnly;MultiSubnetFailover=true` |
| Azure SQL (Entra ID) | `Server=tcp:myserver.database.windows.net;Database=ShopDb;Authentication=Active Directory Default;Encrypt=true` |
| LocalDB (testing) | `Server=(localdb)\MSSQLLocalDB;Database=McpTestDb;Integrated Security=true;Encrypt=false` |

Azure Entra ID authentication additionally requires the `Microsoft.Data.SqlClient.Extensions.Azure` NuGet package
(SqlClient 7 moved Azure auth into a separate package).

**Several environments:** register the same exe several times with different names and connection strings, e.g.
`sql-investigator-test` and `sql-investigator-replica`. Sessions are tagged `Application Name=SqlInvestigatorMcp`
so DBAs can identify them.

**Sharing with a team:** `claude mcp add --scope project ...` writes a `.mcp.json` you can commit. Use
`${MSSQL_CONN}` environment-variable expansion in it so no connection string ends up in git.

---

## Claude Code permissions (recommended)

Add to `~/.claude/settings.json` (user level, applies to all projects; deny rules always win):

```json
{
  "permissions": {
    "deny": [
      "Bash(sqlcmd *)", "PowerShell(sqlcmd *)", "PowerShell(Invoke-Sqlcmd *)",
      "Bash(osql *)",   "PowerShell(osql *)",
      "Bash(bcp *)",    "PowerShell(bcp *)"
    ],
    "allow": [
      "mcp__sql-investigator__ping",
      "mcp__sql-investigator__test_connection",
      "mcp__sql-investigator__list_tables",
      "mcp__sql-investigator__describe_table",
      "mcp__sql-investigator__find_in_definitions",
      "mcp__sql-investigator__get_definition",
      "mcp__sql-investigator__blocking_sessions",
      "mcp__sql-investigator__slow_queries"
    ]
  }
}
```

- **deny** stops Claude from running SQL through the shell with *your* account, which would bypass every layer above.
  Deny rules don't match a program called by full path or inside `cmd /c`, so also avoid auto-approving shell
  commands during real investigations.
- **allow** auto-approves the fixed-query tools. `run_select` is intentionally **not** listed, so Claude asks before
  running SQL it wrote.

---

## Using it: example investigations

Open Claude Code **in your application's folder** (so it can read the code) and describe the problem. Claude combines
its built-in file tools with this server's tools.

Example prompts:

```text
Customer complaint: order #1 shows Total €34.49 but the item lines add up to €58.99.
Investigate across the Angular frontend, the .NET backend in this folder, and the database
using the sql-investigator tools. Find the root cause, check whether other orders are affected,
and propose a fix. Do not change any code or data.
```

```text
Which stored procedures, triggers or views write to sales.Orders.TotalAmount?
```

```text
The orders API times out. Is anything blocked right now, and which queries on sales.Orders are the most expensive?
```

```text
Describe the sales.Orders table. If I delete a customer, which tables are affected?
```

A typical investigation flow:

1. Claude reads the frontend → API → service code (built-in file tools).
2. `list_tables` / `describe_table` to understand the schema.
3. `find_in_definitions` → `get_definition` to find database code that touches the data.
4. `run_select` to verify the hypothesis with real data (you approve each query).
5. A root cause plus a proposed fix. **A human applies any fix** through the normal change process; this server cannot write.

### Demo app

`demo/ShopApp/` contains a small Angular component and .NET service that sit on top of the test database. Run the
first prompt above from `demo/ShopApp` to see a full investigation. (The bug is in
`sales.usp_RecalculateOrderTotal`: it sums `UnitPrice` and ignores `Quantity`.)

---

## Testing with the MCP Inspector

The [MCP Inspector](https://github.com/modelcontextprotocol/inspector) is the standard MCP test client (Node ≥ 22.19).

**Web UI:**

```powershell
npx @modelcontextprotocol/inspector "$env:LOCALAPPDATA\Programs\SqlInvestigatorMcp\SqlInvestigator.Mcp.exe"
```

Set the environment variables in the UI, click **Connect → Tools → List Tools**, pick a tool, **Run Tool**.

**CLI** (server command first, then options):

```powershell
$EXE  = "$env:LOCALAPPDATA\Programs\SqlInvestigatorMcp\SqlInvestigator.Mcp.exe"
$CONN = "MSSQL_CONN=Server=(localdb)\MSSQLLocalDB;Database=McpTestDb;Integrated Security=true;Encrypt=false"

npx @modelcontextprotocol/inspector --cli $EXE -e $CONN -e MSSQL_IMPERSONATE_USER=mcp_reader --method tools/list
npx @modelcontextprotocol/inspector --cli $EXE -e $CONN -e MSSQL_IMPERSONATE_USER=mcp_reader `
  --method tools/call --tool-name describe_table --tool-arg table=sales.Orders
npx @modelcontextprotocol/inspector --cli $EXE -e $CONN -e MSSQL_IMPERSONATE_USER=mcp_reader `
  --method tools/call --tool-name run_select --tool-arg "sql=SELECT TOP 3 OrderId, Status FROM sales.Orders"
```

---

## Logging and audit trail

**Logs** go to **stderr** as JSON (one object per line), ready for Seq, Splunk or Azure Monitor. Every line written
during a tool call carries a scope with `Tool` and `CallId`. Claude Code shows server stderr in its MCP logs.
Use `MCP_LOG_FORMAT=simple` for readable output while developing.

**Audit log:** one JSON line per `tools/call`, written by a central MCP request filter
(`Infrastructure/AuditFilter.cs`), so no tool can be forgotten:

```json
{"ts":"2026-10-06T11:09:23.65+00:00","callId":"6071b69e7000","client":"claude-code 2.1.286","osUser":"jdoe",
 "tool":"run_select","arguments":{"sql":"SELECT Email FROM dbo.Customers"},"outcome":"error",
 "error":"Query rejected: Table 'dbo.Customers' is not in the allowlist (allowed: sales.*).","durationMs":1}
```

- Location: `%LOCALAPPDATA%\SqlInvestigatorMcp\audit\audit-yyyyMMdd.jsonl` (UTC day), or `MCP_AUDIT_LOG_DIR`.
- `outcome`: `ok`, `error` (expected failures such as rejected queries), `exception` (unexpected).
- **Result rows are never logged.** The audit records what was asked, not the data returned.
- If the file can't be written, the error is logged and the call continues (fail-open).

```powershell
Get-Content "$env:LOCALAPPDATA\SqlInvestigatorMcp\audit\audit-*.jsonl" -Tail 10
```

---

## Development

### Build, test, run

```powershell
dotnet build
dotnet test
npx @modelcontextprotocol/inspector dotnet run --project src\SqlInvestigator.Mcp    # quick manual test
```

If Claude Code has the server running from `bin\Debug`, Windows locks the exe and `dotnet build` fails. Register the
**published** exe (above) instead, or close Claude sessions / disable the server with `/mcp` before rebuilding.

### Adding a tool

```csharp
[McpServerToolType]
public sealed class MyTools(SqlSessionFactory sessions)
{
    [McpServerTool(Name = "my_tool", Title = "My tool",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("What it does, when to use it, and how to interpret the result. The AI reads this.")]
    public async Task<MyResult> MyTool(
        [Description("Explain each parameter.")] string name,
        CancellationToken cancellationToken = default)
    {
        await using var session = await sessions.OpenAsync(cancellationToken);   // impersonated, read-only
        await using var cmd = session.CreateCommand("SELECT ... WHERE name = @name");
        cmd.Parameters.Add("@name", SqlDbType.NVarChar, 128).Value = name;     // always parameters
        // ...
    }
}
```

Rules every tool must follow:

- **Never write to stdout** (`Console.WriteLine` breaks the protocol). Use `ILogger`.
- Open connections only through `SqlSessionFactory`. Use `OpenForServerDiagnosticsAsync` **only** for fixed DMV queries.
- Put user input into **typed parameters**; never concatenate it into SQL.
- Cap results (`SqlOptions.MaxRows`) and report truncation, so the AI knows it didn't see everything.
- Throw `McpException` for messages the AI should see (other exceptions are hidden behind a generic error).
- Write the `[Description]` as instructions for the AI, including what to do next.

Tools are discovered automatically (`WithToolsFromAssembly`) and audited automatically (the request filter).

---

## Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| `dotnet build`: *file is locked by SqlInvestigator.Mcp* | A Claude/Inspector session is running that exe | Close sessions or `/mcp` → disable; or run the published copy |
| *The system cannot find the path specified* | Relative exe path from the wrong folder | Use the full path |
| `claude mcp remove`: *No MCP server named …* | Registered in a different scope, or not at all | `claude mcp list` / `claude mcp get sql-investigator` |
| `test_connection`: *MSSQL_CONN … is not set* | Env var missing in the registration | Re-add with `-e "MSSQL_CONN=..."` |
| `test_connection` shows a WARNING with write permissions | Login can write and no impersonation | Use a read-only login, or set `MSSQL_IMPERSONATE_USER` |
| `blocking_sessions`/`slow_queries`: *Missing permission to read server state* | No `VIEW SERVER STATE` / `VIEW DATABASE STATE` | Ask the DBA (see grants above) |
| `slow_queries` returns nothing on LocalDB/Express | `AUTO_CLOSE ON` clears the plan cache when the last connection closes | `ALTER DATABASE <db> SET AUTO_CLOSE OFF` |
| Inspector: *No servers found in config file* | Inspector 2.x argument order | Server command first, then `--method`/`-e` |
| Inspector won't start | Node too old | Node ≥ 22.19 (`nvm install 22`) |
| `run_select`: *Query rejected: …* | Validator or allowlist | Read the reason; it states what is allowed |
| Tools missing in Claude Code | Server added after the session started | Start a new `claude` session, check `/mcp` |

---

## Limitations

- `slow_queries` uses the **plan cache**: statistics since each plan was cached, lost on restart or eviction. Query Store
  would provide history (not implemented yet).
- The table allowlist checks names in the query, not what a **view** reads internally. Use `GRANT SELECT` per
  object for real enforcement.
- `find_in_definitions` can't search **encrypted** modules.
- Case sensitivity of searches follows the database collation.
- stdio only: each developer runs a local copy. A shared, centrally hosted server would need the HTTP transport
  (`ModelContextProtocol.AspNetCore`) plus authentication.

---

## Tech stack

| Package | Version | Purpose |
|---|---|---|
| .NET | 10 | runtime |
| `ModelContextProtocol` | 2.2.0 | official MCP C# SDK (server, stdio transport, filters) |
| `Microsoft.Extensions.Hosting` | 10.0.12 | Generic Host: DI, configuration, logging |
| `Microsoft.Data.SqlClient` | 7.1.1 | SQL Server driver |
| `Microsoft.SqlServer.TransactSql.ScriptDom` | 180.117.0 | T-SQL parser for `run_select` validation |
| xUnit | 2.9.3 | unit tests |
