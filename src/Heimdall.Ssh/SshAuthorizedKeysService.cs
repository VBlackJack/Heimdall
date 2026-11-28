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
using Heimdall.Core.Ssh;
using Renci.SshNet;

namespace Heimdall.Ssh;

/// <summary>Structured error categories returned by <see cref="SshAuthorizedKeysService"/>.</summary>
public enum SshAuthorizedKeysErrorCode
{
    None,
    InvalidPublicKey,
    InvalidOptions,
    SymlinkRefused,
    LastKeyProtected,
    RemoteCommandFailed,
    ConnectionFailed
}

/// <summary>One parsed key line from a remote <c>~/.ssh/authorized_keys</c> file.</summary>
public sealed record AuthorizedKeyEntry(
    int LineNumber,
    string Options,
    string KeyType,
    string KeyBase64,
    string Fingerprint,
    string Comment,
    string RawLine);

/// <summary>Outcome of listing keys from a remote <c>~/.ssh/authorized_keys</c>.</summary>
public sealed record SshAuthorizedKeysListResult(
    bool Success,
    IReadOnlyList<AuthorizedKeyEntry> Entries,
    string? ErrorMessage = null,
    SshAuthorizedKeysErrorCode ErrorCode = SshAuthorizedKeysErrorCode.None);

/// <summary>Outcome of adding or removing a key in a remote <c>~/.ssh/authorized_keys</c>.</summary>
public sealed record SshAuthorizedKeysMutationResult(
    bool Success,
    bool AlreadyPresent = false,
    string? ErrorMessage = null,
    SshAuthorizedKeysErrorCode ErrorCode = SshAuthorizedKeysErrorCode.None,
    int? ExitStatus = null);

/// <summary>
/// Pure parser and safe remote CRUD manager for OpenSSH <c>~/.ssh/authorized_keys</c>.
/// Enforces Base64 transport (no shell metacharacter interpolation), symlink refusal (<c>-L</c>),
/// atomic <c>mktemp</c> + <c>mv -f</c> replacement, and a non-empty lockout guard.
/// </summary>
public static class SshAuthorizedKeysService
{
    internal const string SymlinkRefusedMarker = "HEIMDALL_AK_SYMLINK";
    internal const string LastKeyRefusedMarker = "HEIMDALL_AK_LAST_KEY";
    internal const string KeyAddedMarker = "HEIMDALL_AK_ADDED";
    internal const string KeyExistsMarker = "HEIMDALL_AK_EXISTS";
    internal const string KeyRemovedMarker = "HEIMDALL_AK_REMOVED";
    internal const int MaxOptionsLength = 512;
    private const int MaxRemoteReadBytes = 512 * 1024;
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

    private static readonly HashSet<string> SupportedKeyTypes = new(StringComparer.Ordinal)
    {
        "ssh-ed25519",
        "ssh-rsa",
        "ssh-dss",
        "ecdsa-sha2-nistp256",
        "ecdsa-sha2-nistp384",
        "ecdsa-sha2-nistp521",
        "sk-ssh-ed25519@openssh.com",
        "sk-ecdsa-sha2-nistp256@openssh.com"
    };

    /// <summary>
    /// Parses OpenSSH <c>authorized_keys</c> text into structured entries, preserving optional
    /// leading restrictions (including quoted values such as <c>from="10.0.0.0/8"</c> and
    /// <c>command="/usr/bin/rrsync /srv"</c>) and computing SHA-256 fingerprints.
    /// </summary>
    public static IReadOnlyList<AuthorizedKeyEntry> ParseLines(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return [];
        }

        var entries = new List<AuthorizedKeyEntry>();
        string[] lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string raw = lines[i].Trim();
            if (raw.Length == 0 || raw.StartsWith('#'))
            {
                continue;
            }

            if (TryParseSingleLine(raw, i + 1, out var entry) && entry is not null)
            {
                entries.Add(entry);
            }
        }

        return entries;
    }

    /// <summary>Validates optional OpenSSH <c>authorized_keys</c> key options.</summary>
    public static bool TryValidateOptions(string? options, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(options))
        {
            return true;
        }

        string trimmed = options.Trim();
        if (trimmed.Length > MaxOptionsLength)
        {
            return false;
        }

        bool inQuotes = false;
        bool escaped = false;
        foreach (char c in trimmed)
        {
            if (char.IsControl(c))
            {
                return false;
            }

            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (c == '\\' && inQuotes)
            {
                escaped = true;
                continue;
            }

            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                // Unquoted whitespace separates options from the key type in authorized_keys.
                return false;
            }
        }

        if (inQuotes || escaped)
        {
            return false;
        }

        normalized = trimmed;
        return true;
    }

    internal static string BuildListCommand() =>
        "ak=\"$HOME/.ssh/authorized_keys\"; " +
        $"if [ -L \"$ak\" ]; then echo {SymlinkRefusedMarker}; exit 1; fi; " +
        $"if [ -f \"$ak\" ]; then head -c {MaxRemoteReadBytes} \"$ak\"; fi";

    internal static string BuildAddKeyCommand(string validatedPublicKeyLine, string? validatedOptions)
    {
        string fullLine = string.IsNullOrWhiteSpace(validatedOptions)
            ? validatedPublicKeyLine
            : $"{validatedOptions} {validatedPublicKeyLine}";
        string[] parts = validatedPublicKeyLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        string keyIdentity = parts.Length >= 2 ? $"{parts[0]} {parts[1]}" : validatedPublicKeyLine;

        string payloadB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(fullLine));
        string identityB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(keyIdentity));

        return
            $"key=$(printf '%s' '{payloadB64}' | base64 -d) && " +
            $"key_id=$(printf '%s' '{identityB64}' | base64 -d) && " +
            "mkdir -p \"$HOME/.ssh\" && chmod 700 \"$HOME/.ssh\" && " +
            "ak=\"$HOME/.ssh/authorized_keys\" && " +
            $"if [ -L \"$ak\" ]; then echo {SymlinkRefusedMarker}; exit 1; fi && " +
            "touch \"$ak\" && chmod 600 \"$ak\" && " +
            $"((grep -Ev '^[[:space:]]*(#|$)' \"$ak\" | grep -Fq -- \"$key_id\") && echo {KeyExistsMarker} || " +
            $"((! [ -s \"$ak\" ] || [ -z \"$(tail -c 1 \"$ak\")\" ] || printf '\\n' >> \"$ak\") && " +
            $"printf '%s\\n' \"$key\" >> \"$ak\" && echo {KeyAddedMarker}))";
    }

    internal static string BuildRemoveKeyCommand(
        string keyType,
        string keyBase64,
        bool allowRemoveLastKey,
        string? exactRawLine = null)
    {
        if (string.IsNullOrWhiteSpace(keyType)
            || !SupportedKeyTypes.Contains(keyType)
            || string.IsNullOrWhiteSpace(keyBase64))
        {
            throw new ArgumentException("Valid key type and base64 blob are required.");
        }

        _ = Convert.FromBase64String(keyBase64);
        string keyIdentity = $"{keyType} {keyBase64}";
        string identityB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(keyIdentity));

        string emptyCheck = allowRemoveLastKey
            ? "true"
            : "grep -Eq '^[[:space:]]*[^[:space:]#]' \"$tmp\"";

        string trimmedExact = exactRawLine?.Trim() ?? string.Empty;
        bool useExactLine = !string.IsNullOrEmpty(trimmedExact)
            && !trimmedExact.Contains('\n')
            && !trimmedExact.Contains('\r');

        string filterAssign = useExactLine
            ? $"exact_line=$(printf '%s' '{Convert.ToBase64String(Encoding.UTF8.GetBytes(trimmedExact))}' | base64 -d) && "
            : $"key_id=$(printf '%s' '{identityB64}' | base64 -d) && ";
        string filterCmd = useExactLine
            ? "(grep -Fvx -- \"$exact_line\" \"$ak\" > \"$tmp\"; rc=$?; [ \"$rc\" -le 1 ])"
            : "(grep -Fv -- \"$key_id\" \"$ak\" > \"$tmp\"; rc=$?; [ \"$rc\" -le 1 ])";

        return
            filterAssign +
            "ak=\"$HOME/.ssh/authorized_keys\" && " +
            $"if [ -L \"$ak\" ]; then echo {SymlinkRefusedMarker}; exit 1; fi && " +
            "[ -f \"$ak\" ] && " +
            "tmp=$(mktemp \"$HOME/.ssh/.authorized_keys.heimdall.XXXXXX\") && " +
            "if chmod 600 \"$tmp\" && " +
            $"{filterCmd}; then " +
            $"if {emptyCheck}; then " +
            $"(mv -f \"$tmp\" \"$ak\" && chmod 600 \"$ak\" && echo {KeyRemovedMarker}) || {{ rm -f \"$tmp\"; exit 1; }}; " +
            $"else rm -f \"$tmp\"; echo {LastKeyRefusedMarker}; exit 1; fi; " +
            "else rm -f \"$tmp\"; exit 1; fi";
    }

    /// <summary>Connects and reads the remote user's <c>~/.ssh/authorized_keys</c>.</summary>
    public static async Task<SshAuthorizedKeysListResult> ListAsync(
        SshConnectionParams connParams,
        HostKeyStore hostKeyStore,
        IHostKeyVerifier verifier,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connParams);
        ArgumentNullException.ThrowIfNull(hostKeyStore);
        ArgumentNullException.ThrowIfNull(verifier);

        try
        {
            var (exitStatus, output, error) = await ExecuteRemoteCommandAsync(
                connParams,
                BuildListCommand(),
                hostKeyStore,
                verifier,
                ct).ConfigureAwait(false);

            if (output.Contains(SymlinkRefusedMarker, StringComparison.Ordinal))
            {
                return new SshAuthorizedKeysListResult(
                    false,
                    [],
                    "Remote authorized_keys is a symbolic link.",
                    SshAuthorizedKeysErrorCode.SymlinkRefused);
            }

            if (exitStatus != 0)
            {
                string err = string.IsNullOrWhiteSpace(error)
                    ? $"Remote command exited with status {exitStatus}."
                    : error.Trim();
                return new SshAuthorizedKeysListResult(
                    false,
                    [],
                    err,
                    SshAuthorizedKeysErrorCode.RemoteCommandFailed);
            }

            return new SshAuthorizedKeysListResult(true, ParseLines(output));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new SshAuthorizedKeysListResult(false, [], ex.Message, SshAuthorizedKeysErrorCode.ConnectionFailed);
        }
    }

    /// <summary>Validates and appends a public key (with optional restrictions) to remote <c>~/.ssh/authorized_keys</c>.</summary>
    public static async Task<SshAuthorizedKeysMutationResult> AddKeyAsync(
        SshConnectionParams connParams,
        string publicKeyLine,
        string? options,
        HostKeyStore hostKeyStore,
        IHostKeyVerifier verifier,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connParams);
        ArgumentNullException.ThrowIfNull(hostKeyStore);
        ArgumentNullException.ThrowIfNull(verifier);

        if (!SshPublicKeyDeployer.TryValidatePublicKeyLine(publicKeyLine, out string normalizedKey))
        {
            return new SshAuthorizedKeysMutationResult(
                false,
                ErrorMessage: "Invalid OpenSSH public key line.",
                ErrorCode: SshAuthorizedKeysErrorCode.InvalidPublicKey);
        }

        if (!TryValidateOptions(options, out string normalizedOptions))
        {
            return new SshAuthorizedKeysMutationResult(
                false,
                ErrorMessage: "Invalid authorized_keys options.",
                ErrorCode: SshAuthorizedKeysErrorCode.InvalidOptions);
        }

        try
        {
            var (exitStatus, output, error) = await ExecuteRemoteCommandAsync(
                connParams,
                BuildAddKeyCommand(normalizedKey, normalizedOptions),
                hostKeyStore,
                verifier,
                ct).ConfigureAwait(false);

            if (output.Contains(SymlinkRefusedMarker, StringComparison.Ordinal))
            {
                return new SshAuthorizedKeysMutationResult(
                    false,
                    ErrorMessage: "Remote authorized_keys is a symbolic link.",
                    ErrorCode: SshAuthorizedKeysErrorCode.SymlinkRefused);
            }

            if (exitStatus == 0 && output.Contains(KeyExistsMarker, StringComparison.Ordinal))
            {
                return new SshAuthorizedKeysMutationResult(true, AlreadyPresent: true);
            }

            if (exitStatus == 0 && output.Contains(KeyAddedMarker, StringComparison.Ordinal))
            {
                return new SshAuthorizedKeysMutationResult(true, AlreadyPresent: false);
            }

            string err = string.IsNullOrWhiteSpace(error)
                ? $"Remote command exited with status {exitStatus}."
                : error.Trim();
            return new SshAuthorizedKeysMutationResult(
                false,
                ErrorMessage: err,
                ErrorCode: SshAuthorizedKeysErrorCode.RemoteCommandFailed,
                ExitStatus: exitStatus);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new SshAuthorizedKeysMutationResult(false, ErrorMessage: ex.Message, ErrorCode: SshAuthorizedKeysErrorCode.ConnectionFailed);
        }
    }

    /// <summary>Atomically removes a matching key from remote <c>~/.ssh/authorized_keys</c>.</summary>
    public static async Task<SshAuthorizedKeysMutationResult> RemoveKeyAsync(
        SshConnectionParams connParams,
        string keyType,
        string keyBase64,
        bool allowRemoveLastKey,
        HostKeyStore hostKeyStore,
        IHostKeyVerifier verifier,
        CancellationToken ct = default,
        string? exactRawLine = null)
    {
        ArgumentNullException.ThrowIfNull(connParams);
        ArgumentNullException.ThrowIfNull(hostKeyStore);
        ArgumentNullException.ThrowIfNull(verifier);

        string commandText;
        try
        {
            commandText = BuildRemoveKeyCommand(keyType, keyBase64, allowRemoveLastKey, exactRawLine);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            return new SshAuthorizedKeysMutationResult(
                false,
                ErrorMessage: ex.Message,
                ErrorCode: SshAuthorizedKeysErrorCode.InvalidPublicKey);
        }

        try
        {
            var (exitStatus, output, error) = await ExecuteRemoteCommandAsync(
                connParams,
                commandText,
                hostKeyStore,
                verifier,
                ct).ConfigureAwait(false);

            if (output.Contains(SymlinkRefusedMarker, StringComparison.Ordinal))
            {
                return new SshAuthorizedKeysMutationResult(
                    false,
                    ErrorMessage: "Remote authorized_keys is a symbolic link.",
                    ErrorCode: SshAuthorizedKeysErrorCode.SymlinkRefused);
            }

            if (output.Contains(LastKeyRefusedMarker, StringComparison.Ordinal))
            {
                return new SshAuthorizedKeysMutationResult(
                    false,
                    ErrorMessage: "Cannot remove the last remaining key without confirmation.",
                    ErrorCode: SshAuthorizedKeysErrorCode.LastKeyProtected);
            }

            if (exitStatus == 0 && output.Contains(KeyRemovedMarker, StringComparison.Ordinal))
            {
                return new SshAuthorizedKeysMutationResult(true);
            }

            string err = string.IsNullOrWhiteSpace(error)
                ? $"Remote command exited with status {exitStatus}."
                : error.Trim();
            return new SshAuthorizedKeysMutationResult(
                false,
                ErrorMessage: err,
                ErrorCode: SshAuthorizedKeysErrorCode.RemoteCommandFailed,
                ExitStatus: exitStatus);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new SshAuthorizedKeysMutationResult(false, ErrorMessage: ex.Message, ErrorCode: SshAuthorizedKeysErrorCode.ConnectionFailed);
        }
    }

    private static bool TryParseSingleLine(string line, int lineNumber, out AuthorizedKeyEntry? entry)
    {
        entry = null;
        int pos = 0;
        string options = string.Empty;

        string? firstToken = ReadToken(line, ref pos);
        if (firstToken is null)
        {
            return false;
        }

        string keyType;
        if (SupportedKeyTypes.Contains(firstToken))
        {
            keyType = firstToken;
        }
        else
        {
            // Leading options field (may include quoted strings with spaces).
            options = firstToken;
            string? nextToken = ReadToken(line, ref pos);
            if (nextToken is null || !SupportedKeyTypes.Contains(nextToken))
            {
                return false;
            }

            keyType = nextToken;
        }

        string? keyBase64 = ReadToken(line, ref pos);
        if (string.IsNullOrWhiteSpace(keyBase64))
        {
            return false;
        }

        byte[] blob;
        try
        {
            blob = Convert.FromBase64String(keyBase64);
            if (blob.Length < 16)
            {
                return false;
            }
        }
        catch (FormatException)
        {
            return false;
        }

        string fingerprint = "SHA256:" + Convert.ToBase64String(SHA256.HashData(blob)).TrimEnd('=');
        string comment = pos < line.Length ? line[pos..].Trim() : string.Empty;

        entry = new AuthorizedKeyEntry(lineNumber, options, keyType, keyBase64, fingerprint, comment, line);
        return true;
    }

    private static string? ReadToken(string line, ref int pos)
    {
        while (pos < line.Length && char.IsWhiteSpace(line[pos]))
        {
            pos++;
        }

        if (pos >= line.Length)
        {
            return null;
        }

        int start = pos;
        bool inQuotes = false;
        bool escaped = false;
        while (pos < line.Length)
        {
            char c = line[pos];
            if (escaped)
            {
                escaped = false;
                pos++;
                continue;
            }

            if (c == '\\' && inQuotes)
            {
                escaped = true;
                pos++;
                continue;
            }

            if (c == '"')
            {
                inQuotes = !inQuotes;
                pos++;
                continue;
            }

            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                break;
            }

            pos++;
        }

        return line[start..pos];
    }

    private static async Task<(int? ExitStatus, string Output, string Error)> ExecuteRemoteCommandAsync(
        SshConnectionParams connParams,
        string commandText,
        HostKeyStore hostKeyStore,
        IHostKeyVerifier verifier,
        CancellationToken ct)
    {
        using var client = await OpenConnectedClientAsync(connParams, hostKeyStore, verifier, ct).ConfigureAwait(false);
        return await RunCommandOnClientAsync(client, commandText, ct).ConfigureAwait(false);
    }

    private static async Task<SshClient> OpenConnectedClientAsync(
        SshConnectionParams connParams,
        HostKeyStore hostKeyStore,
        IHostKeyVerifier verifier,
        CancellationToken ct)
    {
        var pinned = await SshConnectionFactory.ResolveHostKeyAsync(
                connParams,
                hostKeyStore,
                verifier,
                ct)
            .ConfigureAwait(false);

        var client = SshConnectionFactory.CreateSshClient(connParams);
        try
        {
            SshConnectionFactory.AttachPinnedHostKeyVerification(client, connParams, pinned);

            await SshConnectionFactory.ConnectWithTransportBoundAsync(
                    client,
                    connParams,
                    token => SshConnectionFactory.ConnectWithCancellationAsync(client, token),
                    ct)
                .ConfigureAwait(false);

            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static async Task<(int? ExitStatus, string Output, string Error)> RunCommandOnClientAsync(
        SshClient client,
        string commandText,
        CancellationToken ct)
    {
        using var cmd = client.CreateCommand(commandText);
        cmd.CommandTimeout = CommandTimeout;
        var ar = cmd.BeginExecute();
        using var reg = ct.Register(static state =>
        {
            try { ((SshCommand)state!).CancelAsync(); }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }, cmd);
        string output = await Task<string>.Factory.FromAsync(ar, cmd.EndExecute).ConfigureAwait(false);
        return (cmd.ExitStatus, output ?? string.Empty, cmd.Error ?? string.Empty);
    }

    private static SshAuthorizedKeysListResult ParseListResponse(int? exitStatus, string output, string error)
    {
        if (output.Contains(SymlinkRefusedMarker, StringComparison.Ordinal))
        {
            return new SshAuthorizedKeysListResult(
                false,
                [],
                "Remote authorized_keys is a symbolic link.",
                SshAuthorizedKeysErrorCode.SymlinkRefused);
        }

        if (exitStatus != 0)
        {
            string err = string.IsNullOrWhiteSpace(error)
                ? $"Remote command exited with status {exitStatus}."
                : error.Trim();
            return new SshAuthorizedKeysListResult(
                false,
                [],
                err,
                SshAuthorizedKeysErrorCode.RemoteCommandFailed);
        }

        return new SshAuthorizedKeysListResult(true, ParseLines(output));
    }

    private static SshAuthorizedKeysMutationResult ParseAddResponse(int? exitStatus, string output, string error)
    {
        if (output.Contains(SymlinkRefusedMarker, StringComparison.Ordinal))
        {
            return new SshAuthorizedKeysMutationResult(
                false,
                ErrorMessage: "Remote authorized_keys is a symbolic link.",
                ErrorCode: SshAuthorizedKeysErrorCode.SymlinkRefused);
        }

        if (exitStatus == 0 && output.Contains(KeyExistsMarker, StringComparison.Ordinal))
        {
            return new SshAuthorizedKeysMutationResult(true, AlreadyPresent: true);
        }

        if (exitStatus == 0 && output.Contains(KeyAddedMarker, StringComparison.Ordinal))
        {
            return new SshAuthorizedKeysMutationResult(true, AlreadyPresent: false);
        }

        string err = string.IsNullOrWhiteSpace(error)
            ? $"Remote command exited with status {exitStatus}."
            : error.Trim();
        return new SshAuthorizedKeysMutationResult(
            false,
            ErrorMessage: err,
            ErrorCode: SshAuthorizedKeysErrorCode.RemoteCommandFailed,
            ExitStatus: exitStatus);
    }

    private static SshAuthorizedKeysMutationResult ParseRemoveResponse(int? exitStatus, string output, string error)
    {
        if (output.Contains(SymlinkRefusedMarker, StringComparison.Ordinal))
        {
            return new SshAuthorizedKeysMutationResult(
                false,
                ErrorMessage: "Remote authorized_keys is a symbolic link.",
                ErrorCode: SshAuthorizedKeysErrorCode.SymlinkRefused);
        }

        if (output.Contains(LastKeyRefusedMarker, StringComparison.Ordinal))
        {
            return new SshAuthorizedKeysMutationResult(
                false,
                ErrorMessage: "Cannot remove the last remaining key without confirmation.",
                ErrorCode: SshAuthorizedKeysErrorCode.LastKeyProtected);
        }

        if (exitStatus == 0 && output.Contains(KeyRemovedMarker, StringComparison.Ordinal))
        {
            return new SshAuthorizedKeysMutationResult(true);
        }

        string err = string.IsNullOrWhiteSpace(error)
            ? $"Remote command exited with status {exitStatus}."
            : error.Trim();
        return new SshAuthorizedKeysMutationResult(
            false,
            ErrorMessage: err,
            ErrorCode: SshAuthorizedKeysErrorCode.RemoteCommandFailed,
            ExitStatus: exitStatus);
    }

    /// <summary>
    /// Adds a public key and refreshes the remote key list over a single SSH connection so users with
    /// interactive 2FA, hardware FIDO2 keys, or signing confirmation prompts authenticate once.
    /// </summary>
    public static async Task<(SshAuthorizedKeysMutationResult Mutation, SshAuthorizedKeysListResult? List)> AddKeyAndListAsync(
        SshConnectionParams connParams,
        string publicKeyLine,
        string? options,
        HostKeyStore hostKeyStore,
        IHostKeyVerifier verifier,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connParams);
        ArgumentNullException.ThrowIfNull(hostKeyStore);
        ArgumentNullException.ThrowIfNull(verifier);

        if (!SshPublicKeyDeployer.TryValidatePublicKeyLine(publicKeyLine, out string normalizedKey))
        {
            return (new SshAuthorizedKeysMutationResult(
                false,
                ErrorMessage: "Invalid OpenSSH public key line.",
                ErrorCode: SshAuthorizedKeysErrorCode.InvalidPublicKey), null);
        }

        if (!TryValidateOptions(options, out string normalizedOptions))
        {
            return (new SshAuthorizedKeysMutationResult(
                false,
                ErrorMessage: "Invalid authorized_keys options.",
                ErrorCode: SshAuthorizedKeysErrorCode.InvalidOptions), null);
        }

        try
        {
            using var client = await OpenConnectedClientAsync(connParams, hostKeyStore, verifier, ct).ConfigureAwait(false);
            var (exitStatus, output, error) = await RunCommandOnClientAsync(
                client,
                BuildAddKeyCommand(normalizedKey, normalizedOptions),
                ct).ConfigureAwait(false);
            var mutation = ParseAddResponse(exitStatus, output, error);
            if (!mutation.Success)
            {
                return (mutation, null);
            }

            var (listExit, listOut, listErr) = await RunCommandOnClientAsync(client, BuildListCommand(), ct)
                .ConfigureAwait(false);
            return (mutation, ParseListResponse(listExit, listOut, listErr));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (new SshAuthorizedKeysMutationResult(
                false,
                ErrorMessage: ex.Message,
                ErrorCode: SshAuthorizedKeysErrorCode.ConnectionFailed), null);
        }
    }

    /// <summary>
    /// Removes a key and refreshes the remote key list over a single SSH connection.
    /// </summary>
    public static async Task<(SshAuthorizedKeysMutationResult Mutation, SshAuthorizedKeysListResult? List)> RemoveKeyAndListAsync(
        SshConnectionParams connParams,
        string keyType,
        string keyBase64,
        bool allowRemoveLastKey,
        HostKeyStore hostKeyStore,
        IHostKeyVerifier verifier,
        CancellationToken ct = default,
        string? exactRawLine = null)
    {
        ArgumentNullException.ThrowIfNull(connParams);
        ArgumentNullException.ThrowIfNull(hostKeyStore);
        ArgumentNullException.ThrowIfNull(verifier);

        string commandText;
        try
        {
            commandText = BuildRemoveKeyCommand(keyType, keyBase64, allowRemoveLastKey, exactRawLine);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            return (new SshAuthorizedKeysMutationResult(
                false,
                ErrorMessage: ex.Message,
                ErrorCode: SshAuthorizedKeysErrorCode.InvalidPublicKey), null);
        }

        try
        {
            using var client = await OpenConnectedClientAsync(connParams, hostKeyStore, verifier, ct).ConfigureAwait(false);
            var (exitStatus, output, error) = await RunCommandOnClientAsync(client, commandText, ct)
                .ConfigureAwait(false);
            var mutation = ParseRemoveResponse(exitStatus, output, error);
            if (!mutation.Success)
            {
                return (mutation, null);
            }

            var (listExit, listOut, listErr) = await RunCommandOnClientAsync(client, BuildListCommand(), ct)
                .ConfigureAwait(false);
            return (mutation, ParseListResponse(listExit, listOut, listErr));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (new SshAuthorizedKeysMutationResult(
                false,
                ErrorMessage: ex.Message,
                ErrorCode: SshAuthorizedKeysErrorCode.ConnectionFailed), null);
        }
    }
}
