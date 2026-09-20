using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace InternetLimiter;

public sealed class ProcessEntry
{
    public string ProcessName { get; init; } = "";
    public string ExecutableName => ProcessName + ".exe";
    public string DisplayName { get; init; } = "";
    public string ExecutablePath { get; init; } = "";
    public int ProcessCount { get; set; }
    public string LimitText { get; set; } = "—";
    public ImageSource? Icon { get; init; }

    public string PolicyName => QosService.PolicyNameFor(ExecutableName);

    public static List<ProcessEntry> Enumerate()
    {
        var groups = new Dictionary<string, ProcessEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in Process.GetProcesses())
        {
            try
            {
                var name = p.ProcessName;
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                if (groups.TryGetValue(name, out var existing))
                {
                    existing.ProcessCount++;
                    continue;
                }

                var path = TryGetPath(p);
                var title = p.MainWindowTitle;
                groups[name] = new ProcessEntry
                {
                    ProcessName = name,
                    DisplayName = string.IsNullOrWhiteSpace(title) ? name : $"{name} — {Truncate(title, 40)}",
                    ExecutablePath = path,
                    ProcessCount = 1,
                    Icon = TryGetIcon(path),
                };
            }
            catch { }
            finally { p.Dispose(); }
        }

        return groups.Values
            .OrderByDescending(e => !string.IsNullOrWhiteSpace(e.ExecutablePath))
            .ThenBy(e => e.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string TryGetPath(Process p)
    {
        try
        {
            var path = p.MainModule?.FileName ?? "";
            return File.Exists(path) ? path : "";
        }
        catch { return ""; }
    }

    private static ImageSource? TryGetIcon(string path)
    {
        IntPtr[] large = new IntPtr[1];
        try
        {
            if (string.IsNullOrEmpty(path))
                return null;
            if (ExtractIconEx(path, 0, large, null, 1) == 0 || large[0] == IntPtr.Zero)
                return null;
            var source = Imaging.CreateBitmapSourceFromHIcon(
                large[0], Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch { return null; }
        finally
        {
            if (large[0] != IntPtr.Zero)
                DestroyIcon(large[0]);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string lpszFile, int nIconIndex, IntPtr[]? phiconLarge, IntPtr[]? phiconSmall, uint nIcons);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
