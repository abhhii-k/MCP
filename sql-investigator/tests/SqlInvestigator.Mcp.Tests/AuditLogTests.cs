using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SqlInvestigator.Mcp.Infrastructure;

namespace SqlInvestigator.Mcp.Tests;

public class AuditLogTests
{
    [Fact]
    public async Task Writes_one_json_line_per_entry()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mcp-audit-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var audit = new AuditLog(dir, NullLogger<AuditLog>.Instance);
            var args = new Dictionary<string, JsonElement>
            {
                ["sql"] = JsonSerializer.SerializeToElement("SELECT 1"),
            };
            var ts = new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

            await audit.WriteAsync(new AuditEntry(ts, "abc123", "test 1.0", "dev", "run_select", args, "ok", null, 42));
            await audit.WriteAsync(new AuditEntry(ts, "def456", "test 1.0", "dev", "run_select", args, "error", "Query rejected", 3));

            var lines = await File.ReadAllLinesAsync(Path.Combine(dir, "audit-20261006.jsonl"));
            Assert.Equal(2, lines.Length);

            using var first = JsonDocument.Parse(lines[0]);
            Assert.Equal("run_select", first.RootElement.GetProperty("tool").GetString());
            Assert.Equal("SELECT 1", first.RootElement.GetProperty("arguments").GetProperty("sql").GetString());
            Assert.Equal(42, first.RootElement.GetProperty("durationMs").GetInt64());
            Assert.False(first.RootElement.TryGetProperty("error", out _)); // nulls are omitted

            using var second = JsonDocument.Parse(lines[1]);
            Assert.Equal("Query rejected", second.RootElement.GetProperty("error").GetString());
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
