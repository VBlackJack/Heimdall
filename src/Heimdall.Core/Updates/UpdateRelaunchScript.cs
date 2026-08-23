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

using System.Globalization;
using System.Text;

namespace Heimdall.Core.Updates;

/// <summary>
/// Inputs for the detached relauncher script: which installer to run, which
/// executable to relaunch, the process to wait on, and where the script lives.
/// </summary>
public sealed record UpdateRelaunchSpec(
    string InstallerPath,
    string ExpectedInstallerSha256,
    string TargetExecutablePath,
    int ProcessId,
    string ScriptPath,
    string StagingDirectory,
    bool RequiresElevation = false,
    string InstallerArguments = UpdateRelaunchScript.DefaultInstallerArguments,
    int WaitTimeoutSeconds = UpdateRelaunchScript.DefaultWaitTimeoutSeconds,
    string? LogPath = null,
    string? FailureRecordPath = null);

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
    /// Builds the command-line arguments for a trusted in-memory bootstrap. The
    /// bootstrap reads the script once under a deny-write handle, verifies its pinned
    /// SHA-256, and executes those verified bytes without reopening the script path.
    /// </summary>
    public static string BuildPowerShellArguments(
        string scriptPath,
        string expectedScriptSha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptPath);
        ValidateSha256(expectedScriptSha256, nameof(expectedScriptSha256));

        string bootstrap = BuildScriptBootstrap(scriptPath, expectedScriptSha256);
        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(bootstrap));
        return $"{PowerShellFlags} -EncodedCommand {encoded}";
    }

    /// <summary>
    /// Builds the full relauncher script text from a spec. The relaunch and cleanup
    /// run inside a <c>finally</c> block so the user is never left without the app.
    /// </summary>
    public static string Build(UpdateRelaunchSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ValidateSha256(spec.ExpectedInstallerSha256, nameof(spec.ExpectedInstallerSha256));

        var hasLog = !string.IsNullOrEmpty(spec.LogPath);
        var hasFailureRecord = !string.IsNullOrEmpty(spec.FailureRecordPath);
        var elevation = spec.RequiresElevation ? " -Verb RunAs" : string.Empty;
        string installerPath = EscapeSingleQuoted(spec.InstallerPath);
        string expectedInstallerSha256 = EscapeSingleQuoted(spec.ExpectedInstallerSha256);

        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine("$transcriptStarted = $false");
        sb.AppendLine("$installerStream = $null");
        sb.AppendLine($"$installerPath = '{installerPath}'");
        sb.AppendLine($"$expectedInstallerSha256 = '{expectedInstallerSha256}'");
        sb.AppendLine($"$updateStage = '{UpdateOutcomeStage.Preparation}'");
        sb.AppendLine("$installerExitCode = 0");
        sb.AppendLine("$installerExitKnown = 0");
        sb.AppendLine("try {");

        if (hasLog)
        {
            sb.AppendLine($"    Start-Transcript -Path '{EscapeSingleQuoted(spec.LogPath!)}' -Append");
            sb.AppendLine("    $transcriptStarted = $true");
        }

        sb.AppendLine(
            $"    Wait-Process -Id {spec.ProcessId} -Timeout {spec.WaitTimeoutSeconds} -ErrorAction SilentlyContinue");
        // Obtaining a verdict and judging one are separate things, and only the second
        // is a reason to refuse. Measured on a GitHub runner: Windows PowerShell 5.1 can
        // fail to import Microsoft.PowerShell.Security, so the command does not exist at
        // all, and under ErrorActionPreference Stop that aborted the update before the
        // installer ever launched - for a reason with nothing to do with the package.
        //
        // Treating an unobtainable verdict as fatal was also stricter than the policy
        // below states: NotSigned is accepted outright, so an unsigned installer already
        // passes here. What actually guards the boundary is the SHA-256 comparison that
        // follows, which is mandatory and cannot be skipped.
        sb.AppendLine("    $signature = $null");
        sb.AppendLine("    try {");
        sb.AppendLine(
            "        $signature = Get-AuthenticodeSignature -LiteralPath $installerPath");
        sb.AppendLine("    } catch {");
        sb.AppendLine(
            "        Write-Warning ('Authenticode verdict unavailable: ' + $_)");
        sb.AppendLine("    }");
        sb.AppendLine(
            "    if ($null -ne $signature -and $signature.Status -ne 'Valid' "
            + "-and $signature.Status -ne 'NotSigned') {");
        sb.AppendLine("        throw 'Installer Authenticode signature is present but invalid.'");
        sb.AppendLine("    }");
        sb.AppendLine(
            "    $installerStream = [System.IO.File]::Open($installerPath, "
            + "[System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, "
            + "[System.IO.FileShare]::Read)");
        sb.AppendLine("    $sha256 = [System.Security.Cryptography.SHA256]::Create()");
        sb.AppendLine("    try {");
        sb.AppendLine(
            "        $actualInstallerSha256 = "
            + "([System.BitConverter]::ToString($sha256.ComputeHash($installerStream))).Replace('-', '')");
        sb.AppendLine("    } finally {");
        sb.AppendLine("        $sha256.Dispose()");
        sb.AppendLine("    }");
        sb.AppendLine($"    $updateStage = '{UpdateOutcomeStage.IntegrityRejected}'");
        sb.AppendLine(
            "    if (-not [System.String]::Equals($actualInstallerSha256, "
            + "$expectedInstallerSha256, [System.StringComparison]::OrdinalIgnoreCase)) {");
        sb.AppendLine("        throw 'Installer SHA-256 verification failed at the execution boundary.'");
        sb.AppendLine("    }");
        sb.AppendLine($"    $updateStage = '{UpdateOutcomeStage.InstallerLaunch}'");
        sb.AppendLine(
            $"    $installerProcess = Start-Process -FilePath $installerPath{elevation} "
            + $"-ArgumentList '{EscapeSingleQuoted(spec.InstallerArguments)}' -Wait -PassThru");

        // The guard wraps the property ACCESS, not merely the null object. Reading
        // ExitCode can throw depending on how the process was started, and an
        // unreadable code must degrade to exactly today's behaviour rather than be
        // reported as a failure that was never measured.
        sb.AppendLine("    if ($null -ne $installerProcess) {");
        sb.AppendLine("        try {");
        sb.AppendLine("            $installerExitCode = [int]$installerProcess.ExitCode");
        sb.AppendLine("            $installerExitKnown = 1");
        sb.AppendLine("        } catch {");
        sb.AppendLine("            $installerExitKnown = 0");
        sb.AppendLine("        }");
        sb.AppendLine("    }");

        // Both conditions, deliberately: an unknown code is never a failure.
        sb.AppendLine(
            "    if ($installerExitKnown -eq 1 -and $installerExitCode -ne 0) {");
        sb.AppendLine($"        $updateStage = '{UpdateOutcomeStage.InstallerExit}'");
        sb.AppendLine("        throw 'Installer reported a non-zero exit code.'");
        sb.AppendLine("    }");
        sb.AppendLine("} catch {");

        // STRICTLY BEFORE Write-Error, and that ordering is the whole point. Under
        // $ErrorActionPreference = 'Stop' a Write-Error inside a catch is a TERMINATING
        // error: it abandons the rest of the block. Anything appended after it is dead
        // code that no text assertion can tell from live code.
        //
        // Wrapped in its own try/catch because failing to explain a failure must never
        // become a second failure - the finally below still has to bring the
        // application back.
        if (hasFailureRecord)
        {
            sb.AppendLine("    try {");
            sb.AppendLine(
                "        [System.IO.File]::WriteAllText('"
                + EscapeSingleQuoted(spec.FailureRecordPath!)
                + "', '{\"schemaVersion\":"
                + UpdateFailureRecord.CurrentSchemaVersion.ToString(CultureInfo.InvariantCulture)
                + ",\"stage\":\"' + $updateStage + '\",\"installerExitCode\":' "
                + "+ $installerExitCode + ',\"installerExitCodeKnown\":' "
                + "+ $installerExitKnown + '}')");
            sb.AppendLine("    } catch {");
            sb.AppendLine("    }");
        }

        sb.AppendLine("    Write-Error $_");
        sb.AppendLine("} finally {");
        sb.AppendLine("    if ($null -ne $installerStream) {");
        sb.AppendLine("        $installerStream.Dispose()");
        sb.AppendLine("    }");
        sb.AppendLine("    try {");
        sb.AppendLine(
            $"        Start-Process -FilePath '{EscapeSingleQuoted(spec.TargetExecutablePath)}'");
        sb.AppendLine("    } catch {");
        sb.AppendLine("        Write-Warning $_");
        sb.AppendLine("    }");

        if (hasLog)
        {
            sb.AppendLine("    if ($transcriptStarted) {");
            sb.AppendLine("        Stop-Transcript -ErrorAction SilentlyContinue");
            sb.AppendLine("    }");
        }

        sb.AppendLine(
            $"    Remove-Item -LiteralPath '{installerPath}' -Force -ErrorAction SilentlyContinue");
        sb.AppendLine(
            $"    Remove-Item -LiteralPath '{EscapeSingleQuoted(spec.ScriptPath)}' -Force -ErrorAction SilentlyContinue");
        sb.AppendLine(
            $"    Remove-Item -LiteralPath '{EscapeSingleQuoted(spec.StagingDirectory)}' "
            + "-Force -ErrorAction SilentlyContinue");
        sb.AppendLine("}");

        return sb.ToString();
    }

    internal static string BuildScriptBootstrap(
        string scriptPath,
        string expectedScriptSha256)
    {
        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine(
            "$securityModulePath = Join-Path $PSHOME "
            + "'Modules\\Microsoft.PowerShell.Security\\Microsoft.PowerShell.Security.psd1'");
        sb.AppendLine("Import-Module -Name $securityModulePath -ErrorAction Stop");
        sb.AppendLine($"$scriptPath = '{EscapeSingleQuoted(scriptPath)}'");
        sb.AppendLine(
            $"$expectedScriptSha256 = '{EscapeSingleQuoted(expectedScriptSha256)}'");
        sb.AppendLine(
            "$scriptStream = [System.IO.File]::Open($scriptPath, "
            + "[System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, "
            + "[System.IO.FileShare]::Read)");
        sb.AppendLine("try {");
        sb.AppendLine("    $memory = New-Object System.IO.MemoryStream");
        sb.AppendLine("    try {");
        sb.AppendLine("        $scriptStream.CopyTo($memory)");
        sb.AppendLine("        $scriptBytes = $memory.ToArray()");
        sb.AppendLine("    } finally {");
        sb.AppendLine("        $memory.Dispose()");
        sb.AppendLine("    }");
        sb.AppendLine("} finally {");
        sb.AppendLine("    $scriptStream.Dispose()");
        sb.AppendLine("}");
        sb.AppendLine("$sha256 = [System.Security.Cryptography.SHA256]::Create()");
        sb.AppendLine("try {");
        sb.AppendLine(
            "    $actualScriptSha256 = "
            + "([System.BitConverter]::ToString($sha256.ComputeHash($scriptBytes))).Replace('-', '')");
        sb.AppendLine("} finally {");
        sb.AppendLine("    $sha256.Dispose()");
        sb.AppendLine("}");
        sb.AppendLine(
            "if (-not [System.String]::Equals($actualScriptSha256, "
            + "$expectedScriptSha256, [System.StringComparison]::OrdinalIgnoreCase)) {");
        sb.AppendLine("    throw 'Relauncher script SHA-256 verification failed.'");
        sb.AppendLine("}");
        sb.AppendLine("$scriptText = [System.Text.Encoding]::UTF8.GetString($scriptBytes)");
        sb.AppendLine("Invoke-Expression $scriptText");
        return sb.ToString();
    }

    private static void ValidateSha256(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length != 64 || !value.All(Uri.IsHexDigit))
        {
            throw new ArgumentException(
                "Expected SHA-256 must contain exactly 64 hexadecimal characters.",
                parameterName);
        }
    }
}
