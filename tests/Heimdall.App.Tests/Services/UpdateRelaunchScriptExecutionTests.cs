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
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Heimdall.Core.Security;
using Heimdall.Core.Updates;

namespace Heimdall.App.Tests.Services;

/// <summary>
/// Runs the text that <see cref="UpdateRelaunchScript.Build"/> actually emits, inside a
/// throwaway sandbox, and parses the branch that cannot be run.
/// </summary>
/// <remarks>
/// Until this file existed, nothing in the repository executed or even parsed the
/// generated script - its tests all asserted on the string. That gap is why the update
/// path was too dangerous to change. The script runs after the application has exited,
/// during file replacement, so a defect in it strands a user on an old version with no
/// recourse; a parse error is worse still, because the bootstrap ends in
/// <c>Invoke-Expression</c>, so a script that does not parse never enters its try and
/// never reaches the finally that brings the application back.
/// <para>
/// Running it here is safe by construction rather than by promise. Build is pure, and
/// every path the script reads, writes, executes or deletes is interpolated from a
/// field of the spec; the only literals in the emitted text are cmdlet names, two throw
/// messages and PowerShell syntax. A test that builds its own spec therefore cannot
/// reach the production one, which is assembled at a single site in
/// <c>UpdateInstaller.BeginInstall</c>. <c>AssertFenced</c> turns that argument into a
/// check that runs before every script.
/// </para>
/// </remarks>
public sealed class UpdateRelaunchScriptExecutionTests
{
    /// <summary>Bound on a single script run, so a hang fails rather than hangs.</summary>
    private static readonly TimeSpan ScriptCeiling = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long the timeout path waits for the already-draining readers once the process
    /// tree has been killed. Short on purpose: this only collects what is already there.
    /// </summary>
    private static readonly TimeSpan DrainAfterKillCeiling = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long a marker may take to appear. The relaunch is started without -Wait, so
    /// its marker lands asynchronously, after the host has already exited. Polled to a
    /// deadline rather than slept on: a fixed wall-clock wait on pool-scheduled work is
    /// this repository's documented source of rotating CI failures.
    /// </summary>
    private static readonly TimeSpan MarkerDeadline = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Wait applied to the script's own <c>Wait-Process</c>. Deliberately not the
    /// production default of two minutes: a recycled process id must bound the test
    /// rather than stall it.
    /// </summary>
    private const int TestWaitTimeoutSeconds = 5;

    /// <summary>
    /// How long to let a relaunch that should NOT happen land before asserting it did
    /// not. The relaunch is started without -Wait, so its marker can trail the host's
    /// exit; a negative assertion needs that window closed, and there is nothing to
    /// poll for.
    /// </summary>
    private static readonly TimeSpan SettleAfterHostExit = TimeSpan.FromSeconds(2);

    private const string InstallerRole = "installer";

    private const string RelaunchRole = "relaunch-target";

    private const string MarkerEnvironmentVariable = "HEIMDALL_UPDATE_STUB_MARKER";

    private const string ExitCodeEnvironmentVariable = "HEIMDALL_UPDATE_STUB_EXIT_CODE";

    public static TheoryData<string> PowerShellHosts()
    {
        var data = new TheoryData<string>();
        foreach (string host in ResolveHosts())
        {
            data.Add(host);
        }

        return data;
    }

    [Fact]
    public void ExecutionHarness_ResolvedAtLeastOnePowerShellHost()
    {
        IReadOnlyList<string> hosts = ResolveHosts();

        // A theory fed by an empty set skips every case and still reports green. This
        // is the assertion that makes that skip visible instead of silent.
        Assert.NotEmpty(hosts);
        Assert.All(hosts, host => Assert.True(File.Exists(host), $"resolved host missing: {host}"));
    }

    [Fact]
    public void Fixture_StandInExecutable_IsPresentAndPassesTheAuthenticodeGate()
    {
        string stub = StubPath();

        Assert.True(File.Exists(stub), $"update stub fixture missing: {stub}");

        // The script refuses anything whose signature status is neither Valid nor
        // NotSigned. Measured: an unsigned apphost reports NotSigned, which is what lets
        // the success-path tests past the gate at all. A copied system binary would
        // report Valid and drag certificate chain building into every run.
        string status = AuthenticodeStatus(stub);
        Assert.True(
            status is "NotSigned" or "Valid",
            $"the stand-in would be refused by the script's own signature gate: {status}");
    }

    [Theory]
    [MemberData(nameof(PowerShellHosts))]
    public async Task GeneratedScript_ParsesWithoutErrors_InAllFourEmissionVariants(string powerShellHost)
    {
        using var sandbox = new UpdateScriptSandbox();
        sandbox.PlaceInstaller(exitCode: 0);

        // The elevated emission is the branch no execution test may ever take, because
        // -Verb RunAs raises a consent prompt. It is also the production default, since
        // the installer targets a directory the user cannot write. Parsing is the only
        // mechanical cover it can have, and a parse error is the failure that strands a
        // user completely.
        var written = new List<string>();
        foreach (bool elevated in new[] { false, true })
        {
            foreach (bool withLog in new[] { false, true })
            {
                UpdateRelaunchSpec spec = sandbox.CreateSpec(
                    requiresElevation: elevated,
                    withLog: withLog);
                UpdateScriptSandbox.AssertFenced(spec, sandbox.Root);
                string path = Path.Combine(
                    sandbox.Root,
                    $"variant-{(elevated ? "elevated" : "plain")}-{(withLog ? "log" : "nolog")}.ps1");
                await File.WriteAllTextAsync(path, UpdateRelaunchScript.Build(spec));
                written.Add(path);
            }
        }

        Assert.Equal(4, written.Count);

        string parserPath = Path.Combine(sandbox.Root, "parse-variants.ps1");
        await File.WriteAllTextAsync(parserPath, ParserScript(written));

        ScriptRun run = await RunHostAsync(powerShellHost, parserPath, markerPath: null);

        Assert.True(
            run.ExitCode == 0,
            $"the generated script does not parse.\nstdout:\n{run.StandardOutput}\nstderr:\n{run.StandardError}");
    }

    /// <summary>
    /// The mandatory gate, reached and refused on its own terms: a properly signed
    /// installer whose bytes do not match the hash the application obtained.
    /// </summary>
    /// <remarks>
    /// Its neighbour is named after this gate but never reaches it - a non-PE file is
    /// turned back at the signature line, recording the Preparation stage. So did any
    /// host where Get-AuthenticodeSignature was unavailable, which is how that test came
    /// to pass on a runner for a reason of its own.
    /// <para>
    /// This one asserts the recorded STAGE rather than merely a non-zero exit code, so
    /// no earlier abort can satisfy it: an abort before the hash is compared records
    /// Preparation, never IntegrityRejected.
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(PowerShellHosts))]
    public async Task Execute_HashMismatch_RefusesAtTheMandatoryGate(string powerShellHost)
    {
        using var sandbox = new UpdateScriptSandbox();

        // A real, signature-clean executable, so nothing turns it back before the hash.
        sandbox.PlaceInstaller(exitCode: 0);
        sandbox.ExpectedSha256Override = new string('0', 64);

        ScriptRun run = await sandbox.RunAsync(powerShellHost);

        Assert.True(run.ExitCode != 0, "a hash mismatch must not report success");
        Assert.False(
            sandbox.SequenceContainsRole(InstallerRole),
            "an installer whose bytes are not the ones verified must never run");

        string recorded = await File.ReadAllTextAsync(sandbox.FailureRecordPath);
        UpdateFailureRecord? record = JsonSerializer.Deserialize<UpdateFailureRecord>(
            recorded,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(record);
        Assert.Equal(UpdateOutcomeStage.IntegrityRejected, record!.Stage);

        // The refusal must not cost the user their application.
        await sandbox.WaitForRoleAsync(RelaunchRole);
    }

    /// <remarks>
    /// Informational rather than blocking, and the reason is a named limitation rather
    /// than a flake - but NOT the limitation this remark used to name. The reading below
    /// replaces one that the runner's own transcript falsified once #243 started
    /// uploading it.
    /// <para>
    /// The failure is confined to Windows PowerShell 5.1. The same test, on the same
    /// runner, in the same run, PASSES under PowerShell 7 - same Start-Process, same
    /// window station - so neither of those can be the cause. What the transcript shows
    /// instead: Microsoft.PowerShell.Security fails to import, its extended type data
    /// declaring members of System.Security.AccessControl.ObjectSecurity that are
    /// already present, so Get-AuthenticodeSignature does not exist and the script
    /// terminates at the signature line under $ErrorActionPreference = 'Stop'.
    /// Start-Process is never reached at all. The installer file being gone afterwards
    /// proves only that the outer finally ran.
    /// </para>
    /// <para>
    /// That was a product defect rather than a harness one, and it is recorded as
    /// BL-0091: the application falls back to powershell.exe wherever PowerShell 7 is
    /// absent, so on such a machine no update could install. Left running rather than
    /// silenced: it still executes, its failure is still reported, and it is a real
    /// oracle under PowerShell 7 and on a developer machine, where the ordering mutant
    /// is also measured.
    /// </para>
    /// <para>
    /// <b>THAT REASON IS FIXED AND NO LONGER APPLIES.</b> PR #247 made obtaining the
    /// Authenticode verdict non-fatal, and four independent post-fix CI runs pass 8 of 8
    /// cases under BOTH hosts, 6 to 7 seconds apiece. The label is kept for a DIFFERENT
    /// and weaker reason, stated here so nobody mistakes one for the other: on
    /// 2026-08-23 this class failed once, 1 case of 16, on a 16-core developer machine,
    /// and the identity of that case was destroyed by re-running before the output was
    /// captured. Four captured runs afterwards were clean.
    /// </para>
    /// <para>
    /// So removal is blocked on CHARACTERISING that observation, not on its absence -
    /// see BL-0067, which exists for this family of process-spawning timeouts. Promoting
    /// these to the blocking lane on the strength of "the old reason is gone" would
    /// replace one unmeasured claim with another.
    /// </para>
    /// </remarks>
    [Trait("Category", "CIUnstable")]
    [Theory]
    [MemberData(nameof(PowerShellHosts))]
    public async Task Execute_InstallerSucceeds_RunsInstallerAndRelaunches(string powerShellHost)
    {
        using var sandbox = new UpdateScriptSandbox();
        sandbox.PlaceInstaller(exitCode: 0);

        ScriptRun run = await sandbox.RunAsync(powerShellHost);

        await sandbox.WaitForRoleAsync(InstallerRole);
        await sandbox.WaitForRoleAsync(RelaunchRole);
        Assert.False(File.Exists(sandbox.InstallerPath), "the installer should be removed");
        Assert.False(File.Exists(sandbox.ScriptPath), "the script should delete itself");

        // The exit code is pinned by the staging test beside this one, which is the
        // place where a non-zero code has a named cause.
        _ = run;
    }

    [Theory]
    [MemberData(nameof(PowerShellHosts))]
    public async Task Execute_IntegrityRejected_StillRelaunchesAndCleansUp(string powerShellHost)
    {
        using var sandbox = new UpdateScriptSandbox();

        // Measured: a non-PE file reports UnknownError, which is neither Valid nor
        // NotSigned, so the signature gate throws before the installer is ever launched.
        sandbox.PlaceNonExecutableInstaller();

        ScriptRun run = await sandbox.RunAsync(powerShellHost);

        // KNOWN WEAKNESS, recorded rather than left to be rediscovered. Both assertions
        // below are satisfied by ANY abort before the installer launches, so on a host
        // where Get-AuthenticodeSignature is unavailable this test passes without ever
        // exercising the hash gate it exists for - which is exactly what happens on a
        // GitHub runner under Windows PowerShell 5.1. Tightening it to name the stage
        // that failed would make it red there, correctly, which is why it belongs with
        // the fix in BL-0091 rather than ahead of it.
        Assert.True(run.ExitCode != 0, "a rejected installer must not report success");
        Assert.False(sandbox.SequenceContainsRole(InstallerRole), "the installer must never have run");

        // The whole purpose of the outer finally: the user gets their application back
        // even when the update was refused.
        await sandbox.WaitForRoleAsync(RelaunchRole);
        Assert.False(File.Exists(sandbox.ScriptPath), "the script should delete itself");
    }

    /// <remarks>
    /// Measured before the fix: the staging Remove-Item carried no -Recurse, so against
    /// a non-empty directory it asked for confirmation. Under -NonInteractive that
    /// request raises PSInvalidOperationException - "PowerShell is in NonInteractive
    /// mode. Read and Prompt functionality is not available." - and -ErrorAction
    /// SilentlyContinue does NOT suppress it, because it is an InvalidOperation from the
    /// host rather than an error from the cmdlet. With $ErrorActionPreference = 'Stop'
    /// at the top of the script, that terminated the rest of the finally, left the
    /// directory with the installer inside it, and exited the host non-zero.
    /// <para>
    /// This fixture makes staging non-empty by construction - the stand-in is an apphost
    /// and needs companions a real installer does not - so it is the natural place to
    /// pin the cleanup. Informational for the same reason as its neighbours: one
    /// uncharacterised local failure of this family, see BL-0067.
    /// </para>
    /// </remarks>
    [Trait("Category", "CIUnstable")]
    [Theory]
    [MemberData(nameof(PowerShellHosts))]
    public async Task Execute_StagingDirectoryNotEmpty_IsRemovedAndTheHostExitsCleanly(string powerShellHost)
    {
        using var sandbox = new UpdateScriptSandbox();
        sandbox.PlaceInstaller(exitCode: 0);

        ScriptRun run = await sandbox.RunAsync(powerShellHost);

        await sandbox.WaitForRoleAsync(InstallerRole);
        await sandbox.WaitForRoleAsync(RelaunchRole);

        Assert.False(
            Directory.Exists(sandbox.StageDirectory),
            $"the staging directory must be removed with everything in it.{Environment.NewLine}{run.StandardError}");
        Assert.True(
            run.ExitCode == 0,
            $"a successful update must leave the host with a clean exit code.{Environment.NewLine}{run.StandardError}");
    }

    /// <summary>
    /// A Wait-Process timeout is an error the script suppresses on purpose, because an
    /// already-exited process reports one too. Before the probe that follows it, the
    /// two were indistinguishable: the installer ran over the live application, which
    /// Inno Setup then force-closed mid-session.
    /// </summary>
    /// <remarks>
    /// The waited process is this test host, which does not exit, and the wait is one
    /// second so the refusal is reached quickly. Nothing may run afterwards: not the
    /// installer, and not a second instance of an application that is still there.
    /// </remarks>
    [Trait("Category", "CIUnstable")]
    [Theory]
    [MemberData(nameof(PowerShellHosts))]
    public async Task Execute_ApplicationStillRunning_RefusesToInstallAndRecordsTheStage(string powerShellHost)
    {
        using var sandbox = new UpdateScriptSandbox();
        sandbox.PlaceInstaller(exitCode: 0);
        sandbox.WaitedProcessIdOverride = Environment.ProcessId;
        sandbox.WaitTimeoutSecondsOverride = 1;

        ScriptRun run = await sandbox.RunAsync(powerShellHost);

        Assert.True(run.ExitCode != 0, "a refused update must not report success");
        Assert.False(
            sandbox.SequenceContainsRole(InstallerRole),
            "the installer must never run over a live application");

        UpdateFailureRecord? record = await sandbox.ReadFailureRecordAsync();
        Assert.NotNull(record);
        Assert.Equal(UpdateOutcomeStage.ApplicationStillRunning, record!.Stage);
        Assert.False(record.HasExitCode);

        // The application is still running, so it must not be started again. The host
        // has exited by now, and the relaunch is started without -Wait, so a short
        // settle covers the only window in which a wrong launch could still land.
        await Task.Delay(SettleAfterHostExit);
        Assert.False(
            sandbox.SequenceContainsRole(RelaunchRole),
            "an application that never exited must not be relaunched");
    }

    /// <summary>
    /// The production launch, end to end: the real -EncodedCommand arguments from
    /// <see cref="UpdateRelaunchScript.BuildPowerShellArguments"/>, under the host
    /// production resolves, against the emitted script. Until this test existed the
    /// bootstrap had zero execution coverage; every other case here runs -File.
    /// </summary>
    [Trait("Category", "CIUnstable")]
    [Theory]
    [MemberData(nameof(PowerShellHosts))]
    public async Task Bootstrap_RunsTheEmittedScript_ThroughTheProductionArguments(string powerShellHost)
    {
        using var sandbox = new UpdateScriptSandbox();
        sandbox.PlaceInstaller(exitCode: 0);

        ScriptRun run = await sandbox.RunViaBootstrapAsync(powerShellHost);

        await sandbox.WaitForRoleAsync(InstallerRole);
        await sandbox.WaitForRoleAsync(RelaunchRole);
        Assert.False(File.Exists(sandbox.FailureRecordPath), "a successful update leaves no failure record");

        // Exactly one relaunch: the script owns it, and the bootstrap must not add a
        // second instance on top.
        await Task.Delay(SettleAfterHostExit);
        Assert.Equal(1, sandbox.CountRole(RelaunchRole));
        Assert.True(run.ExitCode == 0, $"clean run expected.{Environment.NewLine}{run.StandardError}");
    }

    /// <summary>
    /// The bootstrap's SHA-256 refusal is correct and must stay; what it used to cost
    /// was the application. A script changed on disk after the application pinned its
    /// digest is refused, recorded as a preparation failure, and the application comes
    /// back.
    /// </summary>
    [Trait("Category", "CIUnstable")]
    [Theory]
    [MemberData(nameof(PowerShellHosts))]
    public async Task Bootstrap_ScriptChangedAfterHashing_RefusesAndStillRelaunches(string powerShellHost)
    {
        using var sandbox = new UpdateScriptSandbox();
        sandbox.PlaceInstaller(exitCode: 0);

        ScriptRun run = await sandbox.RunViaBootstrapAsync(
            powerShellHost,
            tamperAfterHashing: path => File.AppendAllText(path, "Write-Output 'tampered'"));

        Assert.True(run.ExitCode != 0, "a tampered script must not report success");
        Assert.False(sandbox.SequenceContainsRole(InstallerRole), "a tampered script must never reach the installer");

        UpdateFailureRecord? record = await sandbox.ReadFailureRecordAsync();
        Assert.NotNull(record);
        Assert.Equal(UpdateOutcomeStage.Preparation, record!.Stage);

        await sandbox.WaitForRoleAsync(RelaunchRole);
    }

    /// <summary>
    /// A script that does not parse never runs a single statement, so its own finally
    /// is never installed. The bootstrap is the only thing that can bring the
    /// application back in that case.
    /// </summary>
    [Trait("Category", "CIUnstable")]
    [Theory]
    [MemberData(nameof(PowerShellHosts))]
    public async Task Bootstrap_ScriptThatDoesNotParse_StillRelaunches(string powerShellHost)
    {
        using var sandbox = new UpdateScriptSandbox();
        sandbox.PlaceInstaller(exitCode: 0);

        ScriptRun run = await sandbox.RunViaBootstrapAsync(
            powerShellHost,
            scriptTextOverride: "if ($true) {" + Environment.NewLine);

        Assert.True(run.ExitCode != 0, "a script that does not parse must not report success");
        Assert.False(sandbox.SequenceContainsRole(InstallerRole), "nothing of the script may have run");

        UpdateFailureRecord? record = await sandbox.ReadFailureRecordAsync();
        Assert.NotNull(record);
        Assert.Equal(UpdateOutcomeStage.Preparation, record!.Stage);

        await sandbox.WaitForRoleAsync(RelaunchRole);
    }

    /// <summary>
    /// Every value the spec contributes lands inside a single-quoted literal, where
    /// subexpressions, backticks and double quotes are inert. Parsed rather than run:
    /// these paths do not exist, and parsing is the property under test.
    /// </summary>
    [Theory]
    [MemberData(nameof(PowerShellHosts))]
    public async Task GeneratedScript_WithHostilePaths_StillParses(string powerShellHost)
    {
        using var sandbox = new UpdateScriptSandbox();
        sandbox.PlaceInstaller(exitCode: 0);

        string hostileDirectory = Path.Combine(sandbox.Root, "$(Get-Date)`n\"quoted\" o'brien");
        UpdateRelaunchSpec spec = sandbox.CreateSpec() with
        {
            InstallerPath = Path.Combine(hostileDirectory, "setup.exe"),
            TargetExecutablePath = Path.Combine(hostileDirectory, "app.exe"),
            ScriptPath = Path.Combine(hostileDirectory, "relaunch.ps1"),
            StagingDirectory = hostileDirectory,
            LogPath = Path.Combine(hostileDirectory, "log.txt"),
            FailureRecordPath = Path.Combine(hostileDirectory, "failure.json"),
        };
        UpdateScriptSandbox.AssertFenced(spec, sandbox.Root);

        string path = Path.Combine(sandbox.Root, "hostile.ps1");
        await File.WriteAllTextAsync(path, UpdateRelaunchScript.Build(spec));
        string parserPath = Path.Combine(sandbox.Root, "parse-hostile.ps1");
        await File.WriteAllTextAsync(parserPath, ParserScript(new[] { path }));

        ScriptRun run = await RunHostAsync(powerShellHost, parserPath, markerPath: null);

        Assert.True(
            run.ExitCode == 0,
            $"the generated script does not parse with hostile paths.{Environment.NewLine}{run.StandardOutput}{Environment.NewLine}{run.StandardError}");
    }

    /// <remarks>
    /// The oracle for the whole detection change. Write-Error under ErrorActionPreference
    /// Stop is a TERMINATING error, so a record write placed after it is dead code that
    /// still contains every substring a text assertion could search for, in a plausible
    /// order. Only running the script can tell the two apart.
    /// <para>
    /// Informational for the same reason as its neighbours, and that reason has changed
    /// twice. It used to fail on a GitHub runner under Windows PowerShell 5.1 because
    /// Microsoft.PowerShell.Security would not import and the script died at
    /// Get-AuthenticodeSignature; PR #247 fixed that, and four post-fix runs pass under
    /// both hosts. What keeps the label now is one uncharacterised local failure - see
    /// the fuller account on Execute_InstallerSucceeds_RunsInstallerAndRelaunches, and
    /// BL-0067.
    /// </para>
    /// </remarks>
    [Trait("Category", "CIUnstable")]
    [Theory]
    [MemberData(nameof(PowerShellHosts))]
    public async Task Execute_InstallerExitsNonZero_WritesTheFailureRecordAndStillRelaunches(
        string powerShellHost)
    {
        using var sandbox = new UpdateScriptSandbox();
        sandbox.PlaceInstaller(exitCode: InnoSetupExitCode.FatalInstallError);

        ScriptRun run = await sandbox.RunAsync(powerShellHost);

        await sandbox.WaitForRoleAsync(InstallerRole);

        string recorded = await File.ReadAllTextAsync(sandbox.FailureRecordPath);
        UpdateFailureRecord? record = JsonSerializer.Deserialize<UpdateFailureRecord>(
            recorded,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(record);
        Assert.Equal(UpdateFailureRecord.CurrentSchemaVersion, record!.SchemaVersion);
        Assert.Equal(UpdateOutcomeStage.InstallerExit, record.Stage);
        Assert.Equal(InnoSetupExitCode.FatalInstallError, record.InstallerExitCode);
        Assert.True(record.HasExitCode);

        // The refusal must not cost the user their application.
        await sandbox.WaitForRoleAsync(RelaunchRole);
        Assert.True(run.ExitCode != 0, "a failing installer must not report success");
    }

    [Trait("Category", "CIUnstable")]
    [Theory]
    [MemberData(nameof(PowerShellHosts))]
    public async Task Execute_InstallerSucceeds_WritesNoFailureRecord(string powerShellHost)
    {
        using var sandbox = new UpdateScriptSandbox();
        sandbox.PlaceInstaller(exitCode: InnoSetupExitCode.Success);

        await sandbox.RunAsync(powerShellHost);
        await sandbox.WaitForRoleAsync(InstallerRole);

        // Guards the over-correction of recording a failure on every run, which the
        // version check would then have to mop up.
        Assert.False(
            File.Exists(sandbox.FailureRecordPath),
            "a successful install must leave no failure record");
    }

    /// <summary>
    /// The recorded sequence stays readable while a stand-in still owns the write handle.
    /// </summary>
    /// <remarks>
    /// A Windows handle outlives the process that opened it by a moment, so a read taken the
    /// instant a stand-in exits can still meet the writer. Opening with the default share mode
    /// then throws - not because the sequence is wrong, but because someone else is holding it -
    /// and the run fails on the reading of a result it had already produced correctly (CI,
    /// 2026-08-26). This pins the sharing rather than the timing: it is deterministic, and it
    /// goes red the moment any of those reads goes back to the default.
    /// </remarks>
    [Fact]
    public void TheRecordedSequence_IsReadable_WhileAWriterStillHoldsTheHandle()
    {
        using UpdateScriptSandbox sandbox = new();
        File.WriteAllText(sandbox.SequencePath, $"{InstallerRole}|0{Environment.NewLine}");

        // The handle a PowerShell child keeps for a moment after it has exited. Its own share
        // mode is permissive: what decides the outcome is the share mode the reader asks for.
        using FileStream stillHeld = new(
            sandbox.SequencePath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete);

        Assert.True(
            sandbox.SequenceContainsRole(InstallerRole),
            "the sequence was recorded, so reading it must not depend on whether the writer has "
            + "let go of the file yet");
    }

    /// <summary>Resolves the PowerShell hosts the way the production host does.</summary>
    private static IReadOnlyList<string> ResolveHosts()
    {
        var found = new List<string>();
        string pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (string name in new[] { "pwsh.exe", "powershell.exe" })
        {
            foreach (string directory in pathVariable.Split(
                Path.PathSeparator,
                StringSplitOptions.RemoveEmptyEntries))
            {
                string candidate = Path.Combine(directory.Trim(), name);
                if (File.Exists(candidate))
                {
                    found.Add(candidate);
                    break;
                }
            }
        }

        return found;
    }

    private static string StubPath()
    {
        string? path = typeof(UpdateRelaunchScriptExecutionTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "UpdateStubPath")
            ?.Value;

        Assert.False(
            string.IsNullOrWhiteSpace(path),
            "the build did not inject UpdateStubPath; the fixture reference is broken");
        return path!;
    }

    private static string AuthenticodeStatus(string filePath)
    {
        string host = ResolveHosts().FirstOrDefault()
            ?? throw new InvalidOperationException("no PowerShell host resolved");
        var psi = new ProcessStartInfo(host)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(
            $"(Get-AuthenticodeSignature -LiteralPath '{UpdateRelaunchScript.EscapeSingleQuoted(filePath)}').Status");

        using Process process = Process.Start(psi)
            ?? throw new InvalidOperationException("failed to start PowerShell");
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output.Trim();
    }

    private static string ParserScript(IEnumerable<string> scriptPaths)
    {
        var sb = new StringBuilder();
        sb.AppendLine("$failed = 0");
        foreach (string path in scriptPaths)
        {
            sb.AppendLine($"$path = '{UpdateRelaunchScript.EscapeSingleQuoted(path)}'");
            sb.AppendLine("$tokens = $null");
            sb.AppendLine("$errors = $null");
            sb.AppendLine(
                "[void][System.Management.Automation.Language.Parser]::ParseFile("
                + "$path, [ref]$tokens, [ref]$errors)");
            sb.AppendLine("if ($errors -and $errors.Count -gt 0) {");
            sb.AppendLine("    $failed = 1");
            sb.AppendLine("    Write-Output \"PARSE-ERROR $path\"");
            sb.AppendLine("    foreach ($e in $errors) { Write-Output \"  $($e.Message)\" }");
            sb.AppendLine("} else {");
            sb.AppendLine("    Write-Output \"OK $path\"");
            sb.AppendLine("}");
        }

        sb.AppendLine("exit $failed");
        return sb.ToString();
    }

    private static Task<ScriptRun> RunHostAsync(
        string powerShellHost,
        string scriptPath,
        string? markerPath,
        int installerExitCode = 0)
    {
        var psi = new ProcessStartInfo(powerShellHost)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        // The production flags, read from the production constant, so a change there
        // reaches this harness instead of quietly diverging from it. -File rather than
        // -EncodedCommand: both hosts wrap stderr in CLIXML even on a clean run, which
        // would make stderr useless as an oracle. The bootstrap tests take the other
        // route, through RunBootstrapHostAsync.
        foreach (string flag in UpdateRelaunchScript.PowerShellFlags.Split(
            ' ',
            StringSplitOptions.RemoveEmptyEntries))
        {
            psi.ArgumentList.Add(flag);
        }

        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(scriptPath);

        return RunHostAsync(psi, powerShellHost, markerPath, installerExitCode);
    }

    /// <summary>
    /// Runs the host exactly as production does: one argument string from
    /// <see cref="UpdateRelaunchScript.BuildPowerShellArguments"/>, so the bootstrap
    /// that -EncodedCommand carries is the thing under test.
    /// </summary>
    private static Task<ScriptRun> RunBootstrapHostAsync(
        string powerShellHost,
        string productionArguments,
        string? markerPath,
        int installerExitCode)
    {
        var psi = new ProcessStartInfo(powerShellHost)
        {
            Arguments = productionArguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        return RunHostAsync(psi, powerShellHost, markerPath, installerExitCode);
    }

    private static async Task<ScriptRun> RunHostAsync(
        ProcessStartInfo psi,
        string powerShellHost,
        string? markerPath,
        int installerExitCode)
    {

        if (markerPath is not null)
        {
            psi.Environment[MarkerEnvironmentVariable] = markerPath;
            psi.Environment[ExitCodeEnvironmentVariable] =
                installerExitCode.ToString(CultureInfo.InvariantCulture);
        }

        using Process process = Process.Start(psi)
            ?? throw new InvalidOperationException($"failed to start {powerShellHost}");

        // Both streams drained before the wait, or a full pipe buffer deadlocks the run.
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(ScriptCeiling);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);

            // Everything this wait already knows goes into the message. It used to report
            // the host and nothing else, so the CI failure of 2026-08-24 - two PS 5.1 runs
            // over the ceiling while a third passed in 7 s - could not be told apart from a
            // script that never printed a line. The readers are already draining both
            // streams; killing the tree closes them, so awaiting here returns whatever the
            // child produced before it was stopped. Bounded, because a reader that does not
            // complete must not turn a timeout into a hang - and its own failure is
            // reported rather than swallowed, since "no output" and "could not read the
            // output" are different findings.
            throw new TimeoutException(
                $"relauncher script exceeded {ScriptCeiling.TotalSeconds} s under {powerShellHost}."
                + $" stdout: {await DrainAfterKillAsync(stdout)}"
                + $" stderr: {await DrainAfterKillAsync(stderr)}");
        }

        return new ScriptRun(process.ExitCode, await stdout, await stderr);
    }

    /// <summary>
    /// Returns what a stream reader captured before the process was killed.
    /// </summary>
    /// <remarks>
    /// Never throws: this runs while a <see cref="TimeoutException"/> is already being
    /// built, and losing the timeout to a secondary failure would replace a diagnosable
    /// report with a confusing one. What went wrong is reported in place of the text.
    /// </remarks>
    internal static async Task<string> DrainAfterKillAsync(Task<string> reader, TimeSpan? ceiling = null)
    {
        TimeSpan bound = ceiling ?? DrainAfterKillCeiling;
        try
        {
            Task finished = await Task.WhenAny(reader, Task.Delay(bound));
            if (!ReferenceEquals(finished, reader))
            {
                return "<not readable within "
                    + bound.TotalSeconds.ToString(CultureInfo.InvariantCulture)
                    + " s of the kill>";
            }

            string text = (await reader).Trim();
            return text.Length == 0 ? "<empty>" : text;
        }
        catch (Exception ex)
        {
            return $"<unreadable: {ex.GetType().Name}: {ex.Message}>";
        }
    }

    private sealed record ScriptRun(int ExitCode, string StandardOutput, string StandardError);

    /// <summary>
    /// A disposable directory tree laid out the way production lays one out, plus the
    /// fence that proves a run cannot escape it.
    /// </summary>
    private sealed class UpdateScriptSandbox : IDisposable
    {
        private ScriptRun? _lastRun;

        internal UpdateScriptSandbox()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "heimdall-bl0080",
                Guid.NewGuid().ToString("N"));
            StageDirectory = Path.Combine(Root, "stage");
            MarkerDirectory = Path.Combine(Root, "markers");
            RelaunchDirectory = Path.Combine(Root, "app");
            Directory.CreateDirectory(StageDirectory);
            Directory.CreateDirectory(MarkerDirectory);
            Directory.CreateDirectory(RelaunchDirectory);
            Directory.CreateDirectory(Path.Combine(Root, "logs"));

            InstallerPath = Path.Combine(StageDirectory, "installer.exe");
            ScriptPath = Path.Combine(StageDirectory, "relaunch.ps1");
            LogPath = Path.Combine(Root, "logs", "transcript.log");
            FailureRecordPath = Path.Combine(Root, "updates", "update-failure.json");
            Directory.CreateDirectory(Path.Combine(Root, "updates"));

            // Named for the role it reports, because the stand-in identifies itself from
            // its own file name when the script starts it with no arguments at all.
            TargetExecutablePath = Path.Combine(RelaunchDirectory, $"{RelaunchRole}.exe");
            CopyStub(TargetExecutablePath);
        }

        internal string Root { get; }

        internal string StageDirectory { get; }

        internal string MarkerDirectory { get; }

        internal string RelaunchDirectory { get; }

        internal string InstallerPath { get; }

        internal string ScriptPath { get; }

        internal string LogPath { get; }

        internal string FailureRecordPath { get; }

        internal string TargetExecutablePath { get; }

        internal int InstallerExitCode { get; private set; }

        /// <summary>
        /// The process the script waits on. By default a stand-in that has already
        /// exited, so the wait returns at once and the probe that follows it finds
        /// nothing - which is what production sees when the application has quit.
        /// It used to be this test host, which never exits: every run took the timeout
        /// branch and then asserted that the installer ran anyway, freezing exactly
        /// the behaviour the probe now refuses.
        /// </summary>
        internal int? WaitedProcessIdOverride { get; set; }

        internal int? WaitTimeoutSecondsOverride { get; set; }

        /// <summary>
        /// Refuses to run anything whose spec points outside the sandbox. This is what
        /// makes "it cannot touch the real update path" a check rather than a claim.
        /// </summary>
        internal static void AssertFenced(UpdateRelaunchSpec spec, string root)
        {
            foreach ((string name, string? value) in new (string, string?)[]
            {
                (nameof(spec.InstallerPath), spec.InstallerPath),
                (nameof(spec.TargetExecutablePath), spec.TargetExecutablePath),
                (nameof(spec.ScriptPath), spec.ScriptPath),
                (nameof(spec.StagingDirectory), spec.StagingDirectory),
                (nameof(spec.LogPath), spec.LogPath),
                (nameof(spec.FailureRecordPath), spec.FailureRecordPath),
            })
            {
                if (value is null)
                {
                    continue;
                }

                Assert.True(
                    value.StartsWith(root, StringComparison.OrdinalIgnoreCase),
                    $"{name} escapes the sandbox: {value}");
            }
        }

        internal void PlaceInstaller(int exitCode)
        {
            CopyStub(InstallerPath);
            InstallerExitCode = exitCode;
        }

        /// <summary>
        /// A well-formed hash the installer does not have, so the run reaches the
        /// SHA-256 gate rather than being turned back earlier.
        /// </summary>
        internal string? ExpectedSha256Override { get; set; }

        internal void PlaceNonExecutableInstaller()
        {
            File.WriteAllText(InstallerPath, "this is not a portable executable");
            InstallerExitCode = 0;
        }

        /// <summary>
        /// One file for both roles. The stand-in appends, so a single file carries the
        /// whole sequence in the order it happened, which is more than two files could
        /// say and needs no argument quoting to arrange.
        /// </summary>
        internal string SequencePath => Path.Combine(MarkerDirectory, "sequence.txt");

        internal bool SequenceContainsRole(string role) =>
            File.Exists(SequencePath)
            && ReadSharedText(SequencePath).Contains($"{role}|", StringComparison.Ordinal);

        /// <summary>
        /// Reads a file one of the stand-ins may still hold open.
        /// </summary>
        /// <remarks>
        /// <see cref="File.ReadAllText(string)"/> asks for <see cref="FileShare.Read"/>, so it
        /// throws the moment a writer still owns the handle - and a PowerShell child releases its
        /// handle some time after it exits, not at the instant it does. <see cref="WaitForRoleAsync"/>
        /// learned that and swallows the exception, but the lesson never travelled to the three
        /// other reads of the same file: on 2026-08-26 a run that had already recorded the right
        /// sequence failed on the reading of it. Sharing the handle removes the race instead of
        /// retrying around it.
        /// </remarks>
        private static string ReadSharedText(string path)
        {
            using FileStream stream = new(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using StreamReader reader = new(stream);
            return reader.ReadToEnd();
        }

        internal UpdateRelaunchSpec CreateSpec(bool requiresElevation = false, bool withLog = true)
        {
            return new UpdateRelaunchSpec(
                InstallerPath: InstallerPath,
                ExpectedInstallerSha256:
                    ExpectedSha256Override ?? Sha256OfFileOrPlaceholder(InstallerPath),
                TargetExecutablePath: TargetExecutablePath,
                ProcessId: WaitedProcessIdOverride ?? ExitedStandInProcessId(),
                ScriptPath: ScriptPath,
                StagingDirectory: StageDirectory,
                RequiresElevation: requiresElevation,

                // No quotes and no paths in the argument list, deliberately. Windows
                // PowerShell 5.1 and pwsh 7 do not agree on how a quoted path inside a
                // single -ArgumentList string is split, and the disagreement showed up
                // only on a CI runner: 5.1 failed while 7 passed. The stand-in takes its
                // marker path from the environment instead, which both hosts pass to a
                // child identically - and which the relaunch already had to use, because
                // the script starts the target with no arguments at all.
                InstallerArguments: $"--role {InstallerRole} --exit-code {InstallerExitCode}",
                WaitTimeoutSeconds: WaitTimeoutSecondsOverride ?? TestWaitTimeoutSeconds,
                LogPath: withLog ? LogPath : null,
                FailureRecordPath: FailureRecordPath);
        }

        /// <summary>
        /// Starts the stand-in with no marker and lets it exit, returning the id the
        /// script will wait on. No marker is set on this process, so it records nothing.
        /// </summary>
        private int ExitedStandInProcessId()
        {
            var psi = new ProcessStartInfo(TargetExecutablePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.Environment.Remove(MarkerEnvironmentVariable);

            using Process process = Process.Start(psi)
                ?? throw new InvalidOperationException("failed to start the waited stand-in");
            process.WaitForExit();
            return process.Id;
        }

        /// <summary>
        /// Writes the emitted script, pins its digest the way <c>UpdateInstaller</c>
        /// does, optionally tampers with the file afterwards, and runs the host with the
        /// production arguments.
        /// </summary>
        /// <param name="scriptTextOverride">
        /// Replaces the emitted script entirely, for a case about the bootstrap alone.
        /// The digest is computed over this text, so the bootstrap accepts it.
        /// </param>
        /// <param name="tamperAfterHashing">
        /// Applied to the script file after the digest was pinned, so the bootstrap
        /// refuses it.
        /// </param>
        internal async Task<ScriptRun> RunViaBootstrapAsync(
            string powerShellHost,
            string? scriptTextOverride = null,
            Action<string>? tamperAfterHashing = null)
        {
            UpdateRelaunchSpec spec = CreateSpec();
            AssertFenced(spec, Root);
            string scriptText = scriptTextOverride ?? UpdateRelaunchScript.Build(spec);

            // UTF-8 without a byte-order mark, as SecureFileWriter writes it: the digest
            // is over the file bytes, and a mark would be bytes the text does not have.
            byte[] bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(scriptText);
            await File.WriteAllBytesAsync(ScriptPath, bytes);
            string digest = Convert.ToHexString(SHA256.HashData(bytes));
            tamperAfterHashing?.Invoke(ScriptPath);

            string arguments = UpdateRelaunchScript.BuildPowerShellArguments(
                ScriptPath,
                digest,
                TargetExecutablePath,
                FailureRecordPath);

            _lastRun = await RunBootstrapHostAsync(
                powerShellHost,
                arguments,
                SequencePath,
                InstallerExitCode);
            return _lastRun;
        }

        internal async Task<UpdateFailureRecord?> ReadFailureRecordAsync()
        {
            if (!File.Exists(FailureRecordPath))
            {
                return null;
            }

            string recorded = await File.ReadAllTextAsync(FailureRecordPath);
            return JsonSerializer.Deserialize<UpdateFailureRecord>(
                recorded,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }

        internal int CountRole(string role)
        {
            if (!File.Exists(SequencePath))
            {
                return 0;
            }

            string marker = $"{role}|";
            string text = ReadSharedText(SequencePath);
            int count = 0;
            int index = 0;
            while ((index = text.IndexOf(marker, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += marker.Length;
            }

            return count;
        }

        internal async Task<ScriptRun> RunAsync(string powerShellHost)
        {
            UpdateRelaunchSpec spec = CreateSpec();
            AssertFenced(spec, Root);
            await File.WriteAllTextAsync(ScriptPath, UpdateRelaunchScript.Build(spec));

            // Both roles learn where to record through the environment: the script starts
            // the relaunch target with no arguments at all, and the installer takes the
            // same channel so no path has to survive -ArgumentList quoting.
            _lastRun = await RunHostAsync(
                powerShellHost,
                ScriptPath,
                SequencePath,
                InstallerExitCode);
            return _lastRun;
        }

        internal async Task WaitForRoleAsync(string role)
        {
            string marker = $"{role}|";
            DateTime deadline = DateTime.UtcNow + MarkerDeadline;
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    if (File.Exists(SequencePath)
                        && (await File.ReadAllTextAsync(SequencePath)).Contains(marker, StringComparison.Ordinal))
                    {
                        return;
                    }
                }
                catch (IOException)
                {
                    // Still being written; fall through and retry.
                }

                await Task.Delay(25);
            }

            throw new TimeoutException(
                $"'{role}' never recorded within {MarkerDeadline.TotalSeconds} s.{Environment.NewLine}{Diagnose()}");
        }

        /// <summary>
        /// Everything known about the run, gathered for a failure message.
        /// </summary>
        /// <remarks>
        /// The first CI failure of this harness reported only that a marker had not
        /// appeared, which said nothing about why and could not be reproduced locally.
        /// A timeout here is worth nothing without the script's own account of itself.
        /// </remarks>
        private string Diagnose()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"host exit code: {_lastRun?.ExitCode.ToString(CultureInfo.InvariantCulture) ?? "not run"}");
            sb.AppendLine($"host stdout: {_lastRun?.StandardOutput}");
            sb.AppendLine($"host stderr: {_lastRun?.StandardError}");
            sb.AppendLine($"installer still present: {File.Exists(InstallerPath)}");
            sb.AppendLine($"script still present: {File.Exists(ScriptPath)}");
            sb.AppendLine(
                $"sequence file: {(File.Exists(SequencePath) ? ReadSharedText(SequencePath) : "absent")}");
            sb.AppendLine(
                $"marker directory: {string.Join(", ", Directory.GetFiles(MarkerDirectory))}");
            sb.AppendLine(
                $"transcript: {(File.Exists(LogPath) ? ReadSharedText(LogPath) : "absent")}");
            return sb.ToString();
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch (IOException)
            {
                // A relaunched stand-in may still hold a handle; the root is disposable.
            }
            catch (UnauthorizedAccessException)
            {
                // Same.
            }
        }

        /// <summary>
        /// Copies the stand-in and the companions an apphost needs beside it. Without
        /// the managed assembly and its runtime config the copy starts and immediately
        /// fails with nothing to run - which would look exactly like the script failing.
        /// </summary>
        private static void CopyStub(string destination)
        {
            string sourceDirectory = Path.GetDirectoryName(StubPath())!;
            string targetDirectory = Path.GetDirectoryName(destination)!;
            string stubBaseName = Path.GetFileNameWithoutExtension(StubPath());

            File.Copy(StubPath(), destination, overwrite: true);
            foreach (string companion in Directory.GetFiles(sourceDirectory, $"{stubBaseName}.*"))
            {
                if (companion.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                File.Copy(
                    companion,
                    Path.Combine(targetDirectory, Path.GetFileName(companion)),
                    overwrite: true);
            }
        }

        private static string Sha256OfFileOrPlaceholder(string path)
        {
            if (!File.Exists(path))
            {
                return new string('0', 64);
            }

            using FileStream stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
    }
}
