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
using Heimdall.Core.Updates;

namespace Heimdall.Core.Tests;

public sealed class UpdateRelaunchScriptTests
{
    private static readonly string InstallerSha256 = new('a', 64);
    private static readonly string ScriptSha256 = new('b', 64);

    private const string FailureRecordPath = @"C:\Temp\updates\update-failure.json";

    private static UpdateRelaunchSpec SampleSpec(
        bool requiresElevation = false,
        string? logPath = null,
        string installerPath = @"C:\Temp\HeimdallSetup.exe",
        string targetExecutablePath = @"C:\Program Files\Heimdall\Heimdall.exe",
        string scriptPath = @"C:\Temp\heimdall_relaunch.ps1",
        string? failureRecordPath = null) =>
        new(
            InstallerPath: installerPath,
            ExpectedInstallerSha256: InstallerSha256,
            TargetExecutablePath: targetExecutablePath,
            ProcessId: 4242,
            ScriptPath: scriptPath,
            StagingDirectory: @"C:\Temp\update-stage",
            RequiresElevation: requiresElevation,
            LogPath: logPath,
            FailureRecordPath: failureRecordPath);

    [Fact]
    public void EscapeSingleQuoted_DoublesSingleQuotes()
    {
        Assert.Equal("it''s", UpdateRelaunchScript.EscapeSingleQuoted("it's"));
        Assert.Equal("''a''b''", UpdateRelaunchScript.EscapeSingleQuoted("'a'b'"));
    }

    [Fact]
    public void EscapeSingleQuoted_LeavesNormalPathUnchanged()
    {
        const string path = @"C:\Program Files\Heimdall\Heimdall.exe";
        Assert.Equal(path, UpdateRelaunchScript.EscapeSingleQuoted(path));
    }

    [Fact]
    public void EscapeSingleQuoted_HandlesEmptyString()
    {
        Assert.Equal(string.Empty, UpdateRelaunchScript.EscapeSingleQuoted(string.Empty));
    }

    [Fact]
    public void BuildPowerShellArguments_ContainsHostFlagsAndVerifiedBootstrap()
    {
        const string scriptPath = @"C:\Temp\heimdall_relaunch.ps1";
        var args = UpdateRelaunchScript.BuildPowerShellArguments(
            scriptPath,
            ScriptSha256);

        Assert.Contains("-NoProfile", args);
        Assert.Contains("-NonInteractive", args);
        Assert.Contains("-ExecutionPolicy Bypass", args);
        Assert.Contains("-WindowStyle Hidden", args);
        Assert.Contains("-EncodedCommand ", args);

        string encoded = args[(args.IndexOf("-EncodedCommand ", StringComparison.Ordinal)
            + "-EncodedCommand ".Length)..];
        string bootstrap = Encoding.Unicode.GetString(Convert.FromBase64String(encoded));
        Assert.Contains(
            "Join-Path $PSHOME 'Modules\\Microsoft.PowerShell.Security\\Microsoft.PowerShell.Security.psd1'",
            bootstrap);
        Assert.Contains("Import-Module -Name $securityModulePath -ErrorAction Stop", bootstrap);
        Assert.Contains($"$scriptPath = '{scriptPath}'", bootstrap);
        Assert.Contains($"$expectedScriptSha256 = '{ScriptSha256}'", bootstrap);
        Assert.Contains("[System.IO.FileShare]::Read", bootstrap);
        Assert.Contains("$scriptStream.CopyTo($memory)", bootstrap);
        Assert.Contains("$sha256.ComputeHash($scriptBytes)", bootstrap);
        Assert.Contains("Invoke-Expression $scriptText", bootstrap);
        Assert.DoesNotContain("-File", bootstrap);
    }

    [Fact]
    public void Build_EmbedsProcessIdTimeoutPathsAndArguments()
    {
        var spec = SampleSpec();
        var script = UpdateRelaunchScript.Build(spec);

        Assert.Contains("-Id 4242", script);
        Assert.Contains($"-Timeout {UpdateRelaunchScript.DefaultWaitTimeoutSeconds}", script);
        Assert.Contains($"'{spec.InstallerPath}'", script);
        Assert.Contains($"'{spec.TargetExecutablePath}'", script);
        Assert.Contains($"'{spec.ScriptPath}'", script);
        Assert.Contains($"'{spec.ExpectedInstallerSha256}'", script);
        Assert.Contains($"-ArgumentList '{UpdateRelaunchScript.DefaultInstallerArguments}'", script);
    }

    [Fact]
    public void Build_EmitsSelfDeleteOfScriptPath()
    {
        var spec = SampleSpec();
        var script = UpdateRelaunchScript.Build(spec);

        Assert.Contains(
            $"Remove-Item -LiteralPath '{spec.ScriptPath}' -Force -ErrorAction SilentlyContinue",
            script);
        Assert.Contains(
            $"Remove-Item -LiteralPath '{spec.InstallerPath}' -Force -ErrorAction SilentlyContinue",
            script);
        Assert.Contains(
            $"Remove-Item -LiteralPath '{spec.StagingDirectory}' -Force -ErrorAction SilentlyContinue",
            script);
    }

    [Fact]
    public void Build_WithElevation_EmitsRunAsVerb()
    {
        var script = UpdateRelaunchScript.Build(SampleSpec(requiresElevation: true));
        Assert.Contains("-Verb RunAs", script);
    }

    [Fact]
    public void Build_WithoutElevation_OmitsRunAsVerb()
    {
        var script = UpdateRelaunchScript.Build(SampleSpec(requiresElevation: false));
        Assert.DoesNotContain("-Verb RunAs", script);
    }

    [Fact]
    public void Build_WithLogPath_EmitsTranscriptStartAndStop()
    {
        const string logPath = @"C:\Temp\heimdall_update.log";
        var script = UpdateRelaunchScript.Build(SampleSpec(logPath: logPath));

        Assert.Contains($"Start-Transcript -Path '{logPath}' -Append", script);
        Assert.Contains("Stop-Transcript", script);
    }

    [Fact]
    public void Build_WithoutLogPath_EmitsNoTranscript()
    {
        var script = UpdateRelaunchScript.Build(SampleSpec(logPath: null));

        Assert.DoesNotContain("Start-Transcript", script);
        Assert.DoesNotContain("Stop-Transcript", script);
    }

    [Fact]
    public void Build_PlacesRelaunchInsideFinallyBlock()
    {
        var spec = SampleSpec();
        var script = UpdateRelaunchScript.Build(spec);

        var finallyIndex = script.IndexOf("finally", StringComparison.Ordinal);
        var relaunchIndex = script.IndexOf(
            $"Start-Process -FilePath '{spec.TargetExecutablePath}'", StringComparison.Ordinal);

        Assert.True(finallyIndex >= 0, "script must contain a finally block");
        Assert.True(relaunchIndex > finallyIndex, "relaunch must appear after the finally token");
    }

    /// <remarks>
    /// The test above does not pin what its name promises, which is why this one exists
    /// beside it rather than replacing it. Its <c>IndexOf("finally")</c> matches the
    /// FIRST occurrence of that substring, and the first one in the emitted text is the
    /// inner SHA-256 disposal - inside the try, before the catch. Moving the relaunch
    /// out of the outer finally and into the try, the exact regression the backlog warns
    /// against, still leaves it after that index and keeps the older test green.
    /// <para>
    /// Anchoring on the outer <c>} catch {</c> and the outer <c>} finally {</c> makes the
    /// ordering unambiguous. This is a fast tripwire, not the oracle: text can prove what
    /// was emitted but never that it is reached. The execution harness is the oracle.
    /// </para>
    /// </remarks>
    [Fact]
    public void Build_RelaunchFollowsTheOuterFinallyThatFollowsTheOuterCatch()
    {
        var spec = SampleSpec();
        var script = UpdateRelaunchScript.Build(spec);

        var outerCatchIndex = script.IndexOf("} catch {", StringComparison.Ordinal);
        Assert.True(outerCatchIndex >= 0, "script must contain the outer catch");

        var outerFinallyIndex = script.IndexOf(
            "} finally {", outerCatchIndex, StringComparison.Ordinal);
        Assert.True(outerFinallyIndex > outerCatchIndex, "the outer finally must follow the outer catch");

        var relaunchIndex = script.IndexOf(
            $"Start-Process -FilePath '{spec.TargetExecutablePath}'", StringComparison.Ordinal);
        Assert.True(
            relaunchIndex > outerFinallyIndex,
            "the relaunch must sit inside the outer finally, so it runs on every path");
    }

    /// <remarks>
    /// The installer's exit code was never read: Start-Process ran it with -Wait and no
    /// -PassThru, so an installer that ran and failed raised nothing and the catch was
    /// never entered. That, and not the catch, is where the silence came from.
    /// </remarks>
    [Fact]
    public void Build_CapturesTheInstallerAndChecksItsExitCode()
    {
        var script = UpdateRelaunchScript.Build(SampleSpec());

        Assert.Contains("-Wait -PassThru", script);

        // The guard wraps the property ACCESS, not merely the null object: reading
        // ExitCode can itself throw, and an unreadable code must degrade to today's
        // behaviour rather than be reported as a failure nobody measured.
        int readIndex = script.IndexOf("$installerProcess.ExitCode", StringComparison.Ordinal);
        Assert.True(readIndex > 0, "the exit code must be read");
        int tryIndex = script.LastIndexOf("try {", readIndex, StringComparison.Ordinal);
        Assert.True(tryIndex >= 0 && tryIndex < readIndex, "the exit-code read must be guarded");

        // BOTH conditions, deliberately: an unknown code is never a failure.
        Assert.Contains("$installerExitKnown -eq 1 -and $installerExitCode -ne 0", script);

        int launchIndex = script.IndexOf("-Wait -PassThru", StringComparison.Ordinal);
        int checkIndex = script.IndexOf("$installerExitKnown -eq 1", StringComparison.Ordinal);
        Assert.True(launchIndex < checkIndex, "the check must follow the launch");
        Assert.True(checkIndex < OuterCatchIndex(script), "the check must sit inside the try");
    }

    /// <summary>Index of the OUTER catch, which is the only one at column zero.</summary>
    /// <remarks>
    /// A bare <c>IndexOf("} catch {")</c> matches the INNER catch that guards the
    /// exit-code read, because the outer form is a substring of the indented one. That is
    /// the same mistake as <see cref="Build_PlacesRelaunchInsideFinallyBlock"/>'s search
    /// for the first "finally", which resolves to the inner SHA-256 disposal - and it was
    /// made again here, in a test written to avoid it. Anchor on the line start.
    /// </remarks>
    private static int OuterCatchIndex(string script)
    {
        int index = script.IndexOf(
            Environment.NewLine + "} catch {",
            StringComparison.Ordinal);
        Assert.True(index > 0, "the script must contain an outer catch at column zero");
        return index;
    }

    /// <remarks>
    /// Write-Error under ErrorActionPreference Stop is a TERMINATING error: it abandons
    /// the rest of the catch. A record write placed after it is dead code, and any text
    /// assertion would still find both substrings present in a plausible order. This
    /// pins the order; the execution harness is what proves the statement is reached.
    /// </remarks>
    [Fact]
    public void Build_WritesTheFailureRecordBeforeWriteError()
    {
        var script = UpdateRelaunchScript.Build(
            SampleSpec(failureRecordPath: FailureRecordPath));

        int catchIndex = OuterCatchIndex(script);
        int writeIndex = script.IndexOf("[System.IO.File]::WriteAllText", StringComparison.Ordinal);
        int errorIndex = script.IndexOf("Write-Error", StringComparison.Ordinal);

        Assert.True(writeIndex > catchIndex, "the record write must sit inside the catch");
        Assert.True(writeIndex < errorIndex, "the record write must precede Write-Error");

        // No free text reaches the record: a closed-vocabulary token and two integers.
        // A field that could carry an exception message or a path is a field that can
        // produce malformed JSON on the one run that matters.
        Assert.Contains("$updateStage", script);
        Assert.Contains("$installerExitKnown", script);
        Assert.DoesNotContain("ConvertTo-Json", script);
    }

    [Fact]
    public void Build_WithoutFailureRecordPath_EmitsNoRecordWrite()
    {
        var script = UpdateRelaunchScript.Build(SampleSpec(failureRecordPath: null));

        // Mirrors Build_WithoutLogPath_EmitsNoTranscript: the field stays genuinely
        // optional, so the emission before this change remains reachable and diffable.
        Assert.DoesNotContain("WriteAllText", script);
    }

    [Fact]
    public void Build_PathWithSingleQuote_IsEscapedInScript()
    {
        var spec = SampleSpec(installerPath: @"C:\Temp\o'brien\setup.exe");
        var script = UpdateRelaunchScript.Build(spec);

        Assert.Contains(@"'C:\Temp\o''brien\setup.exe'", script);
    }

    [Fact]
    public void Build_VerifiesAuthenticodeThenHeldInstallerBeforeStartProcess()
    {
        var script = UpdateRelaunchScript.Build(SampleSpec());

        int openIndex = script.IndexOf("[System.IO.File]::Open", StringComparison.Ordinal);
        int hashIndex = script.IndexOf("$sha256.ComputeHash($installerStream)", StringComparison.Ordinal);
        int comparisonIndex = script.IndexOf(
            "Installer SHA-256 verification failed at the execution boundary.",
            StringComparison.Ordinal);
        int signatureIndex = script.IndexOf("Get-AuthenticodeSignature", StringComparison.Ordinal);
        int installerStartIndex = script.IndexOf(
            "Start-Process -FilePath $installerPath",
            StringComparison.Ordinal);
        int disposeIndex = script.IndexOf("$installerStream.Dispose()", StringComparison.Ordinal);

        Assert.True(openIndex >= 0);
        Assert.True(openIndex > signatureIndex);
        Assert.True(hashIndex > openIndex);
        Assert.True(comparisonIndex > hashIndex);
        Assert.True(installerStartIndex > comparisonIndex);
        Assert.True(disposeIndex > installerStartIndex);
        Assert.Contains("[System.IO.FileShare]::Read", script);
        Assert.Contains("$signature.Status -ne 'Valid'", script);
        Assert.Contains("$signature.Status -ne 'NotSigned'", script);
    }

    [Fact]
    public void Build_InvalidInstallerHash_Throws()
    {
        UpdateRelaunchSpec spec = SampleSpec() with
        {
            ExpectedInstallerSha256 = "not-a-hash",
        };

        Assert.Throws<ArgumentException>(() => UpdateRelaunchScript.Build(spec));
    }

    [Fact]
    public void BuildPowerShellArguments_InvalidScriptHash_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => UpdateRelaunchScript.BuildPowerShellArguments(
                @"C:\Temp\relaunch.ps1",
                "not-a-hash"));
    }
}
