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

using System.Security.Cryptography;
using System.Text;
using Renci.SshNet;

namespace Heimdall.Sftp;

/// <summary>
/// Builds privileged file commands that avoid attacker-writable staging paths.
/// </summary>
public static class PrivilegedFileCommands
{
    /// <summary>A creation-only privileged upload encountered an occupied destination.</summary>
    public const int DestinationExistsExitStatus = 78;
    /// <summary>Exit status used when a privileged edit exceeds its configured size limit.</summary>
    public const int FileTooLargeExitStatus = 75;

    /// <summary>
    /// Reported when the replaced file's metadata could not be carried over to the payload.
    /// </summary>
    /// <remarks>
    /// The rename never runs in that case, so the old target survives untouched. A file that loses
    /// its ACLs, capabilities or timestamps on replacement is a security regression, not a detail
    /// to swallow - so this refuses rather than publishing a file with weaker metadata.
    /// <para>
    /// 76, not 74: the no-follow read path already exits 74 for a non-regular source, and reusing
    /// it would make two unrelated refusals indistinguishable to the caller. Kept in step with the
    /// literal the script exits with; a test pins the two together.
    /// </para>
    /// </remarks>
    public const int MetadataPreservationFailedExitStatus = 76;

    /// <summary>
    /// Exit status of a privileged script that found one of the tools it needs missing, or a cp
    /// that is not GNU coreutils. Distinct from 73 (non-regular target), 74 (non-regular source)
    /// 75 (file too large) and 76 (metadata not preserved): the caller names the missing tool rather than reporting
    /// a failed transfer. Kept in step with the literal the probe exits with; a test pins the two.
    /// </summary>
    public const int ToolingUnavailableExitStatus = 77;

    /// <summary>
    /// Exit status of the user-side stage of a privileged write when the content could not be
    /// staged in the server's temporary directory (no space, no directory, no <c>mktemp</c>).
    /// Nothing privileged ran and the target is untouched. Kept in step with the literal the
    /// outer script exits with; a test pins the two.
    /// </summary>
    public const int StagingFailedExitStatus = 74;

    /// <summary>
    /// The first line the root-side installer writes to standard error. Its presence means sudo
    /// accepted the call and the installer started, so every status then belongs to the installer;
    /// its absence means the failure came from the user-side stage or from sudo itself.
    /// </summary>
    public const string InstallStartedMarker = "HEIMDALL-INSTALL-STARTED";

    /// <summary>The exit status a shell gives a command it could not find.</summary>
    public const int CommandNotFoundExitStatus = 127;

    private const string PasswordMode = "password";
    private const string NoPasswordMode = "none";
    private const string BangPlaceholder = "^";

    /// <summary>
    /// The scripts below are written for GNU coreutils (<c>stat -c</c>, <c>cp --attributes-only</c>,
    /// <c>sync -f</c>, <c>mv -T</c>). On a BSD or busybox server they used to fail on the first
    /// such flag with a message about that flag; the probe names the missing tool instead, and
    /// refuses before anything is staged.
    /// </summary>
    private const string ToolingProbe =
        "for t in stat cp sync mv ln mktemp chmod chown rm rmdir base64 cat; do "
        + "command -v \"$t\" >/dev/null 2>&1 || { printf '%s\\n' \"Refusing: $t is not available on the server.\" >&2; exit 77; }; done; "
        + "case \"$(stat --version 2>/dev/null)\" in *GNU*) ;; *) "
        + "printf '%s\\n' 'Refusing: GNU coreutils are required for a privileged transfer.' >&2; exit 77;; esac; ";

    private const string AtomicWriteScript =
        "set -eu; umask 077; target=$1; "
        + ToolingProbe
        + "case \"$target\" in /*) ;; *) target=$PWD/$target ;; esac; "
        + "case \"$target\" in */*) dir=${target%/*}; [ -n \"$dir\" ] || dir=/ ;; *) dir=. ;; esac; "
        + "work=$(mktemp -d -- \"$dir/.heimdall-write.XXXXXXXXXX\"); "
        + "chmod 700 -- \"$work\"; cd -- \"$work\"; "
        + "cleanup() { rm -f -- original payload; cd /; rmdir -- \"$work\" 2>/dev/null || :; }; "
        + "trap cleanup EXIT HUP INT TERM; "
        + "preserve_metadata=0; "
        + "if [ -e \"$target\" ] || [ -L \"$target\" ]; then "
        + "if [ \"$replace\" -eq 0 ]; then printf '%s\\n' 'Refusing existing destination.' >&2; exit 78; fi; "
        + "if ln -P -- \"$target\" original 2>/dev/null; then :; else ln -- \"$target\" original; fi; "
        + "if [ -L original ] || [ ! -f original ]; then "
        + "printf '%s\\n' 'Refusing non-regular or symbolic-link target.' >&2; exit 73; fi; "
        + "original_owner=$(stat -c %u:%g -- original); "
        + "original_mode=$(stat -c %a -- original); preserve_metadata=1; fi; "
        + "cat > payload; "
        + "if [ \"$preserve_metadata\" -eq 1 ]; then "
        + "payload_size=$(stat -c %s -- payload); "
        // Owner and mode first, then the extended attributes - never the other way round. On Linux
        // a chown clears the file capabilities stored in security.capability, so running it after
        // the attribute copy would silently strip exactly what that copy just restored.
        + "chown -- \"$original_owner\" payload; chmod -- \"$original_mode\" payload; "
        // The preserved set is listed explicitly rather than using --preserve=all, because GNU cp
        // does NOT fail when "all" cannot carry the extended attributes or the SELinux context: it
        // warns and exits zero. Naming xattr makes a failure a failure. "original" is a hard link
        // to the target, so it IS the target inode - these are the attributes the replaced file
        // really carried, and ACLs, capabilities and the SELinux context all travel as xattrs.
        + "if cp --attributes-only --preserve=mode,ownership,timestamps,xattr -- original payload; "
        + "then :; else "
        + "printf '%s\\n' 'Refusing: could not preserve the replaced file metadata.' >&2; "
        + "exit 76; fi; "
        // --attributes-only is documented not to touch the data, but this script cannot verify
        // which coreutils build is on the far side, and a truncating one would publish an empty
        // file. Measured across the copy, and refused if it moved.
        + "if [ \"$(stat -c %s -- payload)\" != \"$payload_size\" ]; then "
        + "printf '%s\\n' 'Refusing: the attribute copy altered the payload.' >&2; "
        + "exit 76; fi; fi; "
        // After the metadata, so what reaches the disk is the file as it will be published.
        + "sync -f payload; "
        + "rm -f -- original; "
        + "if [ -L \"$target\" ]; then "
        + "printf '%s\\n' 'Refusing symbolic-link target.' >&2; exit 73; fi; "
        + "if [ \"$replace\" -eq 1 ]; then mv -fT -- payload \"$target\"; "
        + "else if ln -T -- payload \"$target\"; then :; "
        + "else if [ -e \"$target\" ] || [ -L \"$target\" ]; then exit 78; else exit 1; fi; fi; "
        + "rm -f -- payload; fi; "
        + "cd /; rmdir -- \"$work\" 2>/dev/null || :; "
        + "trap - EXIT HUP INT TERM; sync -f \"$dir\"";

    private const string NoFollowReadPrefix =
        "set -eu; umask 077; target=$1; "
        + ToolingProbe
        + "case \"$target\" in /*) ;; *) target=$PWD/$target ;; esac; "
        + "case \"$target\" in */*) dir=${target%/*}; [ -n \"$dir\" ] || dir=/ ;; *) dir=. ;; esac; "
        + "work=$(mktemp -d -- \"$dir/.heimdall-read.XXXXXXXXXX\"); "
        + "chmod 700 -- \"$work\"; cd -- \"$work\"; "
        + "cleanup() { rm -f -- source; cd /; rmdir -- \"$work\" 2>/dev/null || :; }; "
        + "trap cleanup EXIT HUP INT TERM; "
        + "if ln -P -- \"$target\" source 2>/dev/null; then :; else ln -- \"$target\" source; fi; "
        + "if [ -L source ] || [ ! -f source ]; then "
        + "printf '%s\\n' 'Refusing non-regular or symbolic-link source.' >&2; exit 74; fi; "
        + "exec 3< source; rm -f -- source; ";

    private const string NoFollowReadSuffix =
        "base64 <&3; exec 3<&-; "
        + "cd /; rmdir -- \"$work\" 2>/dev/null || :; trap - EXIT HUP INT TERM";

    /// <summary>
    /// Builds a root-side streamed write that commits with a same-directory atomic rename.
    /// </summary>
    public static string BuildAtomicWriteBody(string targetRemotePath, bool overwrite = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetRemotePath);

        return BuildShellBody($"replace={(overwrite ? 1 : 0)}; " + AtomicWriteScript, targetRemotePath);
    }

    /// <summary>
    /// Builds a root-side read that hard-links without following the source and then reads
    /// through a held descriptor. A size limit, when supplied, is checked on that descriptor.
    /// </summary>
    public static string BuildNoFollowBase64ReadBody(
        string sourceRemotePath,
        long? maximumBytes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRemotePath);
        if (maximumBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }

        string sizeCheck = maximumBytes.HasValue
            ? "size=$(stat -Lc %s /proc/self/fd/3); "
                + $"if [ \"$size\" -gt {maximumBytes.Value} ]; then "
                + $"printf '%s\\n' \"$size\"; exit {FileTooLargeExitStatus}; fi; "
            : string.Empty;

        return BuildShellBody(
            NoFollowReadPrefix + sizeCheck + NoFollowReadSuffix,
            sourceRemotePath);
    }

    internal static string BuildPasswordSudoInvocation(string privilegedBody)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(privilegedBody);
        return $"sudo -S -p '' {privilegedBody}";
    }

    internal static string BuildNonInteractiveSudoInvocation(string privilegedBody)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(privilegedBody);
        return $"sudo -n {privilegedBody}";
    }

    /// <summary>
    /// The script the connecting user runs, as a constant. Its positional parameters are the
    /// installer script (<c>$1</c>), the mode (<c>$2</c>), the replace flag (<c>$3</c>), the
    /// target (<c>$4</c>) and the number of bytes the content must have (<c>$5</c>).
    /// </summary>
    /// <remarks>
    /// The content is first staged in a private directory of the connecting user, so no sudo ever
    /// shares standard input with the payload: sudo's input is the single password line and then
    /// its end, a wrong password meets the end after one attempt, and the password can neither be
    /// written to the file nor taken from it. The install is first tried without any password and
    /// with no input: when sudo needs none, the password never reaches sudo's input. Only when the
    /// installer did not start is the password offered, once. A staged file that is not exactly the
    /// expected size is refused before any sudo, so a truncated read is never installed. The script
    /// holds no <c>!</c> (history expansion in some login shells) and no newline. The staged
    /// directory is removed by this script's own trap, never by the root-side installer.
    /// </remarks>
    internal const string StagedWriteOuterScript =
        "umask 077; LC_ALL=C; export LC_ALL; "
        + "inner=$(printf '%s' \"$1\" | tr '^' '\\041') || exit 74; "
        + "mode=$2; replace=$3; target=$4; size=$5; p=; "
        + "if [ \"$mode\" = password ]; then IFS= read -r p; fi; "
        + "d=$(mktemp -d \"${TMPDIR:-/tmp}/.heimdall-stage.XXXXXXXXXX\") || exit 74; "
        + "trap 'rm -rf -- \"$d\"' EXIT; trap 'exit 129' HUP INT TERM; "
        + "cat > \"$d/f\" || exit 74; "
        + "[ $(wc -c < \"$d/f\") -eq \"$size\" ] || exit 74; "
        + "uid=$(id -u) || exit 74; "
        + "sudo -n sh -c \"$inner\" sh \"$replace\" \"$target\" \"$d/f\" \"$uid\" "
        + "</dev/null 2> \"$d/e\"; rc=$?; "
        + "case \"$(cat \"$d/e\")\" in *" + InstallStartedMarker + "*) "
        + "cat \"$d/e\" >&2; p=; unset p; exit $rc ;; esac; "
        + "if [ \"$mode\" = password ]; then "
        + "printf '%s\\n' \"$p\" | sudo -S -p '' sh -c \"$inner\" sh \"$replace\" \"$target\" \"$d/f\" \"$uid\"; rc=$?; "
        + "else cat \"$d/e\" >&2; fi; "
        + "p=; unset p; exit $rc";

    /// <summary>
    /// The script root runs: it announces itself, checks the staged file on the descriptor it
    /// opened, then runs the atomic writer unchanged with that descriptor as standard input.
    /// Positional parameters: replace flag, target, staged file, the caller's uid.
    /// </summary>
    /// <remarks>
    /// The checks use GNU <c>stat</c> and <c>/proc/self/fd</c>, the same platform the writer and the
    /// no-follow read already assume. A check that cannot be performed refuses with 73 rather than
    /// proceed.
    /// </remarks>
    internal const string StagedInstallScript =
        "printf '%s\\n' '" + InstallStartedMarker + "' >&2; "
        + "replace=$1; target=$2; staged=$3; uid=$4; "
        + ToolingProbe
        + "case \"$staged\" in */*) ;; *) "
        + "printf '%s\\n' 'Refusing: the staged file has no directory.' >&2; exit 73;; esac; "
        + "sd=${staged%/*}; "
        + "if [ -L \"$staged\" ] || [ -L \"$sd\" ]; then "
        + "printf '%s\\n' 'Refusing symbolic-link staged file.' >&2; exit 73; fi; "
        + "dinfo=$(stat -c '%u %a' -- \"$sd\" 2>/dev/null) || dinfo=; "
        + "set -- $dinfo; du=$1; dm=$2; "
        + "if [ -n \"$dm\" ] && [ \"$du\" = \"$uid\" ] && [ $((0$dm & 022)) -eq 0 ]; then :; else "
        + "printf '%s\\n' 'Refusing: the staging directory is not private to the caller.' >&2; exit 73; fi; "
        + "exec 3< \"$staged\"; "
        + "info=$(stat -Lc %u:%h /proc/self/fd/3 2>/dev/null) || info=; "
        + "if [ -f /proc/self/fd/3 ] && [ \"$info\" = \"$uid:1\" ]; then :; else "
        + "printf '%s\\n' 'Refusing: the staged file is not a regular single-link file of the caller, or could not be checked.' >&2; "
        + "exit 73; fi; "
        + "exec 0<&3 3<&-; set -- \"$target\"; "
        + AtomicWriteScript;

    /// <summary>
    /// Builds the one simple command that stages the content as the connecting user and installs it
    /// with sudo. Every variable value is a quoted positional parameter.
    /// </summary>
    internal static string BuildStagedWriteCommand(
        string targetRemotePath,
        bool overwrite,
        bool passwordMode,
        long expectedBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetRemotePath);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedBytes);

        // The installer holds "!" (tests and comparisons), which a history-expanding login shell
        // would act on even inside quotes. It travels as a placeholder the outer script restores.
        string installer = StagedInstallScript.Replace("!", BangPlaceholder, StringComparison.Ordinal);

        // A "!" in a file name is escaped the way every such shell reads it: outside the quotes.
        string target = PathEscaper.EscapeForShell(targetRemotePath)
            .Replace("!", "'\\!'", StringComparison.Ordinal);

        return string.Join(
            ' ',
            "sh",
            "-c",
            PathEscaper.EscapeForShell(StagedWriteOuterScript),
            "sh",
            PathEscaper.EscapeForShell(installer),
            PathEscaper.EscapeForShell(passwordMode ? PasswordMode : NoPasswordMode),
            PathEscaper.EscapeForShell(overwrite ? "1" : "0"),
            target,
            PathEscaper.EscapeForShell(expectedBytes.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    private static string BuildShellBody(string script, string remotePath)
    {
        string escapedScript = PathEscaper.EscapeForShell(script);
        string escapedPath = PathEscaper.EscapeForShell(remotePath);
        return $"sh -c {escapedScript} sh {escapedPath}";
    }
}

/// <summary>
/// Immutable result of a completed streamed privileged command.
/// </summary>
public readonly record struct PrivilegedCommandResult(
    int ExitStatus,
    string Result,
    string Error);

/// <summary>
/// Streams command input over an SSH exec channel without an unprivileged remote file.
/// </summary>
public static class PrivilegedFileTransfer
{
    /// <summary>
    /// The bound of a privileged exec command whose duration is not set by a payload.
    /// </summary>
    /// <remarks>
    /// SSH.NET leaves <c>SshCommand.CommandTimeout</c> infinite, so a server that accepts the exec
    /// channel and then never answers holds the operation open with nothing left to end it. Ten
    /// minutes, matching the server-side copy bound: long enough for a recursive privileged
    /// operation over a large tree, short enough that an unproductive channel gives up by itself.
    /// The SFTP pane's sudo control commands use this same value, so the two cannot drift apart.
    /// </remarks>
    public static readonly TimeSpan ControlCommandTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The slowest sustained rate a privileged write is still expected to complete at.
    /// </summary>
    /// <remarks>
    /// SSH.NET's command bound is wall-clock over the whole command, not an inactivity timer, so a
    /// fixed bound on a write would abort a large legitimate one. The write's bound grows by the
    /// time its payload takes at this rate: 32 KiB/s, well below any usable link, so it never
    /// aborts a transfer that is making progress, while a wedged server is still given up on.
    /// </remarks>
    internal const long MinimumTransferBytesPerSecond = 32 * 1024;

    /// <summary>
    /// Returns the bound for a privileged command carrying <paramref name="payloadBytes"/>, or the
    /// control bound when the payload is unknown.
    /// </summary>
    internal static TimeSpan TransferCommandTimeout(long? payloadBytes)
    {
        if (payloadBytes is not { } bytes)
        {
            return ControlCommandTimeout;
        }

        ArgumentOutOfRangeException.ThrowIfNegative(bytes, nameof(payloadBytes));
        return ControlCommandTimeout + TimeSpan.FromSeconds((double)bytes / MinimumTransferBytesPerSecond);
    }

    /// <summary>
    /// Connects a client for a privileged command with a cancellation that reaches the handshake.
    /// </summary>
    /// <remarks>
    /// The application's privileged SFTP connect used <c>Task.Run(() =&gt; client.Connect())</c>,
    /// whose token is checked once before the call and never again. The SSH assembly grants its
    /// cancellable connect to this one, not to the application, so the application reaches it here.
    /// </remarks>
    public static Task ConnectAsync(SshClient client, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        return Heimdall.Ssh.SshConnectionFactory.ConnectWithCancellationAsync(client, ct);
    }

    /// <summary>
    /// Executes a privileged command whose body does not consume standard input.
    /// </summary>
    public static async Task<PrivilegedCommandResult> ExecuteCommandAsync(
        SshClient client,
        string privilegedBody,
        string? sudoPassword,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(privilegedBody);

        if (string.IsNullOrEmpty(sudoPassword))
        {
            using var emptyInput = new MemoryStream();
            return await ExecuteWithInputAsync(
                    client,
                    PrivilegedFileCommands.BuildNonInteractiveSudoInvocation(privilegedBody),
                    emptyInput,
                    ReadOnlyMemory<byte>.Empty,
                    ControlCommandTimeout,
                    ct)
                .ConfigureAwait(false);
        }

        return await ExecuteWithPasswordAsync(
                client,
                PrivilegedFileCommands.BuildPasswordSudoInvocation(privilegedBody),
                sudoPassword,
                ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Writes <paramref name="content"/> to <paramref name="targetRemotePath"/> as root. The content
    /// is first staged in a private directory of the connecting user, then installed by one sudo
    /// call that shares no input with the payload, so a password can never reach the file and a
    /// payload line can never be read as a password.
    /// </summary>
    public static async Task<PrivilegedCommandResult> ExecuteAtomicWriteAsync(
        SshClient client,
        string targetRemotePath,
        bool overwrite,
        Stream content,
        string? sudoPassword,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetRemotePath);
        ArgumentNullException.ThrowIfNull(content);
        if (!content.CanRead)
        {
            throw new ArgumentException("The content stream must be readable.", nameof(content));
        }

        // The remote side refuses a staged file of any other size, so a read that stops early can
        // never be installed. That needs the length up front: a stream that cannot report it is
        // refused rather than buffered. Both callers pass a file stream.
        if (!content.CanSeek)
        {
            throw new ArgumentException("The content stream must report its length.", nameof(content));
        }

        long expectedBytes = Math.Max(0, content.Length - content.Position);

        // The payload sets how long a legitimate write takes, so it sets the bound too.
        TimeSpan commandTimeout = TransferCommandTimeout(expectedBytes);

        bool passwordMode = !string.IsNullOrEmpty(sudoPassword);
        string commandText = PrivilegedFileCommands.BuildStagedWriteCommand(
            targetRemotePath,
            overwrite,
            passwordMode,
            expectedBytes);
        if (!passwordMode)
        {
            return await ExecuteWithInputAsync(
                    client,
                    commandText,
                    content,
                    ReadOnlyMemory<byte>.Empty,
                    commandTimeout,
                    ct,
                    remoteMayExplainWriteFailure: true)
                .ConfigureAwait(false);
        }

        byte[] passwordBytes = EncodePasswordLine(sudoPassword!);
        try
        {
            return await ExecuteWithInputAsync(
                    client,
                    commandText,
                    content,
                    passwordBytes,
                    commandTimeout,
                    ct,
                    remoteMayExplainWriteFailure: true)
                .ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    /// <summary>
    /// Names what a failed privileged write failed at, from its exit status and standard error.
    /// </summary>
    public static PrivilegedWriteFailureKind ClassifyWrite(PrivilegedCommandResult result)
    {
        return ClassifyWrite(result.ExitStatus, result.Error);
    }

    internal static PrivilegedWriteFailureKind ClassifyWrite(int exitStatus, string? stderr)
    {
        if (exitStatus == 0)
        {
            return PrivilegedWriteFailureKind.None;
        }

        // The installer's own start line decides first: whatever it exits with is its own status.
        string error = stderr ?? string.Empty;
        if (error.Contains(PrivilegedFileCommands.InstallStartedMarker, StringComparison.Ordinal))
        {
            return PrivilegedWriteFailureKind.InstallFailed;
        }

        if (exitStatus == PrivilegedFileCommands.StagingFailedExitStatus)
        {
            return PrivilegedWriteFailureKind.StagingFailed;
        }

        if (exitStatus == PrivilegedFileCommands.CommandNotFoundExitStatus)
        {
            return PrivilegedWriteFailureKind.SudoMissing;
        }

        if (ContainsAny(error, NotPermittedMessages))
        {
            return PrivilegedWriteFailureKind.NotPermitted;
        }

        if (ContainsAny(error, PasswordRejectedMessages))
        {
            return PrivilegedWriteFailureKind.PasswordRejected;
        }

        if (ContainsAny(error, PasswordUnavailableMessages))
        {
            return PrivilegedWriteFailureKind.PasswordUnavailable;
        }

        return PrivilegedWriteFailureKind.SudoFailed;
    }

    /// <summary>
    /// Standard error without the installer's start line, fit to show or log.
    /// </summary>
    public static string CleanWriteError(string? stderr)
    {
        if (string.IsNullOrEmpty(stderr))
        {
            return string.Empty;
        }

        return stderr
            .Replace(PrivilegedFileCommands.InstallStartedMarker, string.Empty, StringComparison.Ordinal)
            .Trim();
    }

    /// <summary>
    /// Throws for a failure of the stages before the installer ran: staging, sudo's own refusal,
    /// a missing sudo. A failure of the installer itself, or success, returns so the caller applies
    /// the installer's own statuses.
    /// </summary>
    /// <exception cref="PrivilegedWriteFailedException">A named stage failure.</exception>
    /// <exception cref="InvalidOperationException">An unnamed sudo stage failure.</exception>
    public static void EnsureStageSucceeded(PrivilegedCommandResult result)
    {
        PrivilegedWriteFailureKind kind = ClassifyWrite(result);
        string error = CleanWriteError(result.Error);
        switch (kind)
        {
            case PrivilegedWriteFailureKind.None:
            case PrivilegedWriteFailureKind.InstallFailed:
                return;
            case PrivilegedWriteFailureKind.SudoFailed:
                throw new InvalidOperationException(
                    $"sudo atomic write failed (exit {result.ExitStatus}): {error}");
            default:
                throw new PrivilegedWriteFailedException(kind, error);
        }
    }

    private static readonly string[] PasswordRejectedMessages =
    [
        "incorrect password attempt",
        "sorry, try again",
        "no password was provided",
    ];

    private static readonly string[] PasswordUnavailableMessages =
    [
        "a terminal is required",
        "no tty present",
        "no askpass",
        "a password is required",
        "you must have a tty",
    ];

    private static readonly string[] NotPermittedMessages =
    [
        "not in the sudoers",
        "may not run sudo",
        "is not allowed to execute",
        "is not allowed to run sudo",
    ];

    private static bool ContainsAny(string text, string[] needles)
    {
        foreach (string needle in needles)
        {
            if (text.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<PrivilegedCommandResult> ExecuteWithPasswordAsync(
        SshClient client,
        string commandText,
        string password,
        CancellationToken ct)
    {
        byte[] passwordBytes = EncodePasswordLine(password);
        try
        {
            using var emptyInput = new MemoryStream();
            return await ExecuteWithInputAsync(
                    client,
                    commandText,
                    emptyInput,
                    passwordBytes,
                    ControlCommandTimeout,
                    ct)
                .ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    /// <summary>
    /// Encodes the single line the streaming sudo line reads before anything else: the password
    /// and a line feed. A password holding a line feed would end the line early and leave the rest
    /// at the head of the payload, so it is refused here rather than sent.
    /// </summary>
    internal static byte[] EncodePasswordLine(string sudoPassword)
    {
        ArgumentException.ThrowIfNullOrEmpty(sudoPassword);
        if (sudoPassword.Contains('\n', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The sudo password must be a single line.",
                nameof(sudoPassword));
        }

        return Encoding.UTF8.GetBytes(sudoPassword + "\n");
    }

    /// <summary>
    /// Writes the prefix, then exactly the payload, to the remote command's standard input.
    /// </summary>
    internal static async Task WriteInputAsync(
        Stream commandInput,
        Stream payload,
        ReadOnlyMemory<byte> prefix,
        CancellationToken ct)
    {
        if (!prefix.IsEmpty)
        {
            await commandInput.WriteAsync(prefix, ct).ConfigureAwait(false);
        }

        byte[] buffer = new byte[CopyBufferBytes];
        while (true)
        {
            int read;
            try
            {
                read = await payload.ReadAsync(buffer, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Told apart from a failed write: the remote side then refuses the short file, and
                // its status must not be allowed to hide that the local read is what failed.
                throw new LocalPayloadReadException(ex);
            }

            if (read == 0)
            {
                break;
            }

            await commandInput.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }

        await commandInput.FlushAsync(ct).ConfigureAwait(false);
    }

    private const int CopyBufferBytes = 81920;

    private sealed class LocalPayloadReadException(Exception inner) : IOException(inner.Message, inner);

    private static void Rethrow(Exception failure)
    {
        Exception original = failure is LocalPayloadReadException { InnerException: { } inner } ? inner : failure;
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(original).Throw();
    }

    /// <summary>
    /// Writes the input and returns the failure instead of throwing it. Cancellation still throws.
    /// </summary>
    internal static async Task<Exception?> TryWriteInputAsync(
        Stream commandInput,
        Stream payload,
        ReadOnlyMemory<byte> prefix,
        CancellationToken ct)
    {
        try
        {
            await WriteInputAsync(commandInput, payload, prefix, ct).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ex;
        }
    }

    /// <summary>
    /// Raises a deferred local write failure unless the remote command ended in one of the stages
    /// before the installer (staging, sudo), whose status and message explain it better than a
    /// closed channel does. After the installer started, or on success, the write failure stands.
    /// </summary>
    internal static void RethrowUnlessStageFailed(Exception? writeFailure, int exitStatus, string? stderr)
    {
        if (writeFailure is null)
        {
            return;
        }

        // A failed local read is the cause whatever the remote side then says about the short
        // file, and no exit status read means nothing explains the write failure.
        if (writeFailure is LocalPayloadReadException || exitStatus < 0)
        {
            Rethrow(writeFailure);
        }

        PrivilegedWriteFailureKind kind = ClassifyWrite(exitStatus, stderr);
        if (kind is not (PrivilegedWriteFailureKind.None or PrivilegedWriteFailureKind.InstallFailed))
        {
            return;
        }

        Rethrow(writeFailure);
    }

    private static async Task<PrivilegedCommandResult> ExecuteWithInputAsync(
        SshClient client,
        string commandText,
        Stream input,
        ReadOnlyMemory<byte> prefix,
        TimeSpan commandTimeout,
        CancellationToken ct,
        bool remoteMayExplainWriteFailure = false)
    {
        using SshCommand command = client.CreateCommand(commandText);
        command.CommandTimeout = commandTimeout;
        Task? executeTask = null;
        try
        {
            executeTask = command.ExecuteAsync(ct);
            // A refused password makes the remote side exit before it reads the payload, so a large
            // write can fail on a closed channel first. The failure waits for the exit status.
            Exception? writeFailure = null;
            try
            {
                using Stream commandInput = command.CreateInputStream();
                writeFailure = await TryWriteInputAsync(commandInput, input, prefix, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                writeFailure ??= ex;
            }

            await executeTask.ConfigureAwait(false);
            if (remoteMayExplainWriteFailure)
            {
                RethrowUnlessStageFailed(writeFailure, command.ExitStatus ?? -1, command.Error);
            }
            else if (writeFailure is not null)
            {
                Rethrow(writeFailure);
            }

            return new PrivilegedCommandResult(
                command.ExitStatus ?? -1,
                command.Result ?? string.Empty,
                command.Error ?? string.Empty);
        }
        catch
        {
            if (executeTask is not null)
            {
                try
                {
                    await executeTask.ConfigureAwait(false);
                }
                catch
                {
                    // Preserve the original stream or cancellation exception.
                }
            }

            throw;
        }
    }
}

/// <summary>
/// What a failed privileged write failed at.
/// </summary>
public enum PrivilegedWriteFailureKind
{
    /// <summary>The write succeeded.</summary>
    None,

    /// <summary>The content could not be staged in the server's temporary directory.</summary>
    StagingFailed,

    /// <summary>sudo refused the password.</summary>
    PasswordRejected,

    /// <summary>sudo needs a password or a terminal it was not given.</summary>
    PasswordUnavailable,

    /// <summary>The account is not allowed to use sudo.</summary>
    NotPermitted,

    /// <summary>The server has no sudo to run.</summary>
    SudoMissing,

    /// <summary>The stage failed in a way none of the named kinds recognise.</summary>
    SudoFailed,

    /// <summary>The installer started; its own exit statuses apply.</summary>
    InstallFailed,
}

/// <summary>
/// A privileged write failed before the installer ran, at a stage the caller can name.
/// </summary>
public sealed class PrivilegedWriteFailedException : InvalidOperationException
{
    public PrivilegedWriteFailedException(PrivilegedWriteFailureKind kind, string remoteError)
        : base(string.IsNullOrWhiteSpace(remoteError)
            ? $"The privileged write failed: {kind}."
            : remoteError)
    {
        Kind = kind;
        RemoteError = remoteError ?? string.Empty;
    }

    /// <summary>The stage that failed.</summary>
    public PrivilegedWriteFailureKind Kind { get; }

    /// <summary>What the remote side wrote on standard error, without the installer's start line.</summary>
    public string RemoteError { get; }
}
