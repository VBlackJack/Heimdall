/*
 * Copyright 2026 Julien Bombled
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

namespace Heimdall.Core.Configuration;

/// <summary>
/// Detects NirSoft CLI tools on the local machine.
/// Scans common install directories, NirLauncher locations, and user-configured folders.
/// NirSoft tools cannot be redistributed - users must download them from nirsoft.net.
/// </summary>
public sealed class NirSoftToolProvider : IExternalToolProvider
{
    public string Name => "NirSoft";

    /// <summary>
    /// Known tools with CLI metadata. Only tools with working CLI export are included.
    /// Password recovery tools are excluded (CLI removed by NirSoft since 2014).
    /// </summary>
    private static readonly ToolTemplate[] s_templates =
    [
        new("PINGINFOVIEW",   "Ping Info View",           "PingInfoView.exe",          "/scomma \"\"",        OutputFormat.Csv,  false, "ExtToolDescPingInfoView"),
        new("CURRPORTS",      "CurrPorts",                "cports.exe",                "/scomma \"\"",        OutputFormat.Csv,  false, "ExtToolDescCurrPorts"),
        new("NETWORKLATENCY", "Network Latency View",     "NetworkLatencyView.exe",    "/scomma \"\"",        OutputFormat.Csv,  false, "ExtToolDescNetworkLatency"),
        new("WAKEMELONLAN",   "WakeMeOnLan",              "WakeMeOnLan.exe",           "/wakeup {Host}",      OutputFormat.Text, false, "ExtToolDescWakeMeOnLan"),
        new("FASTRESOLVER",   "Fast Resolver",            "FastResolver.exe",          "/scomma \"\"",        OutputFormat.Csv,  false, "ExtToolDescFastResolver"),
        new("COUNTRYTR",      "Country TraceRoute",       "CountryTraceRoute.exe",     "/scomma \"\"",        OutputFormat.Csv,  false, "ExtToolDescCountryTrace"),
        new("DNSDATAVIEW",    "DNS Data View",            "DNSDataView.exe",           "/scomma \"\"",        OutputFormat.Csv,  false, "ExtToolDescDnsDataView"),
        new("NETRESVIEW",     "Net Resource View",        "NetResView.exe",            "/scomma \"\"",        OutputFormat.Csv,  false, "ExtToolDescNetResView"),
        new("NETINTERFACEVIEW","Network Interfaces View",  "NetworkInterfacesView.exe", "/scomma \"\"",        OutputFormat.Csv,  false, "ExtToolDescNetInterfaceView"),
        new("WIFIINFOVIEW",   "WiFi Info View",           "WifiInfoView.exe",          "/sjson \"\"",         OutputFormat.Json, false, "ExtToolDescWifiInfoView"),
        new("WIRELESSWATCH",  "Wireless Network Watcher", "WNetWatcher.exe",           "/scomma \"\"",        OutputFormat.Csv,  false, "ExtToolDescWirelessWatch"),
        new("EVENTLOGVIEW",   "Event Log View",           "FullEventLogView.exe",      "/sjson \"\"",         OutputFormat.Json, true,  "ExtToolDescEventLogView"),
        new("TASKSCHVIEW",    "Task Scheduler View",      "TaskSchedulerView.exe",     "/sjson \"\"",         OutputFormat.Json, true,  "ExtToolDescTaskSchView"),
        new("USBDEVIEW",      "USB Device View",          "USBDeview.exe",             "/scomma \"\"",        OutputFormat.Csv,  false, "ExtToolDescUsbDeview"),
        new("BLUESCREENVIEW", "BlueScreen View",          "BlueScreenView.exe",        "/scomma \"\"",        OutputFormat.Csv,  false, "ExtToolDescBlueScreenView"),
        new("PRODUKEY",       "ProduKey",                 "ProduKey.exe",              "/scomma \"\"",        OutputFormat.Csv,  false, "ExtToolDescProduKey"),
    ];

    /// <summary>
    /// Standard directories where NirSoft tools may be found.
    /// </summary>
    private static readonly string[] s_defaultSearchPaths =
    [
        @"C:\NirSoft",
        @"C:\Tools\NirSoft",
        @"C:\Tools\NirLauncher",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NirSoft"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NirSoft"),
    ];

    public IReadOnlyList<ExternalToolInfo> Scan(IEnumerable<string>? customSearchPaths = null)
    {
        var searchDirs = BuildSearchDirectories(customSearchPaths);
        var results = new List<ExternalToolInfo>();

        foreach (var template in s_templates)
        {
            var exePath = FindExecutable(template.FileName, searchDirs);
            if (exePath is null) continue;

            results.Add(new ExternalToolInfo
            {
                Id = template.Id,
                Name = template.Name,
                ExecutablePath = exePath,
                ProviderName = Name,
                DescriptionKey = template.DescriptionKey,
                Arguments = template.Arguments,
                OutputFormat = template.Format,
                RequiresElevation = template.RequiresElevation,
            });
        }

        return results;
    }

    private static List<string> BuildSearchDirectories(IEnumerable<string>? customPaths)
    {
        var dirs = new List<string>(s_defaultSearchPaths);
        if (customPaths is not null)
            dirs.AddRange(customPaths);

        // Also search PATH
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathEnv))
        {
            foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!dirs.Contains(dir, StringComparer.OrdinalIgnoreCase))
                    dirs.Add(dir);
            }
        }

        return dirs;
    }

    private static string? FindExecutable(string fileName, List<string> searchDirs)
    {
        foreach (var dir in searchDirs)
        {
            if (!Directory.Exists(dir)) continue;

            var candidate = Path.Combine(dir, fileName);
            if (File.Exists(candidate)) return candidate;

            // NirLauncher nests tools in subdirectories
            try
            {
                foreach (var subDir in Directory.EnumerateDirectories(dir))
                {
                    candidate = Path.Combine(subDir, fileName);
                    if (File.Exists(candidate)) return candidate;
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                Logging.FileLogger.Debug($"NirSoft scan skipped inaccessible directory '{dir}'", ex);
            }
            catch (DirectoryNotFoundException ex)
            {
                Logging.FileLogger.Debug($"NirSoft scan skipped missing directory '{dir}'", ex);
            }
        }

        return null;
    }

    private readonly record struct ToolTemplate(
        string Id,
        string Name,
        string FileName,
        string Arguments,
        OutputFormat Format,
        bool RequiresElevation,
        string DescriptionKey);
}
