using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using StartDock.Models;

namespace StartDock.Services
{
    /// <summary>
    /// Lists installed apps the same way the real Start Menu's all-apps list does, via
    /// PowerShell's built-in `Get-StartApps` cmdlet — it already knows how to enumerate
    /// both Store/UWP packages and ordinary desktop apps registered in the Start Menu,
    /// which is exactly the "shell:AppsFolder" launch-id list Explorer itself uses.
    /// Shelling out to PowerShell is simpler and far more reliable than reimplementing
    /// package enumeration via WinRT interop, at the cost of a ~1 second startup delay
    /// the one time this list is requested (from the Add-icon picker).
    /// </summary>
    public static class StartAppsService
    {
        public static Task<List<InstalledAppInfo>> GetInstalledAppsAsync()
        {
            return Task.Run(GetInstalledApps);
        }

        private static List<InstalledAppInfo> GetInstalledApps()
        {
            var results = new List<InstalledAppInfo>();

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -NonInteractive -Command \"Get-StartApps | ConvertTo-Json -Compress\"",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                using var process = Process.Start(psi);
                if (process == null)
                    return results;

                string output = process.StandardOutput.ReadToEnd();
                if (!process.WaitForExit(15000))
                {
                    try { process.Kill(); } catch { /* best effort */ }
                    return results;
                }

                if (string.IsNullOrWhiteSpace(output))
                    return results;

                using var doc = JsonDocument.Parse(output);
                var root = doc.RootElement;

                if (root.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in root.EnumerateArray())
                        AddIfValid(results, item);
                }
                else if (root.ValueKind == JsonValueKind.Object)
                {
                    // ConvertTo-Json unwraps a single-item collection to a bare object
                    // rather than a one-element array.
                    AddIfValid(results, root);
                }
            }
            catch
            {
                // PowerShell missing/blocked by policy, or unexpected output — the caller
                // (the Add-icon dialog) falls back to plain file browsing in that case.
            }

            return results
                .Where(a => !string.IsNullOrWhiteSpace(a.Name) && !string.IsNullOrWhiteSpace(a.AppId))
                .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static void AddIfValid(List<InstalledAppInfo> results, JsonElement item)
        {
            string? name = item.TryGetProperty("Name", out var nameEl) ? nameEl.GetString() : null;
            string? appId = item.TryGetProperty("AppID", out var idEl) ? idEl.GetString() : null;

            if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(appId))
                results.Add(new InstalledAppInfo { Name = name!, AppId = appId! });
        }
    }
}
