using System.ComponentModel;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace SqlInvestigator.Mcp.Tools;

[McpServerToolType]
public sealed class PingTool(ILogger<PingTool> logger)
{
    [McpServerTool(
        Name = "ping",
        Title = "Ping",
        ReadOnly = true,      // does not modify its environment
        Destructive = false,  // never deletes/overwrites anything
        Idempotent = true,    // calling it twice has no extra effect
        OpenWorld = false)]   // works only within this server, not the internet
    [Description("Checks that the SQL Investigator MCP server is alive. Returns 'pong', the optional message, and the server's UTC time.")]
    public string Ping(
        [Description("Optional text to echo back.")] string? message = null)
    {
        logger.LogInformation("ping called with message: {Message}", message);

        var echo = string.IsNullOrWhiteSpace(message) ? "" : $" (you said: {message})";
        return $"pong{echo} at {DateTime.UtcNow:O}";
    }
}
