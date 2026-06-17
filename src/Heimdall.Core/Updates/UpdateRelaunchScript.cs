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

using System.Text;

namespace Heimdall.Core.Updates;

/// <summary>
/// Inputs for the detached relauncher script: which installer to run, which
/// executable to relaunch, the process to wait on, and where the script lives.
/// </summary>
public sealed record UpdateRelaunchSpec(
    string InstallerPath,
    string TargetExecutablePath,
    int ProcessId,
    string ScriptPath,
    bool RequiresElevation = false,
    string InstallerArguments = UpdateRelaunchScript.DefaultInstallerArguments,
    int WaitTimeoutSeconds = UpdateRelaunchScript.DefaultWaitTimeoutSeconds,
    string? LogPath = null);

/// <summary>
/// Pure builder for the detached PowerShell relauncher used by the in-app updater.
/// The running executable is file-locked while the installer overwrites it and the
/// silent installer does not relaunch the app, so a hidden PowerShell host waits for
/// the app to exit, runs the installer, relaunches the app, and self-deletes.
/// Every member is deterministic from its inputs: no I/O, no process launching.
/// </summary>
public static class UpdateRelaunchScript
{
    /// <summary>Default silent installer arguments (Inno Setup compatible).</summary>
    public const string DefaultInstallerArguments = "/SILENT /NORESTART";

    /// <summary>Default number of seconds to wait for the app process to exit.</summary>
    public const int DefaultWaitTimeoutSeconds = 120;

    /// <summary>
    /// Flags passed to the PowerShell host so the relauncher runs hidden, without
    /// loading a profile and without interactive prompts.
    /// </summary>
    public const string PowerShellFlags =
        "-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden";

    /// <summary>
    /// Escapes a value for embedding inside a PowerShell single-quoted literal:
    /// a single quote is escaped by doubling it.
    /// </summary>
    public static string EscapeSingleQuoted(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Replace("'", "''", StringComparison.Ordinal);
    }

    /// <summary>
    /// Builds the command-line arguments for spawning the relauncher via a PowerShell
    /// host. The script path is wrapped in double quotes.
    /// </summary>
    public static string BuildPowerShellArguments(string scriptPath)
    {
        ArgumentNullException.ThrowIfNull(scriptPath);
        return $"{PowerShellFlags} -File \"{scriptPath}\"";
    }

    /// <summary>
    /// Builds the full relauncher script text from a spec. The relaunch and cleanup
    /// run inside a <c>finally</c> block so the user is never left without the app.
    /// </summary>
    public static string Build(UpdateRelaunchSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        var hasLog = !string.IsNullOrEmpty(spec.LogPath);
        var elevation = spec.RequiresElevation ? " -Verb RunAs" : string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference = 'Stop'");

        if (hasLog)
        {
            sb.AppendLine($"Start-Transcript -Path '{EscapeSingleQuoted(spec.LogPath!)}' -Append");
        }

        sb.AppendLine("try {");
        sb.AppendLine(
            $"    Wait-Process -Id {spec.ProcessId} -Timeout {spec.WaitTimeoutSeconds} -ErrorAction SilentlyContinue");
        sb.AppendLine(
            $"    Start-Process -FilePath '{EscapeSingleQuoted(spec.InstallerPath)}'{elevation} -ArgumentList '{EscapeSingleQuoted(spec.InstallerArguments)}' -Wait");
        sb.AppendLine("} catch {");
        sb.AppendLine("    Write-Error $_");
        sb.AppendLine("} finally {");
        sb.AppendLine(
            $"    Start-Process -FilePath '{EscapeSingleQuoted(spec.TargetExecutablePath)}'");

        if (hasLog)
        {
            sb.AppendLine("    Stop-Transcript");
        }

        sb.AppendLine(
            $"    Remove-Item -LiteralPath '{EscapeSingleQuoted(spec.ScriptPath)}' -Force -ErrorAction SilentlyContinue");
        sb.AppendLine("}");

        return sb.ToString();
    }
}
