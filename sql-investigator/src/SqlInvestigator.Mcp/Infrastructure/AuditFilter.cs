using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace SqlInvestigator.Mcp.Infrastructure;

/// <summary>
/// MCP request filter around every tools/call - the MCP equivalent of ASP.NET Core middleware.
/// Audits each call in one place, so a new tool can never "forget" to be audited.
/// </summary>
public static class AuditFilter
{
    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Create(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next)
    {
        return async (context, cancellationToken) =>
        {
            var services = context.Services ?? context.Server.Services!;
            var audit = services.GetRequiredService<AuditLog>();
            var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("SqlInvestigator.Audit");

            var tool = context.Params?.Name ?? "(unknown)";
            var callId = Guid.NewGuid().ToString("N")[..12];
            var client = context.Server.ClientInfo is { } info ? $"{info.Name} {info.Version}" : null;

            // Every log line written while this tool runs (including inside the tool) carries these fields.
            using var scope = logger.BeginScope(new Dictionary<string, object> { ["Tool"] = tool, ["CallId"] = callId });

            var stopwatch = Stopwatch.StartNew();
            var outcome = "exception";
            string? error = null;
            try
            {
                var result = await next(context, cancellationToken);
                outcome = result.IsError == true ? "error" : "ok";
                if (result.IsError == true)
                {
                    error = result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text;
                }

                return result;
            }
            catch (McpException ex)
            {
                // Deliberate, user-facing failure (e.g. "Query rejected"). The SDK turns it into
                // isError=true AFTER this filter, so we see the exception rather than the result.
                outcome = "error";
                error = ex.Message;
                throw;
            }
            catch (Exception ex)
            {
                error = ex.Message;  // unexpected failure: outcome stays "exception"
                throw;
            }
            finally
            {
                stopwatch.Stop();
                if (error is { Length: > 500 })
                {
                    error = error[..500];
                }

                await audit.WriteAsync(new AuditEntry(
                    DateTimeOffset.UtcNow, callId, client, Environment.UserName, tool,
                    context.Params?.Arguments, outcome, error, stopwatch.ElapsedMilliseconds));

                logger.LogInformation("Tool {Tool} finished: {Outcome} in {DurationMs} ms",
                    tool, outcome, stopwatch.ElapsedMilliseconds);
            }
        };
    }
}
