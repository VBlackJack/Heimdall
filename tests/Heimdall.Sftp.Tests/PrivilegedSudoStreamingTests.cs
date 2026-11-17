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
using System.Text;

namespace Heimdall.Sftp.Tests;

/// <summary>
/// The privileged write stages the content as the connecting user and installs it with one sudo
/// call that shares no input with the payload, so a password can never reach the file and a
/// payload line can never be read as a password.
/// </summary>
public sealed class PrivilegedSudoStreamingTests
{
    // Quotes, expansions, a backtick, a backslash and shell operators: every character that
    // would change meaning if the password ever reached a command line.
    private const string Password = "P@ss'w\"rd $HOME `id` \\ ;&|<>*";

    private const string Target = "/srv/app/app.conf";

    private const long DeclaredBytes = 12;

    private static string Command(bool passwordMode = true, string target = Target, bool overwrite = true)
        => PrivilegedFileCommands.BuildStagedWriteCommand(target, overwrite, passwordMode, DeclaredBytes);

    // --- The command text ----------------------------------------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Command_IsOneSimpleShellCommandWithNoNewlineAndNoBang(bool passwordMode)
    {
        string command = Command(passwordMode);

        Assert.StartsWith("sh -c '", command, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', command);
        Assert.DoesNotContain('\r', command);
        Assert.DoesNotContain('!', command);
        Assert.EndsWith(" '" + Target + "' '12'", command, StringComparison.Ordinal);
    }

    [Fact]
    public void Command_NeverContainsThePasswordOrAnythingLikeIt()
    {
        // The builder is not given the password; the mode is the only trace of it.
        foreach (bool passwordMode in new[] { true, false })
        {
            string command = Command(passwordMode);
            Assert.DoesNotContain("P@ss", command, StringComparison.Ordinal);
            Assert.DoesNotContain(Password, command, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Command_ModeAndFlagAreQuotedPositionalParameters()
    {
        Assert.Contains(" 'password' '1' '" + Target + "' '12'", Command(true), StringComparison.Ordinal);
        Assert.Contains(" 'none' '0' '" + Target + "' '12'", Command(false, overwrite: false), StringComparison.Ordinal);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PrivilegedFileCommands.BuildStagedWriteCommand(Target, true, true, -1));
    }

    [Fact]
    public void Command_TargetWithQuoteAndBang_StaysInertAndHistorySafe()
    {
        string command = Command(target: "/srv/it's here; touch /tmp/pwned!x");

        // A quote is closed, escaped and reopened; a bang is escaped outside the quotes.
        Assert.EndsWith(@"'/srv/it'\''s here; touch /tmp/pwned'\!'x' '12'", command, StringComparison.Ordinal);
        foreach (int index in IndexesOf(command, "!"))
        {
            Assert.True(index > 0 && command[index - 1] == '\\', "a bang is not escaped.");
        }
    }

    [Fact]
    public void Command_RefusesATargetWithAControlCharacter()
    {
        Assert.ThrowsAny<ArgumentException>(() => Command(target: "/srv/line\nbreak"));
        Assert.ThrowsAny<ArgumentException>(() => Command(target: " "));
    }

    [Fact]
    public void Scripts_HoldNoNewlineAndTheOuterScriptHoldsNoBang()
    {
        Assert.DoesNotContain('\n', PrivilegedFileCommands.StagedWriteOuterScript);
        Assert.DoesNotContain('!', PrivilegedFileCommands.StagedWriteOuterScript);
        Assert.DoesNotContain('\n', PrivilegedFileCommands.StagedInstallScript);

        // The installer's bangs travel as a placeholder, so the placeholder must not occur in it.
        Assert.Contains('!', PrivilegedFileCommands.StagedInstallScript);
        Assert.DoesNotContain('^', PrivilegedFileCommands.StagedInstallScript);
    }

    [Fact]
    public void OuterScript_ReadsThePasswordFirstStagesAndChecksTheSizeBeforeAnySudo()
    {
        string outer = PrivilegedFileCommands.StagedWriteOuterScript;

        int read = outer.IndexOf("IFS= read -r p;", StringComparison.Ordinal);
        int mktemp = outer.IndexOf("mktemp -d", StringComparison.Ordinal);
        int stage = outer.IndexOf("cat > \"$d/f\"", StringComparison.Ordinal);
        int size = outer.IndexOf("[ $(wc -c < \"$d/f\") -eq \"$size\" ] || exit 74;", StringComparison.Ordinal);
        int firstSudo = outer.IndexOf("sudo", StringComparison.Ordinal);
        Assert.True(read >= 0 && read < mktemp && mktemp < stage && stage < size && size < firstSudo);

        Assert.Contains("umask 077;", outer, StringComparison.Ordinal);
        Assert.Contains("LC_ALL=C; export LC_ALL;", outer, StringComparison.Ordinal);
        Assert.Contains("trap 'rm -rf -- \"$d\"' EXIT;", outer, StringComparison.Ordinal);
        Assert.EndsWith("p=; unset p; exit $rc", outer, StringComparison.Ordinal);
    }

    [Fact]
    public void OuterScript_TriesTheInstallWithoutAPasswordFirstAndOffersThePasswordOnce()
    {
        string outer = PrivilegedFileCommands.StagedWriteOuterScript;

        int attempt = outer.IndexOf("sudo -n sh -c \"$inner\"", StringComparison.Ordinal);
        int forward = outer.IndexOf("*" + PrivilegedFileCommands.InstallStartedMarker + "*)", StringComparison.Ordinal);
        int withPassword = outer.IndexOf("sudo -S -p '' sh -c \"$inner\"", StringComparison.Ordinal);
        Assert.True(attempt > 0 && attempt < forward && forward < withPassword);
        Assert.Equal(1, Count(outer, "sudo -n"));
        Assert.Equal(1, Count(outer, "sudo -S"));

        // The first attempt has no input at all, so no password can sit unread on sudo's stdin.
        Assert.Contains("</dev/null 2> \"$d/e\"", outer, StringComparison.Ordinal);

        // The only sudo that reads a password is fed by a printf pipe holding that password alone.
        Assert.Contains("printf '%s\\n' \"$p\" | sudo -S -p '' sh -c \"$inner\"", outer, StringComparison.Ordinal);
        Assert.DoesNotContain("sudo -v", outer, StringComparison.Ordinal);
        Assert.DoesNotContain("sudo -k", outer, StringComparison.Ordinal);
        Assert.DoesNotContain("true", outer, StringComparison.Ordinal);

        // When the installer started, its stderr is forwarded and its status kept.
        Assert.Contains("cat \"$d/e\" >&2; p=; unset p; exit $rc ;; esac;", outer, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallerScript_AnnouncesItselfFirstAndChecksTheDescriptorBeforeTheWriter()
    {
        string installer = PrivilegedFileCommands.StagedInstallScript;

        Assert.StartsWith(
            $"printf '%s\\n' '{PrivilegedFileCommands.InstallStartedMarker}' >&2; ",
            installer,
            StringComparison.Ordinal);

        int dirCheck = installer.IndexOf("stat -c '%u %a' -- \"$sd\"", StringComparison.Ordinal);
        Assert.True(dirCheck > 0, "the staging directory is not checked.");
        Assert.Contains("sd=${staged%/*};", installer, StringComparison.Ordinal);
        Assert.Contains("[ -L \"$sd\" ]", installer, StringComparison.Ordinal);
        Assert.Contains("$((0$dm & 022))", installer, StringComparison.Ordinal);
        Assert.DoesNotContain("( : <", installer, StringComparison.Ordinal);
        int open = installer.IndexOf("exec 3< \"$staged\"", StringComparison.Ordinal);
        Assert.True(dirCheck < open, "the directory is checked after the file is opened.");
        int check = installer.IndexOf("stat -Lc %u:%h /proc/self/fd/3", StringComparison.Ordinal);
        int redirect = installer.IndexOf("exec 0<&3 3<&-", StringComparison.Ordinal);
        int writer = installer.IndexOf("cat > payload", StringComparison.Ordinal);
        Assert.True(open > 0 && open < check && check < redirect && redirect < writer);
        Assert.Contains("\"$uid:1\"", installer, StringComparison.Ordinal);
        Assert.Contains("[ -f /proc/self/fd/3 ]", installer, StringComparison.Ordinal);
        Assert.Contains("exit 73;", installer, StringComparison.Ordinal);

        // The atomic writer is the existing one, whole.
        Assert.EndsWith(PrivilegedFileCommandsAtomicScript(), installer, StringComparison.Ordinal);

        // Root does not delete the user's staged file.
        Assert.DoesNotContain("rm -rf -- \"$staged\"", installer, StringComparison.Ordinal);
        Assert.DoesNotContain("rm -f -- \"$staged\"", installer, StringComparison.Ordinal);
    }

    private static string PrivilegedFileCommandsAtomicScript()
    {
        // BuildAtomicWriteBody wraps the atomic script as "sh -c '<script>' sh '<target>'" with a
        // replace flag in front; undoing the wrapping gives back the script it carries.
        string body = PrivilegedFileCommands.BuildAtomicWriteBody("/x");
        const string prefix = "sh -c '";
        const string suffix = "' sh '/x'";
        string script = body[prefix.Length..^suffix.Length].Replace(@"'\''", "'", StringComparison.Ordinal);
        return script["replace=1; ".Length..];
    }

    [Fact]
    public void ExitStatuses_AreDistinctAndRetire79()
    {
        Assert.Equal(74, PrivilegedFileCommands.StagingFailedExitStatus);
        Assert.Equal(127, PrivilegedFileCommands.CommandNotFoundExitStatus);
        Assert.Contains(
            $"|| exit {PrivilegedFileCommands.StagingFailedExitStatus};",
            PrivilegedFileCommands.StagedWriteOuterScript,
            StringComparison.Ordinal);
        Assert.DoesNotContain("exit 79", PrivilegedFileCommands.StagedWriteOuterScript, StringComparison.Ordinal);
        Assert.DoesNotContain("exit 79", PrivilegedFileCommands.StagedInstallScript, StringComparison.Ordinal);
    }

    // --- Classification --------------------------------------------------------------------------

    private const string Marker = PrivilegedFileCommands.InstallStartedMarker + "\n";

    [Theory]
    [InlineData(0, "", PrivilegedWriteFailureKind.None)]
    [InlineData(0, Marker, PrivilegedWriteFailureKind.None)]
    [InlineData(74, "mktemp: failed to create directory", PrivilegedWriteFailureKind.StagingFailed)]
    [InlineData(74, "", PrivilegedWriteFailureKind.StagingFailed)]
    [InlineData(74, Marker + "anything", PrivilegedWriteFailureKind.InstallFailed)]
    [InlineData(1, "sudo: no password was provided\nsudo: 1 incorrect password attempt", PrivilegedWriteFailureKind.PasswordRejected)]
    [InlineData(1, "Sorry, try again.", PrivilegedWriteFailureKind.PasswordRejected)]
    [InlineData(1, "sudo: a password is required", PrivilegedWriteFailureKind.PasswordUnavailable)]
    [InlineData(1, "sudo: a terminal is required to read the password", PrivilegedWriteFailureKind.PasswordUnavailable)]
    [InlineData(1, "sudo: sorry, you must have a tty to run sudo", PrivilegedWriteFailureKind.PasswordUnavailable)]
    [InlineData(1, "alice is not in the sudoers file.  This incident will be reported.", PrivilegedWriteFailureKind.NotPermitted)]
    [InlineData(1, "Sorry, user alice is not allowed to execute '/bin/sh' as root on host.", PrivilegedWriteFailureKind.NotPermitted)]
    [InlineData(127, "sh: 1: sudo: not found", PrivilegedWriteFailureKind.SudoMissing)]
    [InlineData(1, "sudo: unable to resolve host labbox", PrivilegedWriteFailureKind.SudoFailed)]
    [InlineData(78, Marker + "Refusing existing destination.", PrivilegedWriteFailureKind.InstallFailed)]
    [InlineData(73, Marker + "Refusing symbolic-link staged file.", PrivilegedWriteFailureKind.InstallFailed)]
    [InlineData(77, Marker + "Refusing: stat is not available on the server.", PrivilegedWriteFailureKind.InstallFailed)]
    [InlineData(1, Marker + "sudo: Sorry, try again.", PrivilegedWriteFailureKind.InstallFailed)]
    public void ClassifyWrite_NamesTheStageThatFailed(int exit, string stderr, PrivilegedWriteFailureKind expected)
    {
        Assert.Equal(expected, PrivilegedFileTransfer.ClassifyWrite(new PrivilegedCommandResult(exit, string.Empty, stderr)));
    }

    [Fact]
    public void EnsureStageSucceeded_ThrowsTheNamedKindAndLeavesInstallerStatusesAlone()
    {
        PrivilegedWriteFailedException rejected = Assert.Throws<PrivilegedWriteFailedException>(
            () => PrivilegedFileTransfer.EnsureStageSucceeded(
                new PrivilegedCommandResult(1, string.Empty, "sudo: 1 incorrect password attempt")));
        Assert.Equal(PrivilegedWriteFailureKind.PasswordRejected, rejected.Kind);

        PrivilegedFileTransfer.EnsureStageSucceeded(new PrivilegedCommandResult(0, string.Empty, Marker));
        PrivilegedFileTransfer.EnsureStageSucceeded(
            new PrivilegedCommandResult(78, string.Empty, Marker + "Refusing existing destination."));

        InvalidOperationException generic = Assert.ThrowsAny<InvalidOperationException>(
            () => PrivilegedFileTransfer.EnsureStageSucceeded(
                new PrivilegedCommandResult(1, string.Empty, "sudo: unable to resolve host labbox")));
        Assert.IsNotType<PrivilegedWriteFailedException>(generic);
        Assert.Contains("unable to resolve host", generic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CleanWriteError_DropsTheStartLine()
    {
        Assert.Equal(
            "Refusing existing destination.",
            PrivilegedFileTransfer.CleanWriteError(Marker + "Refusing existing destination."));
        Assert.Equal(string.Empty, PrivilegedFileTransfer.CleanWriteError(null));
    }

    [Fact]
    public void EditorWrite_StageFailuresAreNamedAndOnlyTransientOnesAreRetried()
    {
        var rejected = Assert.Throws<PrivilegedWriteFailedException>(
            () => RemoteFileEditor.EnsureSudoWriteSucceeded(
                new PrivilegedCommandResult(1, string.Empty, "sudo: 1 incorrect password attempt")));
        Assert.Equal(PrivilegedWriteFailureKind.PasswordRejected, rejected.Kind);
        Assert.True(RemoteFileEditor.IsDeterministicRefusal(rejected));

        var staging = Assert.Throws<PrivilegedWriteFailedException>(
            () => RemoteFileEditor.EnsureSudoWriteSucceeded(
                new PrivilegedCommandResult(74, string.Empty, "mktemp: no space")));
        Assert.Equal(PrivilegedWriteFailureKind.StagingFailed, staging.Kind);
        Assert.False(RemoteFileEditor.IsDeterministicRefusal(staging));

        RemoteFileEditor.EnsureSudoWriteSucceeded(new PrivilegedCommandResult(0, string.Empty, Marker));

        var installer = Assert.Throws<InvalidOperationException>(
            () => RemoteFileEditor.EnsureSudoWriteSucceeded(
                new PrivilegedCommandResult(76, string.Empty, Marker + "Refusing: metadata")));
        Assert.IsNotType<PrivilegedWriteFailedException>(installer);
        Assert.DoesNotContain(PrivilegedFileCommands.InstallStartedMarker, installer.Message, StringComparison.Ordinal);
    }

    // --- The local stream ------------------------------------------------------------------------

    [Fact]
    public void EncodePasswordLine_IsThePasswordAndOneLineFeed()
    {
        Assert.Equal(Encoding.UTF8.GetBytes(Password + "\n"), PrivilegedFileTransfer.EncodePasswordLine(Password));
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

    private sealed class FailingReadStream(Exception failure) : MemoryStream
    {
        public override int Read(byte[] buffer, int offset, int count) => throw failure;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => throw failure;

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => throw failure;
    }

    [Fact]
    public async Task ALocalReadFailure_IsNeverHiddenByTheRemoteRefusalOfTheShortFile()
    {
        var failure = new IOException("disk read error");
        using var remoteStdin = new MemoryStream();
        using var content = new FailingReadStream(failure);

        Exception? captured = await PrivilegedFileTransfer.TryWriteInputAsync(
            remoteStdin,
            content,
            ReadOnlyMemory<byte>.Empty,
            CancellationToken.None);

        Assert.NotNull(captured);

        // The remote side refuses the short file with its staging status; that status must not
        // stand in for the local read failure.
        IOException thrown = Assert.Throws<IOException>(
            () => PrivilegedFileTransfer.RethrowUnlessStageFailed(
                captured,
                PrivilegedFileCommands.StagingFailedExitStatus,
                string.Empty));
        Assert.Same(failure, thrown);
    }

    [Fact]
    public async Task ExecuteAtomicWrite_RefusesAStreamWhoseLengthIsUnknown()
    {
        using var unseekable = new NonSeekableStream();
        using var client = new Renci.SshNet.SshClient("localhost", "user", "pw");

        ArgumentException refusal = await Assert.ThrowsAsync<ArgumentException>(
            () => PrivilegedFileTransfer.ExecuteAtomicWriteAsync(
                client,
                Target,
                overwrite: true,
                unseekable,
                sudoPassword: null,
                CancellationToken.None));

        Assert.Equal("content", refusal.ParamName);
    }

    private sealed class NonSeekableStream : MemoryStream
    {
        public override bool CanSeek => false;
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

    [Theory]
    [InlineData(74, "mktemp: failed to create directory")]
    [InlineData(1, "sudo: 1 incorrect password attempt")]
    [InlineData(127, "sh: 1: sudo: not found")]
    public void RethrowUnlessStageFailed_AWriteFailureBeforeTheInstallerIsExplainedByTheStatus(int exit, string stderr)
    {
        PrivilegedFileTransfer.RethrowUnlessStageFailed(new IOException("channel closed"), exit, stderr);
    }

    [Theory]
    [InlineData(0, "")]
    [InlineData(1, Marker + "Refusing: metadata")]
    [InlineData(-1, "")]
    public void RethrowUnlessStageFailed_AWriteFailureAnywhereElseStillReportsTheIoFailure(int exit, string stderr)
    {
        var failure = new IOException("channel closed");

        IOException thrown = Assert.Throws<IOException>(
            () => PrivilegedFileTransfer.RethrowUnlessStageFailed(failure, exit, stderr));

        Assert.Same(failure, thrown);
    }

    [Fact]
    public void RethrowUnlessStageFailed_NoWriteFailure_NeverThrows()
    {
        PrivilegedFileTransfer.RethrowUnlessStageFailed(null, 1, "anything");
        PrivilegedFileTransfer.RethrowUnlessStageFailed(null, 0, string.Empty);
    }

    private static int Count(string haystack, string needle) => IndexesOf(haystack, needle).Count();

    private static IEnumerable<int> IndexesOf(string haystack, string needle)
    {
        for (int index = haystack.IndexOf(needle, StringComparison.Ordinal);
             index >= 0;
             index = haystack.IndexOf(needle, index + needle.Length, StringComparison.Ordinal))
        {
            yield return index;
        }
    }

    // --- The generated command, run by a real POSIX sh against a fake sudo ---------------------
    // No real sudo is involved: a script named sudo, first on PATH inside a temp directory, plays
    // one role per run. It records its arguments, reads the password line the way sudo -S does,
    // and otherwise runs the installer as the current user, the way sudo would as root.

    private const string FakeSudoScript =
        "#!/bin/sh\n"
        + "printf 'CALL %s\\n' \"$1\" >> sudo-calls.log\n"
        + "printf '%s\\n' \"$@\" >> sudo-args.log\n"
        + "case \"$FAKE_SUDO_MODE\" in\n"
        + "  missing) echo 'sh: 1: sudo: not found' >&2; exit 127 ;;\n"
        + "  requiretty) echo 'sudo: sorry, you must have a tty to run sudo' >&2; exit 1 ;;\n"
        + "  notsudoer) echo 'alice is not in the sudoers file.  This incident will be reported.' >&2; exit 1 ;;\n"
        + "esac\n"
        + "if [ \"$1\" = \"-S\" ]; then\n"
        + "  shift 3\n"
        + "  IFS= read -r given || given=\n"
        + "  echo read >> sudo-reads.log\n"
        + "  if IFS= read -r extra; then echo extra >> sudo-reads.log; fi\n"
        + "  if [ \"$given\" != \"$FAKE_SUDO_PASSWORD\" ]; then\n"
        + "    echo 'sudo: no password was provided' >&2; echo 'sudo: 1 incorrect password attempt' >&2; exit 1\n"
        + "  fi\n"
        + "elif [ \"$1\" = \"-n\" ]; then\n"
        + "  shift\n"
        + "  if IFS= read -r leaked; then echo leaked >> sudo-reads.log; fi\n"
        + "  if [ \"$FAKE_SUDO_MODE\" != nopasswd ]; then echo 'sudo: a password is required' >&2; exit 1; fi\n"
        + "else\n"
        + "  exit 2\n"
        + "fi\n"
        + "n=$#; i=0\n"
        + "while [ $i -lt $n ]; do\n"
        + "  a=$1; shift; i=$((i+1))\n"
        + "  if [ \"$FAKE_SUDO_TAMPER\" = uid ] && [ $i -eq $n ]; then a=$((a+1)); fi\n"
        + "  if [ \"$FAKE_SUDO_TAMPER\" = dirmode ] && [ $i -eq 3 ]; then a=\"$FAKE_STAT_FUNCTION$a\"; fi\n"
        + "  if [ \"$FAKE_SUDO_TAMPER\" = hardlink ] && [ $i -eq $((n-1)) ]; then ln \"$a\" \"$a.hl\"; fi\n"
        + "  if [ \"$FAKE_SUDO_TAMPER\" = symlink ] && [ $i -eq $((n-1)) ]; then\n"
        + "    mv \"$a\" \"$a.real\"; ln -s \"${a##*/}.real\" \"$a\"\n"
        + "    [ -L \"$a\" ] || { echo NOSYMLINK >&2; exit 98; }\n"
        + "  fi\n"
        + "  set -- \"$@\" \"$a\"\n"
        + "done\n"
        + "exec \"$@\"\n";

    // Defined at the head of the installer, so it takes precedence over whichever stat the PATH of
    // the machine running the test would find. It reports the staging directory (and only it) as
    // writable by group, which a file system without permission bits cannot be made to do.
    private const string StatReportingGroupWritableDirectory =
        "stat() { if [ \"$1\" = -c ] && [ \"$2\" = '%u %a' ]; then "
        + "echo \"$(command stat -c %u -- \"$4\") 770\"; else command stat \"$@\"; fi; }; ";

    private const string RunnerScript =
        "mkdir stage-tmp\n"
        + "sh -c \"$HEIMDALL_TEST_COMMAND\" 2> stderr.txt\n"
        + "echo $? > rc.txt\n"
        + "cat > rest.bin\n";

    private static readonly byte[] PayloadStartingWithThePassword =
    [
        .. Encoding.UTF8.GetBytes(Password + "\n"),
        .. "second line\r\nthird line\n"u8,
    ];

    private static readonly byte[] PayloadWithoutFinalNewline = "no final newline"u8.ToArray();

    private static readonly byte[] PayloadWithNulBytes = [.. "a"u8, 0, 0, 1, 255, 0, .. "\nb\n"u8, 0];

    public static TheoryData<string, byte[]> Payloads => new()
    {
        { "first line equals the password", PayloadStartingWithThePassword },
        { "empty", [] },
        { "no final newline", PayloadWithoutFinalNewline },
        { "NUL bytes", PayloadWithNulBytes },
    };

    [PosixShellTheory]
    [MemberData(nameof(Payloads))]
    public async Task Run_PasswordAcceptedWithNoTicketKept_WritesTheFileByteForByte(string name, byte[] payload)
    {
        // timestamp_timeout=0: the password is good, and nothing is remembered for a second call.
        RunResult run = await RunAsync("password", passwordMode: true, Password, Password, payload);

        Assert.True(run.Completed, name);
        Assert.Equal(0, run.ExitCode);
        Assert.Equal(payload, run.Out);
        Assert.Empty(run.Rest);
        Assert.Equal(PrivilegedWriteFailureKind.None, run.Kind);
        Assert.Equal(["read"], run.SudoReads);

        // The install is tried without a password first; the password form then runs exactly once.
        Assert.Equal(["CALL -n", "CALL -S"], run.SudoCalls);
        Assert.Empty(run.StagingLeftovers);
    }

    [PosixShellTheory]
    [MemberData(nameof(Payloads))]
    public async Task Run_NoPasswordNeeded_WritesTheFileByteForByte(string name, byte[] payload)
    {
        RunResult run = await RunAsync("nopasswd", passwordMode: false, null, null, payload);

        Assert.True(run.Completed, name);
        Assert.Equal(0, run.ExitCode);
        Assert.Equal(payload, run.Out);
        Assert.Equal(PrivilegedWriteFailureKind.None, run.Kind);
        Assert.Empty(run.SudoReads);
        Assert.Equal(["CALL -n"], run.SudoCalls);
        Assert.Equal("-n", run.SudoArgs.Split('\n')[0]);
        Assert.Empty(run.StagingLeftovers);
    }

    [PosixShellFact]
    public async Task Run_NoPasswordNeededButOneIsConfigured_NeverOffersItToSudo()
    {
        // NOPASSWD or a live ticket: the password line must not sit unread on sudo's input, where
        // an input log would keep it.
        RunResult run = await RunAsync("nopasswd", passwordMode: true, Password, Password, PayloadWithNulBytes);

        Assert.True(run.Completed);
        Assert.Equal(0, run.ExitCode);
        Assert.Equal(PayloadWithNulBytes, run.Out);
        Assert.Equal(["CALL -n"], run.SudoCalls);
        Assert.Empty(run.SudoReads);
        Assert.DoesNotContain(Password, run.SudoArgs, StringComparison.Ordinal);
        Assert.Empty(run.StagingLeftovers);
    }

    [PosixShellTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Run_APayloadCutShort_NeverReachesTheTarget(bool passwordMode)
    {
        // The count the remote side is told is larger than what arrives, as when a local read stops.
        RunResult run = await RunAsync(
            "password",
            passwordMode,
            Password,
            Password,
            PayloadWithNulBytes,
            declaredBytes: PayloadWithNulBytes.Length + 7);

        Assert.True(run.Completed);
        Assert.Equal(PrivilegedFileCommands.StagingFailedExitStatus, run.ExitCode);
        Assert.Equal(PrivilegedWriteFailureKind.StagingFailed, run.Kind);
        Assert.False(run.OutExists);
        Assert.Empty(run.SudoCalls);
        Assert.Empty(run.StagingLeftovers);
    }

    [PosixShellFact]
    public async Task Run_AnOversizedPayload_IsRefusedToo()
    {
        RunResult run = await RunAsync(
            "nopasswd",
            passwordMode: false,
            null,
            null,
            PayloadWithNulBytes,
            declaredBytes: PayloadWithNulBytes.Length - 1);

        Assert.Equal(PrivilegedFileCommands.StagingFailedExitStatus, run.ExitCode);
        Assert.False(run.OutExists);
        Assert.Empty(run.SudoCalls);
    }

    [PosixShellFact]
    public async Task Run_PasswordNeededButNoneConfigured_FailsAsUnavailableAndTouchesNothing()
    {
        RunResult run = await RunAsync("password", passwordMode: false, null, null, PayloadWithNulBytes);

        Assert.True(run.Completed);
        Assert.NotEqual(0, run.ExitCode);
        Assert.Equal(PrivilegedWriteFailureKind.PasswordUnavailable, run.Kind);
        Assert.False(run.OutExists);
        Assert.Empty(run.StagingLeftovers);
    }

    [PosixShellFact]
    public async Task Run_WrongPassword_IsTheOnlyRejectedPasswordAndWritesNothing()
    {
        RunResult run = await RunAsync("password", passwordMode: true, Password, "not the password", PayloadStartingWithThePassword);

        Assert.True(run.Completed);
        Assert.Equal(1, run.ExitCode);
        Assert.Equal(PrivilegedWriteFailureKind.PasswordRejected, run.Kind);
        Assert.False(run.OutExists);
        Assert.Empty(run.StagingLeftovers);

        // sudo -S read the password line and nothing else: the payload never reached it.
        Assert.Equal(["read"], run.SudoReads);
    }

    [PosixShellTheory]
    [InlineData("requiretty", PrivilegedWriteFailureKind.PasswordUnavailable)]
    [InlineData("notsudoer", PrivilegedWriteFailureKind.NotPermitted)]
    [InlineData("missing", PrivilegedWriteFailureKind.SudoMissing)]
    public async Task Run_EachSudoFailure_HasItsOwnKindAndLeavesNothingBehind(
        string mode,
        PrivilegedWriteFailureKind expected)
    {
        RunResult run = await RunAsync(mode, passwordMode: true, Password, Password, PayloadWithNulBytes);

        Assert.True(run.Completed);
        Assert.NotEqual(0, run.ExitCode);
        Assert.Equal(expected, run.Kind);
        Assert.NotEqual(PrivilegedWriteFailureKind.PasswordRejected, run.Kind);
        Assert.False(run.OutExists);
        Assert.Empty(run.StagingLeftovers);
    }

    [PosixShellFact]
    public async Task Run_PasswordNeverReachesTheFakeSudoArgumentsOrTheFile()
    {
        RunResult run = await RunAsync("password", passwordMode: true, Password, Password, PayloadWithNulBytes);

        Assert.True(run.Completed);
        Assert.DoesNotContain(Password, run.SudoArgs, StringComparison.Ordinal);
        Assert.DoesNotContain("P@ss", run.SudoArgs, StringComparison.Ordinal);
        Assert.DoesNotContain(Password, Encoding.UTF8.GetString(run.Out), StringComparison.Ordinal);
    }

    [PosixShellFact]
    public async Task Run_AWrongUidArgument_IsRefusedAsUnsafeAndNothingIsInstalled()
    {
        RunResult run = await RunAsync(
            "password",
            passwordMode: true,
            Password,
            Password,
            PayloadWithNulBytes,
            tamper: "uid");

        Assert.True(run.Completed);
        Assert.Equal(73, run.ExitCode);
        Assert.Equal(PrivilegedWriteFailureKind.InstallFailed, run.Kind);
        Assert.False(run.OutExists);
        Assert.Empty(run.StagingLeftovers);
    }

    [PosixShellFact]
    public async Task Run_ASecondHardLinkOnTheStagedFile_IsRefusedByTheDescriptorCheck()
    {
        RunResult run = await RunAsync(
            "password",
            passwordMode: true,
            Password,
            Password,
            PayloadWithNulBytes,
            tamper: "hardlink");

        Assert.True(run.Completed);
        Assert.Equal(73, run.ExitCode);
        Assert.Equal(PrivilegedWriteFailureKind.InstallFailed, run.Kind);
        Assert.Contains("single-link", run.Stderr, StringComparison.Ordinal);
        Assert.False(run.OutExists);
        Assert.Empty(run.StagingLeftovers);
    }

    [PosixShellFact]
    public async Task Run_AStagingDirectoryWritableByTheGroup_IsRefused()
    {
        RunResult run = await RunAsync(
            "password",
            passwordMode: true,
            Password,
            Password,
            PayloadWithNulBytes,
            tamper: "dirmode");

        Assert.True(run.Completed);
        Assert.Equal(73, run.ExitCode);
        Assert.Equal(PrivilegedWriteFailureKind.InstallFailed, run.Kind);
        Assert.Contains("not private", run.Stderr, StringComparison.Ordinal);
        Assert.False(run.OutExists);
        Assert.Empty(run.StagingLeftovers);
    }

    [PosixSymlinkFact]
    public async Task Run_ASymlinkInPlaceOfTheStagedFile_IsRefused()
    {
        RunResult run = await RunAsync(
            "password",
            passwordMode: true,
            Password,
            Password,
            PayloadWithNulBytes,
            tamper: "symlink");

        Assert.True(run.Completed);
        Assert.Equal(73, run.ExitCode);
        Assert.Contains("symbolic-link", run.Stderr, StringComparison.Ordinal);
        Assert.False(run.OutExists);
        Assert.Empty(run.StagingLeftovers);
    }

    private sealed record RunResult(
        bool Completed,
        int ExitCode,
        string Stderr,
        byte[] Out,
        bool OutExists,
        byte[] Rest,
        string SudoArgs,
        string[] SudoReads,
        string[] StagingLeftovers,
        string[] SudoCalls)
    {
        public PrivilegedWriteFailureKind Kind
            => PrivilegedFileTransfer.ClassifyWrite(new PrivilegedCommandResult(ExitCode, string.Empty, Stderr));
    }

    private static async Task<RunResult> RunAsync(
        string mode,
        bool passwordMode,
        string? expectedPassword,
        string? sentPassword,
        byte[] payload,
        string tamper = "",
        long? declaredBytes = null)
    {
        string directory = Path.Combine(Path.GetTempPath(), "heimdall-sudo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "sudo"), FakeSudoScript, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(directory, "run.sh"), RunnerScript, new UTF8Encoding(false));

            string command = PrivilegedFileCommands.BuildStagedWriteCommand(
                "out.bin",
                overwrite: true,
                passwordMode,
                declaredBytes ?? payload.Length);

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
            start.Environment["TMPDIR"] = "stage-tmp";
            start.Environment["FAKE_SUDO_MODE"] = mode;
            start.Environment["FAKE_SUDO_PASSWORD"] = expectedPassword ?? string.Empty;
            start.Environment["FAKE_SUDO_TAMPER"] = tamper;
            start.Environment["FAKE_STAT_FUNCTION"] = StatReportingGroupWritableDirectory;
            start.Environment["HEIMDALL_TEST_COMMAND"] = command;

            using Process process = Process.Start(start)!;
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();

            await using (Stream stdin = process.StandardInput.BaseStream)
            {
                ReadOnlyMemory<byte> prefix = passwordMode
                    ? PrivilegedFileTransfer.EncodePasswordLine(sentPassword!)
                    : ReadOnlyMemory<byte>.Empty;
                using var content = new MemoryStream(payload);
                try
                {
                    await PrivilegedFileTransfer.WriteInputAsync(stdin, content, prefix, CancellationToken.None);
                }
                catch (IOException)
                {
                    // The run ended before reading it all; the leftovers are asserted by the callers.
                }
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
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

            string rcPath = Path.Combine(directory, "rc.txt");
            if (!File.Exists(rcPath))
            {
                return new RunResult(false, -1, await stderr, [], false, [], string.Empty, [], [], []);
            }

            string outPath = Path.Combine(directory, "out.bin");
            string argsPath = Path.Combine(directory, "sudo-args.log");
            string readsPath = Path.Combine(directory, "sudo-reads.log");
            string stagingRoot = Path.Combine(directory, "stage-tmp");
            string callsPath = Path.Combine(directory, "sudo-calls.log");

            return new RunResult(
                true,
                int.Parse(File.ReadAllText(rcPath).Trim(), CultureInfo.InvariantCulture),
                File.ReadAllText(Path.Combine(directory, "stderr.txt")),
                File.Exists(outPath) ? File.ReadAllBytes(outPath) : [],
                File.Exists(outPath),
                File.ReadAllBytes(Path.Combine(directory, "rest.bin")),
                File.Exists(argsPath) ? File.ReadAllText(argsPath) : string.Empty,
                File.Exists(readsPath) ? File.ReadAllLines(readsPath) : [],
                Directory.Exists(stagingRoot)
                    ? Directory.GetFileSystemEntries(stagingRoot, ".heimdall-stage.*")
                    : [],
                File.Exists(callsPath) ? File.ReadAllLines(callsPath) : []);
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
            catch (UnauthorizedAccessException)
            {
                // Same.
            }
        }
    }
}

/// <summary>A fact that runs only where an unprivileged POSIX <c>sh</c> can be found.</summary>
public sealed class PosixShellFactAttribute : FactAttribute
{
    public PosixShellFactAttribute()
    {
        if (PosixShellLocator.Find() is null)
        {
            Skip = "No POSIX sh was found on PATH.";
        }
    }

    internal static string? FindShell() => PosixShellLocator.Find();
}

/// <summary>
/// A fact that runs only where a POSIX <c>sh</c> can be found and a probe script run by it
/// succeeds, so a capability the shell lacks is reported as skipped rather than as passed.
/// </summary>
public abstract class PosixProbeFactAttribute : FactAttribute
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> Probes = new();

    protected PosixProbeFactAttribute(string probeScript, string skipReason)
    {
        if (PosixShellLocator.Find() is null)
        {
            Skip = "No POSIX sh was found on PATH.";
        }
        else if (!Probes.GetOrAdd(probeScript, Probe))
        {
            Skip = skipReason;
        }
    }

    private static bool Probe(string script)
    {
        string directory = Path.Combine(Path.GetTempPath(), "heimdall-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "target"), "x");
            var start = new ProcessStartInfo(PosixShellLocator.Find()!)
            {
                WorkingDirectory = directory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            start.Environment["PATH"] = string.Join(
                Path.PathSeparator,
                Path.GetDirectoryName(start.FileName),
                Environment.GetEnvironmentVariable("PATH"));
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add(script);
            using Process process = Process.Start(start)!;
            if (!process.WaitForExit(TimeSpan.FromSeconds(30)))
            {
                process.Kill(entireProcessTree: true);
                return false;
            }

            return process.ExitCode == 0;
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
            catch (UnauthorizedAccessException)
            {
                // Same.
            }
        }
    }
}

/// <summary>Runs only where <c>ln -s</c> makes a real symbolic link (Git for Windows copies).</summary>
public sealed class PosixSymlinkFactAttribute()
    : PosixProbeFactAttribute("ln -s target link && [ -L link ]", "This sh does not create real symbolic links with ln -s.");

/// <summary>A theory that runs only where an unprivileged POSIX <c>sh</c> can be found.</summary>
public sealed class PosixShellTheoryAttribute : TheoryAttribute
{
    public PosixShellTheoryAttribute()
    {
        if (PosixShellLocator.Find() is null)
        {
            Skip = "No POSIX sh was found on PATH.";
        }
    }
}

internal static class PosixShellLocator
{
    private const string ShellName = "sh";
    private const string GitForWindowsShell = @"Git\usr\bin\sh.exe";

    internal static string? Find()
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
