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

using System.Diagnostics;
using System.IO;

namespace Heimdall.App.Services;

/// <summary>
/// Module directories that decide the PSModulePath handed to Windows PowerShell.
/// </summary>
/// <param name="PowerShell7">PowerShell 7's personal and shared module directories.</param>
/// <param name="WindowsPowerShellDefaults">
/// The module path Windows PowerShell builds for itself when none is inherited.
/// </param>
internal sealed record PowerShellModuleRoots(
    IReadOnlyCollection<string> PowerShell7,
    IReadOnlyList<string> WindowsPowerShellDefaults)
{
    private const string PowerShell7DirectoryName = "PowerShell";
    private const string WindowsPowerShellDirectoryName = "WindowsPowerShell";
    private const string WindowsPowerShellVersionDirectoryName = "v1.0";

    /// <summary>Resolves the roots for the current user and machine.</summary>
    public static PowerShellModuleRoots ForCurrentUser()
    {
        string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        return new PowerShellModuleRoots(
            [
                Path.Combine(documents, PowerShell7DirectoryName, WindowsPowerShellModulePath.ModulesDirectoryName),
                Path.Combine(programFiles, PowerShell7DirectoryName, WindowsPowerShellModulePath.ModulesDirectoryName)
            ],
            [
                Path.Combine(documents, WindowsPowerShellDirectoryName, WindowsPowerShellModulePath.ModulesDirectoryName),
                Path.Combine(programFiles, WindowsPowerShellDirectoryName, WindowsPowerShellModulePath.ModulesDirectoryName),
                Path.Combine(
                    Environment.SystemDirectory,
                    WindowsPowerShellDirectoryName,
                    WindowsPowerShellVersionDirectoryName,
                    WindowsPowerShellModulePath.ModulesDirectoryName)
            ]);
    }
}

/// <summary>
/// Derives the PSModulePath a Windows PowerShell child should get from the one Heimdall inherited.
/// </summary>
/// <remarks>
/// Heimdall started from a PowerShell 7 session inherits PowerShell 7's module path, and a
/// Windows PowerShell child given it verbatim loads PowerShell 7's PSReadLine. Under an AllSigned
/// execution policy that module's format file asks "Do you want to run software from this
/// untrusted publisher?" before anything else runs (CI run 36765683841). The update relauncher
/// given it loads PowerShell 7's manifests for Microsoft.PowerShell.Utility, Management and Host,
/// and cannot load its Security module at all, so Get-AuthenticodeSignature does not exist
/// (CI run 36870815225). PowerShell 7 itself
/// removes its personal, shared and $PSHOME module directories when it starts powershell.exe and
/// keeps every other entry; this does the same.
/// </remarks>
internal static class WindowsPowerShellModulePath
{
    /// <summary>Name of the environment variable holding the module search path.</summary>
    internal const string VariableName = "PSModulePath";

    /// <summary>Leaf name of every PowerShell module directory.</summary>
    internal const string ModulesDirectoryName = "Modules";

    private const string WindowsPowerShellExecutableName = "powershell.exe";
    private const string PowerShell7ExecutableName = "pwsh.exe";
    private const char EntrySeparator = ';';

    /// <summary>Whether <paramref name="executable"/> is Windows PowerShell.</summary>
    internal static bool IsWindowsPowerShell(string executable)
    {
        ArgumentNullException.ThrowIfNull(executable);
        return string.Equals(
            Path.GetFileName(executable),
            WindowsPowerShellExecutableName,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns <paramref name="inheritedModulePath"/> without PowerShell 7's entries, or null when
    /// nothing was inherited and Windows PowerShell should build its own.
    /// </summary>
    /// <param name="inheritedModulePath">The PSModulePath of the Heimdall process.</param>
    /// <param name="roots">PowerShell 7's fixed module directories and Windows PowerShell's defaults.</param>
    /// <param name="fileExists">
    /// Tells a PowerShell 7 home apart: its module directory sits beside pwsh.exe, wherever it is installed.
    /// </param>
    internal static string? FromInherited(
        string? inheritedModulePath,
        PowerShellModuleRoots roots,
        Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(fileExists);
        if (string.IsNullOrWhiteSpace(inheritedModulePath))
        {
            return null;
        }

        HashSet<string> powerShell7Entries = new(
            roots.PowerShell7.Select(Normalize),
            StringComparer.OrdinalIgnoreCase);
        List<string> kept = [];
        foreach (string rawEntry in inheritedModulePath.Split(EntrySeparator))
        {
            string entry = Normalize(rawEntry);
            if (entry.Length == 0
                || powerShell7Entries.Contains(entry)
                || IsPowerShell7Home(entry, fileExists))
            {
                continue;
            }

            kept.Add(rawEntry.Trim());
        }

        // Windows PowerShell keeps an inherited module path verbatim, an empty one included.
        return string.Join(EntrySeparator, kept.Count > 0 ? kept : roots.WindowsPowerShellDefaults);
    }

    /// <summary>
    /// Gives a Windows PowerShell child the module path <see cref="FromInherited"/> derives, and
    /// leaves any other host's environment as it found it.
    /// </summary>
    /// <param name="startInfo">The launch to adjust; its FileName decides whether anything changes.</param>
    /// <param name="inheritedModulePath">The PSModulePath the launching process inherited.</param>
    /// <param name="roots">PowerShell 7's fixed module directories and Windows PowerShell's defaults.</param>
    /// <param name="fileExists">Tells a PowerShell 7 home apart, as in <see cref="FromInherited"/>.</param>
    /// <remarks>
    /// Nothing inherited removes the variable rather than writing it empty: Windows PowerShell
    /// keeps an inherited value verbatim, an empty one included, and builds its own defaults only
    /// when there is none.
    /// </remarks>
    internal static void ApplyTo(
        ProcessStartInfo startInfo,
        string? inheritedModulePath,
        PowerShellModuleRoots roots,
        Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        if (!IsWindowsPowerShell(startInfo.FileName))
        {
            return;
        }

        string? modulePath = FromInherited(inheritedModulePath, roots, fileExists);
        if (modulePath is null)
        {
            startInfo.Environment.Remove(VariableName);
            return;
        }

        startInfo.Environment[VariableName] = modulePath;
    }

    /// <summary>
    /// Returns the statement that gives a running Windows PowerShell the module path
    /// <see cref="FromInherited"/> derives, for a launch that cannot carry an environment.
    /// </summary>
    /// <param name="inheritedModulePath">The PSModulePath the launching process inherited.</param>
    /// <param name="roots">PowerShell 7's fixed module directories and Windows PowerShell's defaults.</param>
    /// <param name="fileExists">Tells a PowerShell 7 home apart, as in <see cref="FromInherited"/>.</param>
    /// <remarks>
    /// An elevated launch goes through ShellExecute, which takes no environment. Set as the first
    /// statement, the module path decides every module the script auto-loads afterwards: measured
    /// on 2026-10-01, Get-Service and Start-Service then come from System32's
    /// Microsoft.PowerShell.Management instead of PowerShell 7's. It cannot reach what the host
    /// loads before the script runs. Nothing inherited writes Windows PowerShell's defaults, since
    /// an elevated child may otherwise not start from the same environment.
    /// </remarks>
    internal static string ScriptAssignment(
        string? inheritedModulePath,
        PowerShellModuleRoots roots,
        Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(roots);
        string modulePath = FromInherited(inheritedModulePath, roots, fileExists)
            ?? string.Join(EntrySeparator, roots.WindowsPowerShellDefaults);
        return $"$env:{VariableName} = {PowerShellSingleQuotedString.Quote(modulePath)}";
    }

    private static bool IsPowerShell7Home(string entry, Func<string, bool> fileExists)
    {
        if (!Path.IsPathFullyQualified(entry)
            || !string.Equals(Path.GetFileName(entry), ModulesDirectoryName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string? home = Path.GetDirectoryName(entry);
        return !string.IsNullOrEmpty(home)
            && fileExists(Path.Combine(home, PowerShell7ExecutableName));
    }

    private static string Normalize(string entry)
    {
        return entry.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
