using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WindowsStuff;

internal sealed class PowerShellRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<JsonElement?> RunAsync(
        WorkerRequest request,
        Action<string> onLog,
        CancellationToken cancellationToken)
    {
        var script = ReadWorkerScript();
        var encodedScript = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-EncodedCommand");
        startInfo.ArgumentList.Add(encodedScript);

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("Could not start Windows PowerShell.");
        }

        var outputTask = ReadOutputAsync(process.StandardOutput, onLog, cancellationToken);
        var errorTask = ReadErrorsAsync(process.StandardError, onLog, cancellationToken);
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request, JsonOptions));
        process.StandardInput.Close();

        await process.WaitForExitAsync(cancellationToken);
        var workerOutput = await outputTask;
        await errorTask;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(workerOutput.Error ?? $"PowerShell worker exited with code {process.ExitCode}.");
        }

        return workerOutput.Result;
    }

    private static async Task<WorkerOutput> ReadOutputAsync(
        StreamReader reader,
        Action<string> onLog,
        CancellationToken cancellationToken)
    {
        JsonElement? result = null;
        string? error = null;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var type = root.GetProperty("type").GetString();
                if (type == "log")
                {
                    onLog(root.GetProperty("message").GetString() ?? string.Empty);
                }
                else if (type == "error")
                {
                    error = root.GetProperty("message").GetString();
                    onLog($"ERROR: {error}");
                }
                else if (type == "result")
                {
                    if (root.TryGetProperty("data", out var data))
                    {
                        result = data.Clone();
                    }
                    onLog(root.GetProperty("message").GetString() ?? "Worker operation completed.");
                }
            }
            catch (JsonException)
            {
                onLog(line);
            }
        }

        return new WorkerOutput(result, error);
    }

    private static async Task ReadErrorsAsync(
        StreamReader reader,
        Action<string> onLog,
        CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            onLog($"PowerShell error: {line}");
        }
    }

    private static string ReadWorkerScript()
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("WindowsStuff.Worker.ps1")
            ?? throw new InvalidOperationException("Embedded PowerShell worker was not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private sealed record WorkerOutput(JsonElement? Result, string? Error);
}

internal sealed class WorkerRequest
{
    [JsonPropertyName("action")]
    public string Action { get; init; } = string.Empty;

    [JsonPropertyName("packageFullNames")]
    public string[] PackageFullNames { get; init; } = [];

    [JsonPropertyName("forceMode")]
    public bool ForceMode { get; init; }

    [JsonPropertyName("createRestorePoint")]
    public bool CreateRestorePoint { get; init; }
}

internal sealed class InstalledPackage
{
    [JsonPropertyName("displayName")]
    public string DisplayName { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("packageFullName")]
    public string PackageFullName { get; init; } = string.Empty;

    [JsonPropertyName("packageFamilyName")]
    public string PackageFamilyName { get; init; } = string.Empty;

    [JsonPropertyName("publisher")]
    public string Publisher { get; init; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; init; } = string.Empty;

    [JsonPropertyName("installLocation")]
    public string InstallLocation { get; init; } = string.Empty;

    [JsonPropertyName("userScope")]
    public string UserScope { get; init; } = string.Empty;

    [JsonPropertyName("state")]
    public string State { get; init; } = string.Empty;

    [JsonPropertyName("isInstalled")]
    public bool IsInstalled { get; init; }

    [JsonPropertyName("isProvisioned")]
    public bool IsProvisioned { get; init; }

    [JsonPropertyName("isFramework")]
    public bool IsFramework { get; init; }

    [JsonPropertyName("nonRemovable")]
    public bool NonRemovable { get; init; }
}

internal sealed class UserSettings
{
    public Dictionary<string, string[]> CategoryFilters { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string[]> SelectionProfiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

internal sealed record HistoryEntry(DateTimeOffset Timestamp, string Message);