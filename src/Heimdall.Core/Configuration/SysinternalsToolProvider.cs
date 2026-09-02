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
/// Detects Microsoft Sysinternals CLI tools on the local machine.
/// Scans PATH, common install directories, winget paths, and user-configured folders.
/// Redistribution is prohibited - tools must be installed by the user.
/// </summary>
public sealed class SysinternalsToolProvider : IExternalToolProvider
{
    public string Name => "Sysinternals";

    /// <summary>
    /// Known tools with their CLI metadata. Only tools with meaningful CLI output are included.
    /// </summary>
    private static readonly ToolTemplate[] s_templates =
    [
        new("PSEXEC",     "PsExec",      "psexec.exe",      "\\\\{Host} -accepteula -nobanner",        OutputFormat.Text,   true,  "ExtToolDescPsExec"),
        new("PSINFO",     "PsInfo",      "psinfo.exe",      "\\\\{Host} -accepteula -nobanner -h -s -d", OutputFormat.Text, false, "ExtToolDescPsInfo"),
        new("PSLIST",     "PsList",      "pslist.exe",      "\\\\{Host} -accepteula -nobanner",        OutputFormat.Text,   false, "ExtToolDescPsList"),
        new("PSLOGGEDON", "PsLoggedOn",  "psloggedon.exe",  "\\\\{Host} -accepteula -nobanner",        OutputFormat.Text,   false, "ExtToolDescPsLoggedOn"),
        new("PSSERVICE",  "PsService",   "psservice.exe",   "\\\\{Host} query -accepteula -nobanner",  OutputFormat.Text,   false, "ExtToolDescPsService"),
        new("PSKILL",     "PsKill",      "pskill.exe",      "\\\\{Host} -accepteula -nobanner",        OutputFormat.Text,   true,  "ExtToolDescPsKill"),
        new("PSSHUTDOWN", "PsShutdown",  "psshutdown.exe",  "\\\\{Host} -accepteula -nobanner",        OutputFormat.Text,   true,  "ExtToolDescPsShutdown"),
        new("PSPING",     "PsPing",      "psping.exe",      "-accepteula -nobanner {Host}:{Port}",     OutputFormat.Text,   false, "ExtToolDescPsPing"),
        new("TCPVCON",    "Tcpvcon",     "tcpvcon.exe",     "-a -c -nobanner -accepteula",             OutputFormat.Csv,    false, "ExtToolDescTcpvcon"),
        new("AUTORUNSC",  "Autorunsc",   "autorunsc.exe",   "-a * -c -m -s -accepteula -nobanner",     OutputFormat.Csv,    true,  "ExtToolDescAutorunsc"),
        new("SIGCHECK",   "Sigcheck",    "sigcheck.exe",    "-c -accepteula -nobanner",                OutputFormat.Csv,    false, "ExtToolDescSigcheck"),
        new("ACCESSCHK",  "AccessChk",   "accesschk.exe",   "-accepteula -nobanner",                   OutputFormat.Text,   true,  "ExtToolDescAccessChk"),
        new("HANDLE",     "Handle",      "handle.exe",      "-accepteula -nobanner",                   OutputFormat.Text,   true,  "ExtToolDescHandle"),
        new("LISTDLLS",   "ListDLLs",    "listdlls.exe",    "-accepteula -nobanner",                   OutputFormat.Text,   true,  "ExtToolDescListDlls"),
        new("DU",         "Disk Usage",  "du.exe",          "-accepteula -nobanner -c -l 1",           OutputFormat.Csv,    false, "ExtToolDescDu"),
        new("WHOIS",      "Whois",       "whois.exe",       "-accepteula -nobanner {Host}",            OutputFormat.Text,   false, "ExtToolDescWhois"),
    ];

    /// <summary>
    /// Standard directories where Sysinternals tools may be found.
    /// </summary>
    private static readonly string[] s_defaultSearchPaths =
    [
        @"C:\SysinternalsSuite",
        @"C:\Tools\Sysinternals",
        @"C:\Tools\SysinternalsSuite",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Sysinternals"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Packages"),
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

        // Also search PATH environment variable
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

            // Direct match
            var candidate = Path.Combine(dir, fileName);
            if (File.Exists(candidate)) return candidate;

            // 64-bit variant (e.g. psexec64.exe)
            var name64 = Path.GetFileNameWithoutExtension(fileName) + "64" + Path.GetExtension(fileName);
            var candidate64 = Path.Combine(dir, name64);
            if (Environment.Is64BitOperatingSystem && File.Exists(candidate64)) return candidate64;

            // WinGet nested package directories - search one level deep
            if (dir.Contains("WinGet", StringComparison.OrdinalIgnoreCase))
            {
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
                    Logging.FileLogger.Debug($"Sysinternals scan skipped inaccessible directory '{dir}'", ex);
                }
                catch (DirectoryNotFoundException ex)
                {
                    Logging.FileLogger.Debug($"Sysinternals scan skipped missing directory '{dir}'", ex);
                }
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
