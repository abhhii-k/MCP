using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace SqlInvestigator.Mcp.Infrastructure;

/// <summary>One line in the audit file. Result data is deliberately NOT recorded (it may be sensitive).</summary>
public sealed record AuditEntry(
    DateTimeOffset Ts,
    string CallId,
    string? Client,
    string OsUser,
    string Tool,
    IDictionary<string, JsonElement>? Arguments,
    string Outcome,
    string? Error,
    long DurationMs);

/// <summary>
/// Append-only JSON Lines audit trail (one file per UTC day). Written to a local folder,
/// never to stdout (protocol channel) and not inside the OneDrive-synced repository.
/// </summary>
public sealed class AuditLog(string directoryPath, ILogger<AuditLog> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly SemaphoreSlim _gate = new(1, 1);

    public string DirectoryPath => directoryPath;

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SqlInvestigatorMcp", "audit");

    public async Task WriteAsync(AuditEntry entry)
    {
        var path = Path.Combine(directoryPath, $"audit-{entry.Ts.UtcDateTime:yyyyMMdd}.jsonl");
        var line = JsonSerializer.Serialize(entry, JsonOptions) + "\n";

        // Tool calls can run concurrently; serialize appends so lines never interleave.
        await _gate.WaitAsync();
        try
        {
            Directory.CreateDirectory(directoryPath);
            await File.AppendAllTextAsync(path, line);
        }
        catch (Exception ex)
        {
            // Fail-open: a full disk should not stop an investigation, but it must be visible.
            logger.LogError(ex, "Could not write audit entry {CallId} to {Path}", entry.CallId, path);
        }
        finally
        {
            _gate.Release();
        }
    }
}
