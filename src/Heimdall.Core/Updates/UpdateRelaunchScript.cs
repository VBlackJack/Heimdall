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
    /// Win32 <c>ERROR_CANCELLED</c>: what ShellExecute reports when the user declines
    /// the elevation consent prompt. It surfaces from <c>Start-Process -Verb RunAs</c>
    /// as a <c>Win32Exception</c>, usually wrapped in an <c>InvalidOperationException</c>.
    /// </summary>
    public const int Win32ErrorCancelled = 1223;

    /// <summary>
    /// The variable through which the script tells the bootstrap that the relaunch is
    /// now its responsibility. The bootstrap initialises it to <c>$false</c> and the
    /// script's very first statement sets it to <c>$true</c>; both run in one scope
    /// because the bootstrap executes the script with <c>Invoke-Expression</c>.
    /// </summary>
    /// <remarks>
    /// This is what lets the two guarantees compose. A script that never starts - the
    /// file was tampered with, or it does not parse, so no statement of it runs - leaves
    /// the flag false and the bootstrap brings the application back. A script that
    /// starts and then fails has its own <c>finally</c> for that, and the flag stops the
    /// bootstrap from starting a second instance on top of it.
    /// </remarks>
    public const string RelaunchOwnedVariable = "relaunchOwnedByScript";

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
    /// <param name="scriptPath">The script the bootstrap reads and verifies.</param>
    /// <param name="expectedScriptSha256">The digest the script bytes must match.</param>
    /// <param name="targetExecutablePath">
    /// The application to bring back if the script never gets to run. The script has
    /// its own relaunch for every failure after it starts; this one covers the failures
    /// before that, which used to leave the user with no application at all.
    /// </param>
    /// <param name="failureRecordPath">
    /// Where to record a failure that happened before the script started, so the next
    /// startup can say so. Null records nothing.
    /// </param>
    public static string BuildPowerShellArguments(
        string scriptPath,
        string expectedScriptSha256,
        string targetExecutablePath,
        string? failureRecordPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptPath);
        ValidateSha256(expectedScriptSha256, nameof(expectedScriptSha256));
        ArgumentException.ThrowIfNullOrWhiteSpace(targetExecutablePath);

        string bootstrap = BuildScriptBootstrap(
            scriptPath,
            expectedScriptSha256,
            targetExecutablePath,
            failureRecordPath);
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
        ValidateLiteral(spec.InstallerPath, nameof(spec.InstallerPath));
        ValidateLiteral(spec.TargetExecutablePath, nameof(spec.TargetExecutablePath));
        ValidateLiteral(spec.ScriptPath, nameof(spec.ScriptPath));
        ValidateLiteral(spec.StagingDirectory, nameof(spec.StagingDirectory));
        ValidateLiteral(spec.InstallerArguments, nameof(spec.InstallerArguments));
        ValidateOptionalLiteral(spec.LogPath, nameof(spec.LogPath));
        ValidateOptionalLiteral(spec.FailureRecordPath, nameof(spec.FailureRecordPath));

        bool hasLog = !string.IsNullOrEmpty(spec.LogPath);
        bool hasFailureRecord = !string.IsNullOrEmpty(spec.FailureRecordPath);
        string elevation = spec.RequiresElevation ? " -Verb RunAs" : string.Empty;
        string installerPath = EscapeSingleQuoted(spec.InstallerPath);
        string expectedInstallerSha256 = EscapeSingleQuoted(spec.ExpectedInstallerSha256);

        StringBuilder sb = new();

        // The first statement, before anything that can fail: from here on the script
        // owns the relaunch, and the bootstrap must not start a second instance.
        sb.AppendLine($"${RelaunchOwnedVariable} = $true");
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
            // Losing the transcript must not lose the update: a log path held open by
            // an earlier relauncher, or transcription disabled by policy, is not a
            // reason to leave the user on the old version.
            sb.AppendLine("    try {");
            sb.AppendLine($"        Start-Transcript -Path '{EscapeSingleQuoted(spec.LogPath!)}' -Append");
            sb.AppendLine("        $transcriptStarted = $true");
            sb.AppendLine("    } catch {");
            sb.AppendLine("        Write-Warning ('Transcript unavailable: ' + $_)");
            sb.AppendLine("    }");
        }

        sb.AppendLine(
            $"    Wait-Process -Id {spec.ProcessId} -Timeout {spec.WaitTimeoutSeconds} -ErrorAction SilentlyContinue");

        // Wait-Process reports a timeout as an error, which the line above suppresses
        // on purpose because a process that had already exited reports one too. So the
        // two outcomes are told apart here, by asking. An installer started over a
        // live application does not update it: Inno Setup force-closes the process it
        // finds holding the files, mid-session, which is the one thing this script
        // exists to avoid.
        sb.AppendLine(
            $"    if ($null -ne (Get-Process -Id {spec.ProcessId} -ErrorAction SilentlyContinue)) {{");
        sb.AppendLine($"        $updateStage = '{UpdateOutcomeStage.ApplicationStillRunning}'");
        sb.AppendLine("        throw 'The application is still running after the wait timeout.'");
        sb.AppendLine("    }");

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

        // A declined consent prompt never reaches the installer, so no Inno exit code
        // can describe it: it arrives as ERROR_CANCELLED from ShellExecute, somewhere in
        // the exception chain of the launch. Only the launch stage is inspected; the
        // same code raised anywhere else would be a coincidence, not a decision.
        sb.AppendLine($"    if ($updateStage -eq '{UpdateOutcomeStage.InstallerLaunch}') {{");
        sb.AppendLine("        $inspected = $_.Exception");
        sb.AppendLine("        while ($null -ne $inspected) {");
        sb.AppendLine(
            "            if ($inspected -is [System.ComponentModel.Win32Exception] "
            + $"-and $inspected.NativeErrorCode -eq {Win32ErrorCancelled.ToString(CultureInfo.InvariantCulture)}) {{");
        sb.AppendLine($"                $updateStage = '{UpdateOutcomeStage.ElevationDeclined}'");
        sb.AppendLine("            }");
        sb.AppendLine("            $inspected = $inspected.InnerException");
        sb.AppendLine("        }");
        sb.AppendLine("    }");

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
            AppendFailureRecordWrite(sb, "    ", spec.FailureRecordPath!, stageExpression: "$updateStage");
        }

        sb.AppendLine("    Write-Error $_");
        sb.AppendLine("} finally {");
        sb.AppendLine("    if ($null -ne $installerStream) {");
        sb.AppendLine("        $installerStream.Dispose()");
        sb.AppendLine("    }");

        // The one case in which the application must NOT be started: it never exited,
        // so it is still there. A second instance would only hand over to it.
        sb.AppendLine($"    if ($updateStage -ne '{UpdateOutcomeStage.ApplicationStillRunning}') {{");
        sb.AppendLine("        try {");
        sb.AppendLine(
            $"            Start-Process -FilePath '{EscapeSingleQuoted(spec.TargetExecutablePath)}'");
        sb.AppendLine("        } catch {");
        sb.AppendLine("            Write-Warning $_");
        sb.AppendLine("        }");
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

        // -Recurse, because without it a non-empty directory asks for confirmation, and
        // under -NonInteractive that request is an invalid-operation error from the host
        // that -ErrorAction SilentlyContinue does not cover. Measured: it truncated the
        // rest of the finally and left the directory, with the installer inside it.
        sb.AppendLine(
            $"    Remove-Item -LiteralPath '{EscapeSingleQuoted(spec.StagingDirectory)}' "
            + "-Recurse -Force -ErrorAction SilentlyContinue");
        sb.AppendLine("}");

        return sb.ToString();
    }

    /// <summary>
    /// The bootstrap that <c>-EncodedCommand</c> runs. It has its own failure path,
    /// because the script's guarantees only start once the script starts.
    /// </summary>
    /// <remarks>
    /// Everything before <c>Invoke-Expression</c> can fail - a script file that was
    /// deleted, locked or tampered with, a script that does not parse - and every one
    /// of those failures used to end the host silently, with the application already
    /// gone. The <c>finally</c> here brings it back whenever the script did not get far
    /// enough to do so itself, and the <c>catch</c> records a preparation failure so the
    /// next startup can say what happened.
    /// </remarks>
    internal static string BuildScriptBootstrap(
        string scriptPath,
        string expectedScriptSha256,
        string targetExecutablePath,
        string? failureRecordPath)
    {
        StringBuilder sb = new();
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine($"${RelaunchOwnedVariable} = $false");
        sb.AppendLine($"$relaunchTarget = '{EscapeSingleQuoted(targetExecutablePath)}'");
        sb.AppendLine("try {");

        // Advisory, matching the policy the script itself states: the Authenticode
        // verdict is optional and the SHA-256 comparison is the gate. This import is the
        // exact module whose failure to load under Windows PowerShell 5.1 is documented
        // (BL-0091), and the host started here is always Windows PowerShell.
        sb.AppendLine(
            "    $securityModulePath = Join-Path $PSHOME "
            + "'Modules\\Microsoft.PowerShell.Security\\Microsoft.PowerShell.Security.psd1'");
        sb.AppendLine("    try {");
        sb.AppendLine("        Import-Module -Name $securityModulePath -ErrorAction Stop");
        sb.AppendLine("    } catch {");
        sb.AppendLine("        Write-Warning ('Security module unavailable: ' + $_)");
        sb.AppendLine("    }");
        sb.AppendLine($"    $scriptPath = '{EscapeSingleQuoted(scriptPath)}'");
        sb.AppendLine(
            $"    $expectedScriptSha256 = '{EscapeSingleQuoted(expectedScriptSha256)}'");
        sb.AppendLine(
            "    $scriptStream = [System.IO.File]::Open($scriptPath, "
            + "[System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, "
            + "[System.IO.FileShare]::Read)");
        sb.AppendLine("    try {");
        sb.AppendLine("        $memory = New-Object System.IO.MemoryStream");
        sb.AppendLine("        try {");
        sb.AppendLine("            $scriptStream.CopyTo($memory)");
        sb.AppendLine("            $scriptBytes = $memory.ToArray()");
        sb.AppendLine("        } finally {");
        sb.AppendLine("            $memory.Dispose()");
        sb.AppendLine("        }");
        sb.AppendLine("    } finally {");
        sb.AppendLine("        $scriptStream.Dispose()");
        sb.AppendLine("    }");
        sb.AppendLine("    $sha256 = [System.Security.Cryptography.SHA256]::Create()");
        sb.AppendLine("    try {");
        sb.AppendLine(
            "        $actualScriptSha256 = "
            + "([System.BitConverter]::ToString($sha256.ComputeHash($scriptBytes))).Replace('-', '')");
        sb.AppendLine("    } finally {");
        sb.AppendLine("        $sha256.Dispose()");
        sb.AppendLine("    }");
        sb.AppendLine(
            "    if (-not [System.String]::Equals($actualScriptSha256, "
            + "$expectedScriptSha256, [System.StringComparison]::OrdinalIgnoreCase)) {");
        sb.AppendLine("        throw 'Relauncher script SHA-256 verification failed.'");
        sb.AppendLine("    }");
        sb.AppendLine("    $scriptText = [System.Text.Encoding]::UTF8.GetString($scriptBytes)");
        sb.AppendLine("    Invoke-Expression $scriptText");
        sb.AppendLine("} catch {");

        // Only when the script never started: once it has, its own record is the
        // account of what happened, and this one would overwrite it with less.
        if (!string.IsNullOrEmpty(failureRecordPath))
        {
            sb.AppendLine($"    if (-not ${RelaunchOwnedVariable}) {{");
            AppendFailureRecordWrite(
                sb,
                "        ",
                failureRecordPath,
                stageExpression: $"'{UpdateOutcomeStage.Preparation}'",
                scriptVariablesDefined: false);
            sb.AppendLine("    }");
        }

        sb.AppendLine("    Write-Error $_");
        sb.AppendLine("} finally {");
        sb.AppendLine($"    if (-not ${RelaunchOwnedVariable}) {{");
        sb.AppendLine("        try {");
        sb.AppendLine("            Start-Process -FilePath $relaunchTarget");
        sb.AppendLine("        } catch {");
        sb.AppendLine("            Write-Warning $_");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    /// <summary>
    /// Emits the guarded write of a failure record. One emitter for both the script and
    /// the bootstrap, so the two cannot drift into writing different JSON.
    /// </summary>
    /// <remarks>
    /// The stage is an expression rather than a value because the script reports the
    /// stage it reached, held in a variable, while the bootstrap always reports
    /// preparation. The exit-code fields come from the script's variables only when
    /// the script defined them: the bootstrap writes before the script ran, and an
    /// undefined variable concatenates as empty text, which would leave a hole in the
    /// JSON on the one run that matters.
    /// </remarks>
    private static void AppendFailureRecordWrite(
        StringBuilder sb,
        string indent,
        string failureRecordPath,
        string stageExpression,
        bool scriptVariablesDefined = true)
    {
        string exitCodeExpression = scriptVariablesDefined ? "$installerExitCode" : "'0'";
        string exitKnownExpression = scriptVariablesDefined ? "$installerExitKnown" : "'0'";

        sb.AppendLine($"{indent}try {{");
        sb.AppendLine(
            $"{indent}    [System.IO.File]::WriteAllText('"
            + EscapeSingleQuoted(failureRecordPath)
            + "', '{\"schemaVersion\":"
            + UpdateFailureRecord.CurrentSchemaVersion.ToString(CultureInfo.InvariantCulture)
            + ",\"stage\":\"' + " + stageExpression + " + '\",\"installerExitCode\":' "
            + "+ " + exitCodeExpression + " + ',\"installerExitCodeKnown\":' "
            + "+ " + exitKnownExpression + " + '}')");
        sb.AppendLine($"{indent}}} catch {{");
        sb.AppendLine($"{indent}}}");
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

    /// <summary>
    /// Refuses a value that could not survive a single-quoted literal intact. Quotes are
    /// escaped; control characters are not representable and are refused instead.
    /// </summary>
    /// <remarks>
    /// A single-quoted PowerShell literal spans lines, so a newline cannot break out of
    /// it. It can, however, make the emitted text mean something other than a path, and
    /// no Windows path legitimately contains one. Refusing at the boundary keeps the
    /// invariant "every interpolated value is a quoted literal" checkable in one place.
    /// </remarks>
    private static void ValidateLiteral(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Any(char.IsControl))
        {
            throw new ArgumentException(
                "Values embedded in the relauncher script must not contain control characters.",
                parameterName);
        }
    }

    private static void ValidateOptionalLiteral(string? value, string parameterName)
    {
        if (value is not null)
        {
            ValidateLiteral(value, parameterName);
        }
    }
}
