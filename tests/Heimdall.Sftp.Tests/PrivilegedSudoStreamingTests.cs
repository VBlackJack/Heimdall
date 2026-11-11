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
using System.Text;

namespace Heimdall.Sftp.Tests;

/// <summary>
/// The streaming sudo line reads the password itself and authenticates through a pipe, so the
/// payload stream can never be taken for a password and the password can never reach the file.
/// </summary>
public sealed class PrivilegedSudoStreamingTests
{
    private const string Body = "sh -c 'body'";

    // Quotes, expansions, a backtick, a backslash and shell operators: every character that
    // would change meaning if the password ever reached a command line.
    private const string Password = "P@ss'w\"rd $HOME `id` \\ ;&|<>*";

    private const string PasswordVariable = "_heimdall_password";

    private static string Line() => PrivilegedFileCommands.BuildPasswordStreamingSudoInvocation(Body);

    [Fact]
    public void StreamingLine_ReadsThePasswordBeforeAnySudoRuns()
    {
        string line = Line();

        Assert.StartsWith($"IFS= read -r {PasswordVariable};", line, StringComparison.Ordinal);
        Assert.Equal(0, line.IndexOf("IFS= read -r", StringComparison.Ordinal));
        Assert.True(
            line.IndexOf("sudo", StringComparison.Ordinal) > line.IndexOf("read -r", StringComparison.Ordinal),
            "sudo runs before the password line is consumed.");
    }

    [Fact]
    public void StreamingLine_OnlyPasswordSudoReadsFromAPipeFedByPrintf()
    {
        string line = Line();

        const string feeder = "printf '%s\\n' \"$" + PasswordVariable + "\" | sudo -S -p '' -v";
        Assert.Equal(1, Count(line, "sudo -S"));
        Assert.Contains(feeder, line, StringComparison.Ordinal);
        Assert.DoesNotContain("sudo -S -p '' " + Body, line, StringComparison.Ordinal);
        Assert.DoesNotContain("sudo -k", line, StringComparison.Ordinal);
    }

    [Fact]
    public void StreamingLine_RunsTheBodyOnlyThroughNonInteractiveSudo()
    {
        string line = Line();

        Assert.Equal(1, Count(line, Body));
        Assert.EndsWith("; sudo -n " + Body, line, StringComparison.Ordinal);
        Assert.Equal(0, Count(line, "sudo " + Body));
        Assert.Equal(0, Count(line, "-S -p '' " + Body));
    }

    [Fact]
    public void StreamingLine_AuthenticationFailureExitsWithItsOwnCodeBeforeTheBody()
    {
        string line = Line();
        string exitText = $"exit {PrivilegedFileCommands.SudoAuthenticationFailedExitStatus};";

        int exit = line.IndexOf(exitText, StringComparison.Ordinal);
        int body = line.IndexOf(Body, StringComparison.Ordinal);

        Assert.True(exit > 0, "the failure branch has no exit.");
        Assert.True(body > exit, "the body can run before the authentication failure exits.");
        Assert.Equal(1, Count(line, "exit "));

        // The password is dropped before the exit, so it is not left in a variable.
        Assert.Contains($"unset {PasswordVariable}; {exitText}", line, StringComparison.Ordinal);
    }

    [Fact]
    public void SudoAuthenticationFailedExitStatus_IsItsOwnStatus()
    {
        Assert.Equal(79, PrivilegedFileCommands.SudoAuthenticationFailedExitStatus);
        int[] others =
        [
            73, 74, 75, 76, 77, 78,
            PrivilegedFileCommands.FileTooLargeExitStatus,
            PrivilegedFileCommands.MetadataPreservationFailedExitStatus,
            PrivilegedFileCommands.ToolingUnavailableExitStatus,
            PrivilegedFileCommands.DestinationExistsExitStatus,
        ];
        Assert.DoesNotContain(PrivilegedFileCommands.SudoAuthenticationFailedExitStatus, others);
    }

    [Fact]
    public void StreamingLine_DropsThePasswordBeforeTheBodyAndKeepsItOffTheCommandText()
    {
        string line = Line();

        int lastUnset = line.LastIndexOf($"unset {PasswordVariable}", StringComparison.Ordinal);
        int body = line.IndexOf(Body, StringComparison.Ordinal);
        Assert.True(lastUnset > 0 && lastUnset < body, "the password is still set when the body starts.");

        // The builder is never given the password; the text carries none of its characters.
        Assert.DoesNotContain("P@ss", line, StringComparison.Ordinal);
        Assert.DoesNotContain(Password, line, StringComparison.Ordinal);
    }

    [Fact]
    public void StreamingLine_UsesOnlyPosixBuiltinsForThePassword()
    {
        string line = Line();

        Assert.DoesNotContain("read -s", line, StringComparison.Ordinal);
        Assert.DoesNotContain("<<<", line, StringComparison.Ordinal);
        Assert.DoesNotContain("[[", line, StringComparison.Ordinal);
        Assert.DoesNotContain("echo", line, StringComparison.Ordinal);
    }

    [Fact]
    public void EncodePasswordLine_IsThePasswordAndOneLineFeed()
    {
        byte[] bytes = PrivilegedFileTransfer.EncodePasswordLine(Password);

        Assert.Equal(Encoding.UTF8.GetBytes(Password + "\n"), bytes);
    }

    [Theory]
    [InlineData("two\nlines")]
    [InlineData("\n")]
    public void EncodePasswordLine_RefusesAPasswordThatWouldSpillIntoThePayload(string password)
    {
        Assert.Throws<ArgumentException>(() => PrivilegedFileTransfer.EncodePasswordLine(password));
    }

    [Fact]
    public void EncodePasswordLine_RefusesAnEmptyPassword()
    {
        Assert.Throws<ArgumentException>(() => PrivilegedFileTransfer.EncodePasswordLine(string.Empty));
    }

    [Fact]
    public async Task WriteInput_SendsThePasswordLineThenExactlyThePayload()
    {
        byte[] payload = [.. "hello\r\nworld\n"u8, 0, 1, 255, .. "\n"u8];
        byte[] prefix = PrivilegedFileTransfer.EncodePasswordLine(Password);
        using var remoteStdin = new MemoryStream();
        using var content = new MemoryStream(payload);

        await PrivilegedFileTransfer.WriteInputAsync(remoteStdin, content, prefix, CancellationToken.None);

        Assert.Equal([.. prefix, .. payload], remoteStdin.ToArray());
    }

    [Fact]
    public async Task WriteInput_WithNoPrefixSendsOnlyThePayload()
    {
        byte[] payload = "first line\nsecond line\n"u8.ToArray();
        using var remoteStdin = new MemoryStream();
        using var content = new MemoryStream(payload);

        await PrivilegedFileTransfer.WriteInputAsync(
            remoteStdin,
            content,
            ReadOnlyMemory<byte>.Empty,
            CancellationToken.None);

        Assert.Equal(payload, remoteStdin.ToArray());
    }

    private sealed class ThrowingStream(Exception failure) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count) => throw failure;

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => throw failure;

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => throw failure;
    }

    [Fact]
    public async Task TryWriteInput_ReturnsTheWriteFailureInsteadOfThrowingIt()
    {
        var failure = new IOException("channel closed");
        using var remoteStdin = new ThrowingStream(failure);
        using var content = new MemoryStream("payload"u8.ToArray());

        Exception? result = await PrivilegedFileTransfer.TryWriteInputAsync(
            remoteStdin,
            content,
            PrivilegedFileTransfer.EncodePasswordLine(Password),
            CancellationToken.None);

        Assert.Same(failure, result);
    }

    [Fact]
    public async Task TryWriteInput_StillThrowsOnCancellation()
    {
        using var remoteStdin = new MemoryStream();
        using var content = new MemoryStream("payload"u8.ToArray());
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => PrivilegedFileTransfer.TryWriteInputAsync(
                remoteStdin,
                content,
                ReadOnlyMemory<byte>.Empty,
                cancelled.Token));
    }

    [Fact]
    public void RethrowUnlessPasswordRefused_WriteFailureAfterAuthenticationExit_IsNotFatal()
    {
        var failure = new IOException("channel closed");

        PrivilegedFileTransfer.RethrowUnlessPasswordRefused(
            failure,
            PrivilegedFileCommands.SudoAuthenticationFailedExitStatus);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(78)]
    public void RethrowUnlessPasswordRefused_WriteFailureWithAnyOtherStatus_StillReportsTheIoFailure(int? status)
    {
        var failure = new IOException("channel closed");

        IOException thrown = Assert.Throws<IOException>(
            () => PrivilegedFileTransfer.RethrowUnlessPasswordRefused(failure, status));

        Assert.Same(failure, thrown);
    }

    [Fact]
    public void RethrowUnlessPasswordRefused_NoWriteFailure_NeverThrows()
    {
        PrivilegedFileTransfer.RethrowUnlessPasswordRefused(null, 1);
        PrivilegedFileTransfer.RethrowUnlessPasswordRefused(null, null);
    }

    [Fact]
    public void EditorWrite_AuthenticationExit_IsThePasswordRejectedError()
    {
        var result = new PrivilegedCommandResult(
            PrivilegedFileCommands.SudoAuthenticationFailedExitStatus,
            string.Empty,
            "sudo: 1 incorrect password attempt");

        var error = Assert.Throws<SudoPasswordRejectedException>(
            () => RemoteFileEditor.EnsureSudoWriteSucceeded(result));

        Assert.True(RemoteFileEditor.IsDeterministicRefusal(error));
    }

    [Fact]
    public void EditorWrite_OtherFailureStaysGenericAndSuccessPasses()
    {
        RemoteFileEditor.EnsureSudoWriteSucceeded(new PrivilegedCommandResult(0, string.Empty, string.Empty));

        var error = Assert.Throws<InvalidOperationException>(
            () => RemoteFileEditor.EnsureSudoWriteSucceeded(new PrivilegedCommandResult(1, string.Empty, "boom")));

        Assert.IsNotType<SudoPasswordRejectedException>(error);
        Assert.False(RemoteFileEditor.IsDeterministicRefusal(error));
    }

    private static int Count(string haystack, string needle)
    {
        int count = 0;
        for (int index = haystack.IndexOf(needle, StringComparison.Ordinal);
             index >= 0;
             index = haystack.IndexOf(needle, index + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    // --- The generated line, run by a real POSIX sh against a fake sudo ---------------------------
    // No real sudo is involved: a script named sudo, first on PATH inside a temp directory, plays
    // one of three roles. It records its arguments and reads standard input only where the real one
    // would, so a password that reached the payload, or a payload line taken for a password, shows
    // up in the files the run leaves behind.

    private const string FakeSudoScript =
        "#!/bin/sh\n"
        + "printf '%s\\n' \"$*\" >> sudo-args.log\n"
        + "mode=$FAKE_SUDO_MODE\n"
        + "if [ \"$1\" = \"-n\" ] && [ \"$2\" = \"-v\" ]; then\n"
        + "  [ \"$mode\" = cached ] && exit 0\n"
        + "  exit 1\n"
        + "fi\n"
        + "if [ \"$1\" = \"-S\" ]; then\n"
        + "  IFS= read -r given || given=\n"
        + "  if [ \"$given\" = \"$FAKE_SUDO_PASSWORD\" ]; then : > sudo-stamp; exit 0; fi\n"
        + "  echo 'sudo: 1 incorrect password attempt' >&2; exit 1\n"
        + "fi\n"
        + "if [ \"$1\" = \"-n\" ]; then\n"
        + "  shift\n"
        + "  case \"$mode\" in\n"
        + "    cached|nopasswd-command) exec \"$@\" ;;\n"
        + "    *) if [ -e sudo-stamp ]; then exec \"$@\"; fi ;;\n"
        + "  esac\n"
        + "  echo 'sudo: a password is required' >&2; exit 1\n"
        + "fi\n"
        + "exit 2\n";

    // The generated line, then whatever is left on standard input: after an early exit the rest
    // must be exactly the payload.
    private const string RunnerScript =
        "sh -c \"$HEIMDALL_TEST_LINE\"\n"
        + "echo $? > rc.txt\n"
        + "cat > rest.bin\n";

    private const string WriterBody = "sh -c 'cat > out.bin' sh";

    [PosixShellFact]
    public async Task Run_NoPasswordNeeded_WritesExactlyThePayload()
    {
        RunResult run = await RunAsync("cached", Password, Password);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(run.Payload, run.Out);
        Assert.Empty(run.Rest);
        Assert.DoesNotContain("-S", run.SudoArgs, StringComparison.Ordinal);
    }

    [PosixShellFact]
    public async Task Run_CommandWithoutPasswordButValidationNeedsOne_NeverPutsThePasswordInTheFile()
    {
        // The reported case: -v is refused without a password, yet the command itself needs none.
        RunResult run = await RunAsync("nopasswd-command", Password, Password);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(run.Payload, run.Out);
        // The payload's own first line is the password text, so exactly one occurrence is expected:
        // a second would be the password line that reached the file.
        Assert.Equal(1, Count(Encoding.UTF8.GetString(run.Out), Password + "\n"));
    }

    [PosixShellFact]
    public async Task Run_CorrectPassword_WritesExactlyThePayloadAndSudoSeesNoPassword()
    {
        RunResult run = await RunAsync("password-only", Password, Password);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(run.Payload, run.Out);
        Assert.Empty(run.Rest);
        Assert.DoesNotContain("P@ss", run.SudoArgs, StringComparison.Ordinal);
        Assert.DoesNotContain(Password, run.SudoArgs, StringComparison.Ordinal);
        Assert.Contains("-n sh -c", run.SudoArgs, StringComparison.Ordinal);
        Assert.Equal(1, Count(run.SudoArgs, "-S -p  -v"));
    }

    [PosixShellFact]
    public async Task Run_WrongPassword_ExitsWithTheAuthenticationCodeAndConsumesNoPayload()
    {
        RunResult run = await RunAsync("password-only", Password, "not the password");

        Assert.Equal(PrivilegedFileCommands.SudoAuthenticationFailedExitStatus, run.ExitCode);
        Assert.False(run.OutExists, "the writer ran after a refused password.");
        Assert.DoesNotContain("-n sh -c", run.SudoArgs, StringComparison.Ordinal);

        // sudo -S retried against the pipe, not the payload: every payload byte is still unread.
        Assert.Equal(run.Payload, run.Rest);
    }

    [PosixShellFact]
    public async Task Run_PayloadLineEqualToThePassword_IsKeptAsPayload()
    {
        RunResult run = await RunAsync("password-only", Password, Password);

        string written = Encoding.UTF8.GetString(run.Out);
        Assert.StartsWith(Password + "\n", written, StringComparison.Ordinal);
        Assert.Equal(run.Payload, run.Out);
    }

    private sealed record RunResult(
        int ExitCode,
        byte[] Payload,
        byte[] Out,
        bool OutExists,
        byte[] Rest,
        string SudoArgs);

    private static async Task<RunResult> RunAsync(string mode, string expectedPassword, string sentPassword)
    {
        string directory = Path.Combine(Path.GetTempPath(), "heimdall-sudo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "sudo"), FakeSudoScript, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(directory, "run.sh"), RunnerScript, new UTF8Encoding(false));

            // The first payload line is the password itself: a writer that lost a line, or a
            // password that reached the file, cannot go unseen.
            byte[] payload =
            [
                .. Encoding.UTF8.GetBytes(expectedPassword + "\n"),
                .. "second line\r\nthird line\n"u8,
                0, 1, 2, 255,
                .. "\nlast\n"u8,
            ];

            string line = PrivilegedFileCommands.BuildPasswordStreamingSudoInvocation(WriterBody);

            var start = new ProcessStartInfo(PosixShellFactAttribute.FindShell()!)
            {
                WorkingDirectory = directory,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add("run.sh");
            // The fake first, then the shell's own directory so cat and the like resolve even when
            // the shell was found by its install path rather than through PATH.
            start.Environment["PATH"] = string.Join(
                Path.PathSeparator,
                directory,
                Path.GetDirectoryName(start.FileName),
                Environment.GetEnvironmentVariable("PATH"));
            start.Environment["FAKE_SUDO_MODE"] = mode;
            start.Environment["FAKE_SUDO_PASSWORD"] = expectedPassword;
            start.Environment["HEIMDALL_TEST_LINE"] = line;

            using Process process = Process.Start(start)!;
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();

            await using (Stream stdin = process.StandardInput.BaseStream)
            {
                byte[] prefix = PrivilegedFileTransfer.EncodePasswordLine(sentPassword);
                using var content = new MemoryStream(payload);
                try
                {
                    await PrivilegedFileTransfer.WriteInputAsync(stdin, content, prefix, CancellationToken.None);
                }
                catch (IOException)
                {
                    // The run ended before reading it all; the leftovers are asserted below.
                }
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw;
            }

            await Task.WhenAll(stdout, stderr);

            string rcText = File.ReadAllText(Path.Combine(directory, "rc.txt")).Trim();
            string outPath = Path.Combine(directory, "out.bin");
            string logPath = Path.Combine(directory, "sudo-args.log");

            return new RunResult(
                int.Parse(rcText, System.Globalization.CultureInfo.InvariantCulture),
                payload,
                File.Exists(outPath) ? File.ReadAllBytes(outPath) : [],
                File.Exists(outPath),
                File.ReadAllBytes(Path.Combine(directory, "rest.bin")),
                File.Exists(logPath) ? File.ReadAllText(logPath) : string.Empty);
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // A temp directory left behind is harmless.
            }
        }
    }
}

/// <summary>A fact that runs only where an unprivileged POSIX <c>sh</c> can be found.</summary>
public sealed class PosixShellFactAttribute : FactAttribute
{
    private const string ShellName = "sh";
    private const string GitForWindowsShell = @"Git\usr\bin\sh.exe";

    public PosixShellFactAttribute()
    {
        if (FindShell() is null)
        {
            Skip = "No POSIX sh was found on PATH.";
        }
    }

    internal static string? FindShell()
    {
        string executable = OperatingSystem.IsWindows() ? ShellName + ".exe" : ShellName;
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(directory, executable);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        if (OperatingSystem.IsWindows())
        {
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string candidate = Path.Combine(programFiles, GitForWindowsShell);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
