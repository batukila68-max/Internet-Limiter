using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace InternetLimiter;

public sealed class ActiveLimit
{
    public string PolicyName { get; init; } = "";
    public string AppMatch { get; init; } = "";
    public long RateBitsPerSecond { get; init; }
    public string RateText => FormatRate(RateBitsPerSecond);

    public static string FormatRate(long bitsPerSecond)
    {
        var kbps = bitsPerSecond / 8192.0; // bits -> KB/s (1024 bytes)
        return kbps >= 1024 ? $"{kbps / 1024.0:0.#} МБ/с" : $"{kbps:0} КБ/с";
    }
}

/// <summary>
/// Manages per-application bandwidth limits via Windows policy-based QoS
/// (NetQosPolicy / ActiveStore). Requires administrator rights.
/// </summary>
public static class QosService
{
    private const string PolicyPrefix = "InternetLimiter_";

    public static string PolicyNameFor(string executableName)
    {
        var sanitized = Regex.Replace(executableName, @"[^A-Za-z0-9]+", "_").Trim('_');
        return PolicyPrefix + sanitized;
    }

    public static async Task<List<ActiveLimit>> GetLimitsAsync()
    {
        var cmd =
            "[Console]::OutputEncoding=[Text.Encoding]::UTF8;" +
            $"Get-NetQosPolicy -PolicyStore ActiveStore -ErrorAction SilentlyContinue " +
            $"| Where-Object {{ $_.Name -like '{PolicyPrefix}*' }} " +
            "| Select-Object Name, AppPathName, ThrottleRateAction | ConvertTo-Json -Compress";

        var output = await RunPowerShellAsync(cmd);
        var limits = new List<ActiveLimit>();
        if (string.IsNullOrWhiteSpace(output))
            return limits;

        using var doc = JsonDocument.Parse(output);
        var root = doc.RootElement;
        var items = root.ValueKind == JsonValueKind.Array ? root.EnumerateArray() : new[] { root }.AsEnumerable();

        foreach (var item in items)
        {
            var name = item.TryGetProperty("Name", out var n) ? n.GetString() ?? "" : "";
            var app = item.TryGetProperty("AppPathName", out var a) ? a.GetString() ?? "" : "";
            long rate = 0;
            if (item.TryGetProperty("ThrottleRateAction", out var r) && r.ValueKind == JsonValueKind.Number)
                r.TryGetInt64(out rate);
            if (!string.IsNullOrEmpty(name))
                limits.Add(new ActiveLimit { PolicyName = name, AppMatch = app, RateBitsPerSecond = rate });
        }
        return limits;
    }

    public static async Task SetLimitAsync(string executableName, long bitsPerSecond)
    {
        var name = PolicyNameFor(executableName);
        var app = EscapePs(executableName);
        var cmd =
            $"$name='{name}';" +
            $"if (Get-NetQosPolicy -Name $name -PolicyStore ActiveStore -ErrorAction SilentlyContinue) " +
            $"{{ Set-NetQosPolicy -Name $name -PolicyStore ActiveStore -ThrottleRateActionBitsPerSecond {bitsPerSecond} | Out-Null }} " +
            $"else " +
            $"{{ New-NetQosPolicy -Name $name -PolicyStore ActiveStore -AppPathNameMatchCondition '{app}' -ThrottleRateActionBitsPerSecond {bitsPerSecond} | Out-Null }}";
        await RunPowerShellAsync(cmd);
    }

    public static async Task RemoveLimitAsync(string executableName) =>
        await RemovePolicyAsync(PolicyNameFor(executableName));

    public static async Task RemovePolicyAsync(string policyName)
    {
        var cmd = $"Remove-NetQosPolicy -Name '{EscapePs(policyName)}' -PolicyStore ActiveStore -Confirm:$false -ErrorAction SilentlyContinue";
        await RunPowerShellAsync(cmd);
    }

    public static async Task RemoveAllLimitsAsync()
    {
        var cmd =
            $"Get-NetQosPolicy -PolicyStore ActiveStore -ErrorAction SilentlyContinue " +
            $"| Where-Object {{ $_.Name -like '{PolicyPrefix}*' }} " +
            "| Remove-NetQosPolicy -PolicyStore ActiveStore -Confirm:$false -ErrorAction SilentlyContinue";
        await RunPowerShellAsync(cmd);
    }

    private static string EscapePs(string s) => s.Replace("'", "''");

    private static async Task<string> RunPowerShellAsync(string command)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{command}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        using var proc = Process.Start(psi)!;
        var stdout = await proc.StandardOutput.ReadToEndAsync();
        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();

        if (proc.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr) ? $"PowerShell завершился с кодом {proc.ExitCode}" : stderr.Trim());

        return stdout.Trim();
    }
}
