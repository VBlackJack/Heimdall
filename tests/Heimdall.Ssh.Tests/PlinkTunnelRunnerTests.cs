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
using System.Net;
using Heimdall.Core.Network;
using Heimdall.Ssh.Plink;

namespace Heimdall.Ssh.Tests;

/// <summary>
/// Tests for <see cref="PlinkTunnelRunner"/> argument building and lifecycle.
/// </summary>
public class PlinkTunnelRunnerTests : IDisposable
{
    private readonly PlinkTunnelRunner _runner = new();

    public void Dispose()
    {
        _runner.Dispose();
    }

    // ── Argument building ────────────────────────────────────────────

    [Fact]
    public void BuildArguments_BasicTunnel_ContainsRequiredFlags()
    {
        var args = _runner.BuildArguments(
            "gateway.example.com", 22, "admin", null, null,
            "target.internal", 3389, 13389);

        Assert.Contains("-ssh", args);
        Assert.Contains("-N", args);
        Assert.Contains("-L", args);
        Assert.Contains("13389:target.internal:3389", args);
        Assert.DoesNotContain($"{LoopbackBinding.DefaultHost}:13389:target.internal:3389", args);
        Assert.Contains("-P", args);
        Assert.Contains("22", args);
        Assert.Contains("admin@gateway.example.com", args);
    }

    [Fact]
    public void BuildArguments_CustomLocalBindHost_PrefixesForwardSpec()
    {
        string alias = LoopbackBinding.FormatAlias(2);

        var args = _runner.BuildArguments(
            "gateway.example.com", 22, "admin", null, null,
            "target.internal", 3389, 13389,
            localBindHost: alias);

        Assert.Contains("-L", args);
        Assert.Contains($"{alias}:13389:target.internal:3389", args);
        Assert.DoesNotContain("13389:target.internal:3389", args);
    }

    [Fact]
    public void BuildArguments_InvalidLocalBindHost_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() => _runner.BuildArguments(
            "gateway.example.com", 22, "admin", null, null,
            "target.internal", 3389, 13389,
            localBindHost: "10.0.0.1"));

        Assert.Contains("Local bind host", ex.Message);
    }

    [Fact]
    public void BuildArguments_WithKeyPath_IncludesKeyFlag()
    {
        var keyPath = Path.GetTempFileName();

        try
        {
            var args = _runner.BuildArguments(
                "gw.test", 2222, "user", keyPath, null,
                "remote", 22, 10022);

            Assert.Contains("-i", args);
            int keyIndex = args.IndexOf("-i");
            Assert.Equal(keyPath, args[keyIndex + 1]);
        }
        finally
        {
            File.Delete(keyPath);
        }
    }

    [Fact]
    public void BuildArguments_WithPassword_UsesPwfile()
    {
        var args = _runner.BuildArguments(
            "gw.test", 22, "user", null, "s3cret",
            "remote", 22, 10022);

        Assert.Contains("-pwfile", args);
        Assert.DoesNotContain("-pw", args.Where(a => a == "-pw"));
    }

    [Fact]
    public void PlinkTunnelRunner_PasswordFile_UsesCanonicalPrefix()
    {
        var args = _runner.BuildArguments(
            "gw.test", 22, "user", null, "s3cret",
            "remote", 22, 10022);

        int passwordFileIndex = args.IndexOf("-pwfile");
        Assert.NotEqual(-1, passwordFileIndex);
        string passwordFileName = Path.GetFileName(args[passwordFileIndex + 1]);
        Assert.StartsWith(PlinkPasswordFileNaming.Prefix, passwordFileName, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildArguments_NoKeyNoPassword_NoAuthFlags()
    {
        var args = _runner.BuildArguments(
            "gw.test", 22, "user", null, null,
            "remote", 22, 10022);

        Assert.DoesNotContain("-i", args);
        Assert.DoesNotContain("-pwfile", args);
        Assert.DoesNotContain("-pw", args);
    }

    [Fact]
    public void BuildArguments_ContainsBatchFlag()
    {
        // -batch prevents interactive prompts; safe because -hostkey is
        // passed from TOFU store for known hosts, and unknown hosts fail
        // deterministically instead of hanging.
        var keyPath = Path.GetTempFileName();

        try
        {
            var args = _runner.BuildArguments(
                "gw.test", 22, "user", keyPath, "pass",
                "remote", 22, 10022);

            Assert.Contains("-batch", args);
        }
        finally
        {
            File.Delete(keyPath);
        }
    }

    [Fact]
    public void BuildArguments_WithHostKey_IncludesHostKeyFlag()
    {
        var args = _runner.BuildArguments(
            "gw.test", 22, "user", null, null,
            "remote", 22, 10022, "SHA256:abc123");

        Assert.Contains("-hostkey", args);
        var idx = args.IndexOf("-hostkey");
        Assert.Equal("SHA256:abc123", args[idx + 1]);
    }

    [Fact]
    public void BuildArguments_WithoutHostKey_OmitsHostKeyFlag()
    {
        var args = _runner.BuildArguments(
            "gw.test", 22, "user", null, null,
            "remote", 22, 10022);

        Assert.DoesNotContain("-hostkey", args);
    }

    [Fact]
    public void BuildArguments_CustomPort_IncludedCorrectly()
    {
        var args = _runner.BuildArguments(
            "gw.test", 2222, "user", null, null,
            "remote", 3389, 13389);

        int portIndex = args.IndexOf("-P");
        Assert.NotEqual(-1, portIndex);
        Assert.Equal("2222", args[portIndex + 1]);
    }

    // ── Lifecycle ────────────────────────────────────────────────────

    [Fact]
    public void IsRunning_BeforeStart_ReturnsFalse()
    {
        Assert.False(_runner.IsRunning);
    }

    [Fact]
    public void ProcessId_BeforeStart_ReturnsNull()
    {
        Assert.Null(_runner.ProcessId);
    }

    [Fact]
    public void LogProcessExit_LiveProcess_DoesNotThrow()
    {
        using Process process = Process.GetCurrentProcess();

        Exception? exception = Record.Exception(() => PlinkTunnelRunner.LogProcessExit(process, 10022));

        Assert.Null(exception);
    }

    [Fact]
    public void LogProcessExit_DisposedProcess_DoesNotThrow()
    {
        Process process = new Process();
        process.Dispose();

        Exception? exception = Record.Exception(() => PlinkTunnelRunner.LogProcessExit(process, 10023));

        Assert.Null(exception);
    }

    [Fact]
    public void Dispose_CalledTwice_DoesNotThrow()
    {
        _runner.Dispose();
        _runner.Dispose();
    }

    [Fact]
    public async Task StartAsync_PlinkNotFound_ReturnsFailure()
    {
        var result = await _runner.StartAsync(
            @"C:\nonexistent\plink.exe",
            "gw.test", 22, "user", null, null,
            "remote", 22, 10022);

        Assert.False(result.Success);
        Assert.Contains("not found", result.ErrorMessage);
    }

    [Fact]
    public async Task StartAsync_ProcessStartFails_ResetsStateAndAllowsRetry()
    {
        string invalidExecutablePath = Path.Combine(
            Path.GetTempPath(),
            $"heimdall-invalid-plink-{Guid.NewGuid():N}.exe");
        await File.WriteAllTextAsync(invalidExecutablePath, "not a Windows executable");

        try
        {
            PlinkTunnelResult firstResult = await _runner.StartAsync(
                invalidExecutablePath,
                "gw.test", 22, "user", null, null,
                "remote", 22, 10022);

            Assert.False(firstResult.Success);
            Assert.False(_runner.IsRunning);
            Assert.Null(_runner.ProcessId);

            PlinkTunnelResult retryResult = await _runner.StartAsync(
                invalidExecutablePath,
                "gw.test", 22, "user", null, null,
                "remote", 22, 10022);

            Assert.False(retryResult.Success);
            Assert.False(_runner.IsRunning);
            Assert.Null(_runner.ProcessId);
        }
        finally
        {
            File.Delete(invalidExecutablePath);
        }
    }

    [Fact]
    public async Task StartAsync_KeyPathWithKeyPassphrase_ReturnsFailureWithoutStartingProcess()
    {
        var result = await _runner.StartAsync(
            @"C:\nonexistent\plink.exe",
            "gw.test", 22, "user", @"C:\keys\id_rsa.ppk", null,
            "remote", 22, 10022,
            keyPassphrase: "key-passphrase",
            passphraseUnsupportedMessage: "localized plink passphrase unsupported");

        Assert.False(result.Success);
        Assert.Equal(SshFailureCode.PassphraseRequired, result.FailureCode);
        Assert.Equal("localized plink passphrase unsupported", result.ErrorMessage);
        Assert.False(_runner.IsRunning);
    }

    [Fact]
    public async Task TunnelPasswordFile_ForeignListenerFailsClosed()
    {
        string tempDirectory = Path.GetTempPath();
        HashSet<string> existingPasswordFiles = Directory
            .EnumerateFiles(tempDirectory, PlinkPasswordFileNaming.SearchPattern)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        int localPort = GetAvailableLoopbackPort();
        var listener = new TcpListener(IPAddress.Loopback, localPort);
        TaskCompletionSource startGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakePlinkProcess process = new()
        {
            WaitForExitResult = true,
            StartGate = startGate
        };

        // The ownership probe stays the real one, against a real listener, so this remains a test of
        // fail-closed detection rather than of a stubbed verdict. Only the process is faked, and only
        // to make the instant at which the password file can be observed deterministic.
        using PlinkTunnelRunner runner = CreateRunner(
            WindowsTcpListenerOwnershipProbe.Instance,
            _ => process);
        string? passwordFilePath = null;

        try
        {
            Task<PlinkTunnelResult> startTask = Task.Run(() => runner.StartAsync(
                GetCommandProcessorPath(),
                "gw.test", 22, "user", null, "s3cret",
                "remote", 22, localPort, "SHA256:test"));

            await process.Started.Task.WaitAsync(TimeSpan.FromSeconds(30));

            try
            {
                passwordFilePath = Assert.Single(
                    Directory.EnumerateFiles(tempDirectory, PlinkPasswordFileNaming.SearchPattern),
                    path => !existingPasswordFiles.Contains(path));
                Assert.True(File.Exists(passwordFilePath));

                // Bound while the runner is still held at process start, so the foreign listener is
                // in place before the first attestation rather than racing it.
                listener.Start();
            }
            finally
            {
                startGate.TrySetResult();
            }

            PlinkTunnelResult result = await startTask.WaitAsync(TimeSpan.FromSeconds(30));

            Assert.False(result.Success);
            Assert.Equal(SshFailureCode.TunnelPortOwnedByDifferentProcess, result.FailureCode);
            Assert.Contains("did not open forwarded port", result.ErrorMessage, StringComparison.Ordinal);
            Assert.Contains("cmd.exe", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(passwordFilePath));
        }
        finally
        {
            startGate.TrySetResult();
            listener.Stop();
            runner.Stop();
            if (passwordFilePath is not null)
            {
                File.Delete(passwordFilePath);
            }
        }
    }

    // Polling the directory would be a race in the other direction, not a fix for one: the runner
    // deletes the file as soon as the forwarded port is attested, so a poll that arrives late
    // legitimately finds nothing and the test would fail for the opposite reason. The fake process
    // instead holds the runner inside Start, which is after the arguments were built - the file
    // exists - and before any attestation - nothing can have removed it yet.
    [Fact]
    public async Task TunnelPasswordFile_DeletedAfterAttestedBind()
    {
        string tempDirectory = Path.GetTempPath();
        HashSet<string> existingPasswordFiles = Directory
            .EnumerateFiles(tempDirectory, PlinkPasswordFileNaming.SearchPattern)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var probe = new FakeOwnershipProbe(TcpListenerOwnership.OwnedByExpectedProcess);
        TaskCompletionSource startGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakePlinkProcess process = new()
        {
            WaitForExitResult = true,
            StartGate = startGate
        };
        using PlinkTunnelRunner runner = CreateRunner(probe, _ => process);
        string? passwordFilePath = null;

        try
        {
            Task<PlinkTunnelResult> startTask = Task.Run(() => runner.StartAsync(
                GetCommandProcessorPath(),
                "gw.test", 22, "user", null, "s3cret",
                "remote", 22, GetAvailableLoopbackPort(), "SHA256:test"));

            await process.Started.Task.WaitAsync(TimeSpan.FromSeconds(30));

            try
            {
                // The gate has to hold the runner BEFORE any attestation, otherwise observing the
                // file here would still be a race. An unprobed port is what proves it does.
                Assert.Equal(0, probe.LastExpectedProcessId);

                passwordFilePath = Assert.Single(
                    Directory.EnumerateFiles(tempDirectory, PlinkPasswordFileNaming.SearchPattern),
                    path => !existingPasswordFiles.Contains(path));
                Assert.True(File.Exists(passwordFilePath));
            }
            finally
            {
                startGate.TrySetResult();
            }

            PlinkTunnelResult result = await startTask.WaitAsync(TimeSpan.FromSeconds(30));

            Assert.True(result.Success, result.ErrorMessage);
            Assert.False(File.Exists(passwordFilePath));
            Assert.True(probe.LastExpectedProcessId > 0);
        }
        finally
        {
            startGate.TrySetResult();
            runner.Stop();
            if (passwordFilePath is not null)
            {
                File.Delete(passwordFilePath);
            }
        }
    }

    [Theory]
    [InlineData(TcpListenerOwnership.OwnedByDifferentProcess, SshFailureCode.TunnelPortOwnedByDifferentProcess)]
    [InlineData(TcpListenerOwnership.NothingListening, SshFailureCode.TunnelPortNotListening)]
    [InlineData(TcpListenerOwnership.Indeterminate, SshFailureCode.TunnelPortOwnershipIndeterminate)]
    public async Task StartAsync_UnattestedOwnership_ReturnsDistinctFailureCode(
        TcpListenerOwnership ownership,
        SshFailureCode expectedFailureCode)
    {
        using var runner = CreateRunner(new FakeOwnershipProbe(ownership));

        PlinkTunnelResult result = await runner.StartAsync(
            GetCommandProcessorPath(),
            "gw.test", 22, "user", null, null,
            "remote", 22, GetAvailableLoopbackPort(), "SHA256:test");

        Assert.False(result.Success);
        Assert.Equal(expectedFailureCode, result.FailureCode);
    }

    [Fact]
    public async Task StartAsync_CancelledDuringOwnershipWait_StopsPromptly()
    {
        using var runner = CreateRunner(new FakeOwnershipProbe(TcpListenerOwnership.NothingListening));
        using var cancellation = new CancellationTokenSource();

        Task<PlinkTunnelResult> startTask = runner.StartAsync(
            GetCommandProcessorPath(),
            "gw.test", 22, "user", null, null,
            "remote", 22, GetAvailableLoopbackPort(), "SHA256:test",
            cancellation.Token);
        cancellation.Cancel();

        PlinkTunnelResult result = await startTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(result.Success);
        Assert.Equal(SshFailureCode.Cancelled, result.FailureCode);
    }

    [Fact]
    public void BuildArguments_RejectsKeyPathWithQuoteInjection()
    {
        var ex = Assert.Throws<ArgumentException>(() => _runner.BuildArguments(
            "gw.test", 22, "user", "C:\\keys\\id\" --corrupt.ppk", null,
            "remote", 22, 10022));

        Assert.Contains("Invalid SSH key path", ex.Message);
    }

    [Fact]
    public void BuildArguments_RejectsRelativeKeyPath()
    {
        var ex = Assert.Throws<ArgumentException>(() => _runner.BuildArguments(
            "gw.test", 22, "user", "id.ppk", null,
            "remote", 22, 10022));

        Assert.Contains("must be absolute", ex.Message);
    }

    [Fact]
    public void BuildArguments_RejectsMissingKeyPath()
    {
        var ex = Assert.Throws<FileNotFoundException>(() => _runner.BuildArguments(
            "gw.test", 22, "user", @"C:\nope\does-not-exist.ppk", null,
            "remote", 22, 10022));

        Assert.Contains("SSH key file not found", ex.Message);
    }

    [Fact]
    public void CreateStartInfo_UsesArgumentListForValidInputs()
    {
        var keyPath = Path.GetTempFileName();

        try
        {
            var args = _runner.BuildArguments(
                "gateway.example.com", 22, "user", keyPath, null,
                "target.internal", 3389, 13389, "SHA256:abc123");
            var psi = _runner.CreateStartInfo(@"C:\tools\plink.exe", args);

            Assert.Equal(@"C:\tools\plink.exe", psi.FileName);
            Assert.Contains("-i", psi.ArgumentList);
            Assert.Contains(keyPath, psi.ArgumentList);
            Assert.Contains("-hostkey", psi.ArgumentList);
            Assert.Contains("SHA256:abc123", psi.ArgumentList);
        }
        finally
        {
            File.Delete(keyPath);
        }
    }

    [Fact]
    public void Stop_BeforeStart_DoesNotThrow()
    {
        _runner.Stop();
    }

    [Fact]
    public void Stop_StillRemovesPasswordFile_AsBackstop()
    {
        using var runner = new PlinkTunnelRunner();
        List<string> args = runner.BuildArguments(
            "gw.test", 22, "user", null, "s3cret",
            "remote", 22, 10022);
        int passwordFileIndex = args.IndexOf("-pwfile");
        string passwordFilePath = args[passwordFileIndex + 1];
        Assert.True(File.Exists(passwordFilePath));

        runner.Stop();

        Assert.False(File.Exists(passwordFilePath));
    }

    [Fact]
    public void SanitizeForLog_StripsControlChars_PreservesPrintable()
    {
        var sanitized = PlinkTunnelRunner.SanitizeForLog($"banner\r\nok{(char)127}");

        Assert.Equal("banner??ok?", sanitized);
    }

    [Fact]
    public void SanitizeForLog_TruncatesAtCap_AppendsEllipsis()
    {
        var line = new string('x', 260);

        var sanitized = PlinkTunnelRunner.SanitizeForLog(line);

        Assert.Equal(new string('x', 256) + " [...]", sanitized);
    }

    [Fact]
    public void SanitizeForLog_PreservesTab()
    {
        var sanitized = PlinkTunnelRunner.SanitizeForLog("left\tright");

        Assert.Equal("left\tright", sanitized);
    }

    [Fact]
    public void SanitizeForLog_OnNullOrEmpty_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, PlinkTunnelRunner.SanitizeForLog(null));
        Assert.Equal(string.Empty, PlinkTunnelRunner.SanitizeForLog(string.Empty));
    }

    // ── Secret redaction in stderr drain ────────────────────────────

    [Fact]
    public void SanitizeForLog_RedactsBearerToEndOfLine()
    {
        var sanitized = PlinkTunnelRunner.SanitizeForLog("Authorization: Bearer abc def ghi");

        Assert.Equal("Authorization: [REDACTED]", sanitized);
        Assert.DoesNotContain("abc", sanitized);
        Assert.DoesNotContain("def", sanitized);
        Assert.DoesNotContain("ghi", sanitized);
    }

    [Fact]
    public void SanitizeForLog_RedactsTokenToEndOfLine()
    {
        var sanitized = PlinkTunnelRunner.SanitizeForLog("token = xyz some-uuid extra");

        Assert.Equal("[REDACTED]", sanitized);
        Assert.DoesNotContain("xyz", sanitized);
        Assert.DoesNotContain("some-uuid", sanitized);
        Assert.DoesNotContain("extra", sanitized);
    }

    [Fact]
    public void SanitizeForLog_RedactsSingleTokenPassword()
    {
        var sanitized = PlinkTunnelRunner.SanitizeForLog("password=secret123 trailing");

        Assert.Equal("[REDACTED] trailing", sanitized);
        Assert.DoesNotContain("secret123", sanitized);
    }

    [Fact]
    public void SanitizeForLog_RedactsSingleTokenPassphrase()
    {
        var sanitized = PlinkTunnelRunner.SanitizeForLog("passphrase: foobar123 trailing");

        Assert.Equal("[REDACTED] trailing", sanitized);
        Assert.DoesNotContain("foobar123", sanitized);
    }

    [Fact]
    public void SanitizeForLog_DoesNotOverRedactNonCredentialLines()
    {
        const string line = "connecting to gateway 192.0.2.1";

        var sanitized = PlinkTunnelRunner.SanitizeForLog(line);

        Assert.Equal(line, sanitized);
        Assert.DoesNotContain("[REDACTED]", sanitized);
        Assert.DoesNotContain('?', sanitized);
    }

    [Theory]
    [InlineData("connecting with password=hunter2", "connecting with [REDACTED]")]
    [InlineData("Bearer abcdef0123456789", "[REDACTED]")]
    [InlineData("token: abc-123-def", "[REDACTED]")]
    [InlineData("passphrase = 's3cr3t!'", "[REDACTED]")]
    [InlineData("secret=topsecret continuing", "[REDACTED] continuing")]
    public void SanitizeForLog_RedactsCredentialAssignments(string raw, string expected)
    {
        var sanitized = PlinkTunnelRunner.SanitizeForLog(raw);
        Assert.Equal(expected, sanitized);
    }

    [Theory]
    [InlineData("plink -pwfile C:\\temp\\plink-password.tmp -ssh user@host",
                "plink [REDACTED] -ssh user@host")]
    [InlineData("trying with -pw mySecret target", "trying with [REDACTED] target")]
    public void SanitizeForLog_RedactsPlinkCredentialFlags(string raw, string expected)
    {
        var sanitized = PlinkTunnelRunner.SanitizeForLog(raw);
        Assert.Equal(expected, sanitized);
    }

    [Fact]
    public void SanitizeForLog_DoesNotRedactBenignText()
    {
        const string benign = "Tunnel established on local port 13389";
        Assert.Equal(benign, PlinkTunnelRunner.SanitizeForLog(benign));
    }

    // ── Stop() lifecycle ────────────────────────────────────────────

    [Fact]
    public void Stop_WithoutStart_IsNoOpAndIdempotent()
    {
        // Exercise the new drain-task join path: Stop() must tolerate being
        // called when Start was never invoked, and a second call must also
        // be safe (no double-Dispose, no AggregateException leaking).
        _runner.Stop();
        _runner.Stop();
    }

    [Fact]
    public async Task Stop_WhenKillTimesOut_ReaperRetainsProcessUntilExit()
    {
        FakePlinkProcess process = new()
        {
            WaitForExitResult = false
        };
        using PlinkTunnelRunner runner = CreateRunner(
            new FakeOwnershipProbe(TcpListenerOwnership.OwnedByExpectedProcess),
            _ => process);
        PlinkTunnelResult result = await runner.StartAsync(
            GetCommandProcessorPath(),
            "gateway.example.com",
            22,
            "user",
            null,
            null,
            "target.internal",
            3389,
            13389,
            "SHA256:test");

        Assert.True(result.Success);

        runner.Stop();

        Assert.Equal(1, process.KillCount);
        Assert.Equal(0, process.DisposeCount);
        Assert.False(runner.IsRunning);
        Assert.Equal(1, PlinkProcessReaper.PendingCount);

        process.CompleteExit();
        await process.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, process.DisposeCount);
        Assert.Equal(0, PlinkProcessReaper.PendingCount);
    }

    [Fact]
    public async Task Stop_WhenKillThrowsWin32_ReaperRetainsProcessAndPasswordFileIsCleaned()
    {
        HashSet<string> filesBefore = Directory
            .EnumerateFiles(Path.GetTempPath(), PlinkPasswordFileNaming.SearchPattern)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        FakePlinkProcess process = new()
        {
            KillException = new System.ComponentModel.Win32Exception("Simulated kill failure")
        };
        using PlinkTunnelRunner runner = CreateRunner(
            new FakeOwnershipProbe(TcpListenerOwnership.NothingListening),
            _ => process);

        PlinkTunnelResult result = await runner.StartAsync(
            GetCommandProcessorPath(),
            "gateway.example.com",
            22,
            "user",
            null,
            "s3cret",
            "target.internal",
            3389,
            13390,
            "SHA256:test");

        Assert.False(result.Success);
        Assert.Equal(1, process.KillCount);
        Assert.Equal(0, process.DisposeCount);
        Assert.DoesNotContain(
            Directory.EnumerateFiles(Path.GetTempPath(), PlinkPasswordFileNaming.SearchPattern),
            path => !filesBefore.Contains(path));
        Assert.Equal(1, PlinkProcessReaper.PendingCount);

        process.CompleteExit();
        await process.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, process.DisposeCount);
        Assert.Equal(0, PlinkProcessReaper.PendingCount);
    }

    [Fact]
    public async Task Stop_WhenExitIsConfirmed_DisposesImmediatelyWithoutReaper()
    {
        FakePlinkProcess process = new()
        {
            WaitForExitResult = true
        };
        using PlinkTunnelRunner runner = CreateRunner(
            new FakeOwnershipProbe(TcpListenerOwnership.OwnedByExpectedProcess),
            _ => process);
        PlinkTunnelResult result = await runner.StartAsync(
            GetCommandProcessorPath(),
            "gateway.example.com",
            22,
            "user",
            null,
            null,
            "target.internal",
            3389,
            13391,
            "SHA256:test");

        Assert.True(result.Success);

        runner.Stop();

        Assert.Equal(1, process.KillCount);
        Assert.Equal(1, process.DisposeCount);
        Assert.Equal(0, PlinkProcessReaper.PendingCount);
    }

    [Fact]
    public void Dispose_WithoutStart_IsNoOpAndIdempotent()
    {
        var runner = new PlinkTunnelRunner();

        runner.Dispose();
        runner.Dispose();
    }

    // ── Options object ───────────────────────────────────────────────

    [Fact]
    public void Constructor_AcceptsOptionsObject()
    {
        // Smoke test: construction with a custom options object must not
        // throw and must produce a usable runner.
        var options = new PlinkTunnelRunnerOptions(
            PortCheckIntervalMs: 250,
            KillGracePeriodMs: 1000);

        using var runner = new PlinkTunnelRunner(options);

        // Stop without Start must still be safe with custom timings.
        runner.Stop();
    }

    [Fact]
    public void Options_Default_MatchesHistoricalConstants()
    {
        PlinkTunnelRunnerOptions options = new PlinkTunnelRunnerOptions();

        Assert.Equal(2000, options.PortCheckIntervalMs);
        Assert.Equal(2000, options.KillGracePeriodMs);
    }

    [Fact]
    public void Constructor_NullOptions_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new PlinkTunnelRunner((PlinkTunnelRunnerOptions)null!));
    }

    private static int GetAvailableLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static PlinkTunnelRunner CreateRunner(ITcpListenerOwnershipProbe probe)
    {
        return new PlinkTunnelRunner(
            new PlinkTunnelRunnerOptions(1, 100),
            probe);
    }

    private static PlinkTunnelRunner CreateRunner(
        ITcpListenerOwnershipProbe probe,
        Func<ProcessStartInfo, IPlinkProcess> processFactory)
    {
        return new PlinkTunnelRunner(
            new PlinkTunnelRunnerOptions(1, 100),
            probe,
            processFactory);
    }

    private static string GetCommandProcessorPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "cmd.exe");
    }

    private sealed class FakeOwnershipProbe(TcpListenerOwnership ownership)
        : ITcpListenerOwnershipProbe
    {
        public int LastExpectedProcessId { get; private set; }

        public TcpListenerOwnership Probe(string bindHost, int port, int expectedProcessId)
        {
            LastExpectedProcessId = expectedProcessId;
            return ownership;
        }
    }

    private sealed class FakePlinkProcess : IPlinkProcess
    {
        private static int _nextProcessId = 10000;
        private readonly StreamReader _standardError = new(new MemoryStream([]));
        private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public event EventHandler? Exited;

        public int Id { get; } = Interlocked.Increment(ref _nextProcessId);

        public bool HasExited { get; private set; }

        public int ExitCode => 0;

        public StreamReader StandardError => _standardError;

        public bool WaitForExitResult { get; init; }

        public Exception? KillException { get; init; }

        public int KillCount { get; private set; }

        public int DisposeCount { get; private set; }

        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// Signalled once <see cref="Start"/> has been entered.
        /// </summary>
        /// <remarks>
        /// That instant is the only one at which the password file is guaranteed to be observable:
        /// the runner has already built its arguments, which is what writes the file, and it has not
        /// yet begun attesting the forwarded port, which is what deletes it.
        /// </remarks>
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// Optional gate the fake blocks on inside <see cref="Start"/>, so a test can hold the runner
        /// at that instant instead of racing it.
        /// </summary>
        public TaskCompletionSource? StartGate { get; init; }

        public bool Start()
        {
            Started.TrySetResult();
            StartGate?.Task.GetAwaiter().GetResult();
            return true;
        }

        public void Kill()
        {
            KillCount++;
            if (KillException is not null)
            {
                throw KillException;
            }
        }

        public bool WaitForExit(int milliseconds)
        {
            if (WaitForExitResult)
            {
                CompleteExit();
            }

            return WaitForExitResult;
        }

        public Task WaitForExitAsync(CancellationToken cancellationToken = default)
        {
            return _exited.Task.WaitAsync(cancellationToken);
        }

        public void CompleteExit()
        {
            if (HasExited)
            {
                return;
            }

            HasExited = true;
            _exited.TrySetResult();
            Exited?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose()
        {
            DisposeCount++;
            _standardError.Dispose();
            Disposed.TrySetResult();
        }
    }
}
