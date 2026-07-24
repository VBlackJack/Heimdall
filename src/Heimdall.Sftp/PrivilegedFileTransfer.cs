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
    /// <summary>Exit status used when a privileged edit exceeds its configured size limit.</summary>
    public const int FileTooLargeExitStatus = 75;

    private const string AtomicWriteScript =
        "set -eu; umask 077; target=$1; "
        + "case \"$target\" in /*) ;; *) target=$PWD/$target ;; esac; "
        + "case \"$target\" in */*) dir=${target%/*}; [ -n \"$dir\" ] || dir=/ ;; *) dir=. ;; esac; "
        + "work=$(mktemp -d -- \"$dir/.heimdall-write.XXXXXXXXXX\"); "
        + "chmod 700 -- \"$work\"; cd -- \"$work\"; "
        + "cleanup() { rm -f -- original payload; cd /; rmdir -- \"$work\" 2>/dev/null || :; }; "
        + "trap cleanup EXIT HUP INT TERM; "
        + "preserve_metadata=0; "
        + "if [ -e \"$target\" ] || [ -L \"$target\" ]; then "
        + "if ln -P -- \"$target\" original 2>/dev/null; then :; else ln -- \"$target\" original; fi; "
        + "if [ -L original ] || [ ! -f original ]; then "
        + "printf '%s\\n' 'Refusing non-regular or symbolic-link target.' >&2; exit 73; fi; "
        + "original_owner=$(stat -c %u:%g -- original); "
        + "original_mode=$(stat -c %a -- original); preserve_metadata=1; fi; "
        + "cat > payload; sync -f payload; "
        + "if [ \"$preserve_metadata\" -eq 1 ]; then "
        + "chown -- \"$original_owner\" payload; chmod -- \"$original_mode\" payload; fi; "
        + "rm -f -- original; "
        + "if [ -L \"$target\" ]; then "
        + "printf '%s\\n' 'Refusing symbolic-link target.' >&2; exit 73; fi; "
        + "mv -fT -- payload \"$target\"; "
        + "cd /; rmdir -- \"$work\" 2>/dev/null || :; "
        + "trap - EXIT HUP INT TERM; sync -f \"$dir\"";

    private const string NoFollowReadPrefix =
        "set -eu; umask 077; target=$1; "
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
    public static string BuildAtomicWriteBody(string targetRemotePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetRemotePath);

        return BuildShellBody(AtomicWriteScript, targetRemotePath);
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

    internal static string BuildPasswordStreamingSudoInvocation(string privilegedBody)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(privilegedBody);
        return "if sudo -n -v 2>/dev/null; then "
            + $"IFS= read -r _heimdall_password; exec sudo -n {privilegedBody}; "
            + $"else sudo -k; exec sudo -S -p '' {privilegedBody}; fi";
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
    /// Streams file content to a privileged atomic writer. A password prefix is consumed by an
    /// authentication prelude before the writer starts, so only payload bytes reach the writer.
    /// </summary>
    public static async Task<PrivilegedCommandResult> ExecuteAtomicWriteAsync(
        SshClient client,
        string privilegedBody,
        Stream content,
        string? sudoPassword,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(privilegedBody);
        ArgumentNullException.ThrowIfNull(content);
        if (!content.CanRead)
        {
            throw new ArgumentException("The content stream must be readable.", nameof(content));
        }

        string commandText;
        if (string.IsNullOrEmpty(sudoPassword))
        {
            commandText = PrivilegedFileCommands.BuildNonInteractiveSudoInvocation(privilegedBody);
            return await ExecuteWithInputAsync(
                    client,
                    commandText,
                    content,
                    ReadOnlyMemory<byte>.Empty,
                    ct)
                .ConfigureAwait(false);
        }

        commandText = PrivilegedFileCommands.BuildPasswordStreamingSudoInvocation(privilegedBody);
        byte[] passwordBytes = Encoding.UTF8.GetBytes(sudoPassword + "\n");
        try
        {
            return await ExecuteWithInputAsync(client, commandText, content, passwordBytes, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    private static async Task<PrivilegedCommandResult> ExecuteWithPasswordAsync(
        SshClient client,
        string commandText,
        string password,
        CancellationToken ct)
    {
        byte[] passwordBytes = Encoding.UTF8.GetBytes(password + "\n");
        try
        {
            using var emptyInput = new MemoryStream();
            return await ExecuteWithInputAsync(client, commandText, emptyInput, passwordBytes, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    private static async Task<PrivilegedCommandResult> ExecuteWithInputAsync(
        SshClient client,
        string commandText,
        Stream input,
        ReadOnlyMemory<byte> prefix,
        CancellationToken ct)
    {
        using SshCommand command = client.CreateCommand(commandText);
        Task? executeTask = null;
        try
        {
            executeTask = command.ExecuteAsync(ct);
            using (Stream commandInput = command.CreateInputStream())
            {
                if (!prefix.IsEmpty)
                {
                    await commandInput.WriteAsync(prefix, ct).ConfigureAwait(false);
                }

                await input.CopyToAsync(commandInput, ct).ConfigureAwait(false);
                await commandInput.FlushAsync(ct).ConfigureAwait(false);
            }

            await executeTask.ConfigureAwait(false);
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
