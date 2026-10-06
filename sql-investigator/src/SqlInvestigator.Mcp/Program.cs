using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using SqlInvestigator.Mcp.Infrastructure;
using SqlInvestigator.Mcp.Sql;

var builder = Host.CreateApplicationBuilder(args);

// IMPORTANT: stdout is the MCP protocol channel (JSON-RPC messages).
// The default console logger writes to stdout, which would corrupt the protocol,
// so we remove all providers and re-add the console logger pointed at stderr.
builder.Logging.ClearProviders();
builder.Logging.SetMinimumLevel(LogLevel.Information);
builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace; // every level -> stderr
});

// Structured (JSON) logs by default, so they can be shipped to Seq/Splunk/Azure Monitor.
// MCP_LOG_FORMAT=simple switches back to the human-readable format.
if (!string.Equals(builder.Configuration["MCP_LOG_FORMAT"], "simple", StringComparison.OrdinalIgnoreCase))
{
    builder.Logging.AddJsonConsole(options =>
    {
        options.UseUtcTimestamp = true;
        options.TimestampFormat = "O";
        options.IncludeScopes = true;
    });
}

// Normal .NET DI: configuration already includes environment variables.
builder.Services.AddSingleton(new SqlOptions
{
    ConnectionString = builder.Configuration["MSSQL_CONN"],
    ImpersonateUser = builder.Configuration["MSSQL_IMPERSONATE_USER"],
    TableAllowlist = TableAllowlist.Parse(builder.Configuration["MSSQL_TABLE_ALLOWLIST"]),
});
builder.Services.AddSingleton<SqlSessionFactory>();
builder.Services.AddSingleton(sp => new AuditLog(
    builder.Configuration["MCP_AUDIT_LOG_DIR"] ?? AuditLog.DefaultDirectory,
    sp.GetRequiredService<ILogger<AuditLog>>()));

builder.Services
    .AddMcpServer(options =>
    {
        // Sent to the client during the "initialize" handshake.
        options.ServerInfo = new Implementation
        {
            Name = "sql-investigator",
            Title = "SQL Server Investigator (read-only)",
            Version = "0.2.0",
        };
        // Hint to the LLM about how to use this server as a whole.
        options.ServerInstructions =
            "Read-only tools for investigating a Microsoft SQL Server database during debugging. " +
            "These tools never modify data or schema.";
    })
    .WithStdioServerTransport()   // talk JSON-RPC over stdin/stdout
    .WithToolsFromAssembly()      // find [McpServerToolType] classes in this assembly
    .WithRequestFilters(filters => filters.AddCallToolFilter(AuditFilter.Create)); // audit every tools/call

await builder.Build().RunAsync();
