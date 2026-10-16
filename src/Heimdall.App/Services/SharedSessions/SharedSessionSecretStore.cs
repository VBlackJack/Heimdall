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

using System.IO;
using System.Text.Json;
using Heimdall.Core.Configuration;
using Heimdall.Core.Logging;
using Heimdall.Core.Security;

namespace Heimdall.App.Services.SharedSessions;

/// <summary>What a remembered secret of a shared source opens.</summary>
public enum SharedSecretKind
{
    /// <summary>A bastion of a shared source, keyed by its host, port and user.</summary>
    Gateway,

    /// <summary>A shared session, keyed by its profile, host, port and user.</summary>
    Session,
}

/// <summary>
/// The passwords a user chose to remember for shared sessions and shared gateways, kept on this
/// machine only.
/// </summary>
/// <remarks>
/// <para>One JSON document in the data root, <see cref="FileName"/>, written atomically with the
/// restrictive ACL of the configuration files. Each secret is the DPAPI-sealed form
/// <see cref="CredentialProtector"/> produces, never the plain text. The document is not part of
/// the settings, so a settings export or import never carries it and Reset defaults never clears it;
/// it is never written into a shared file, the offline copy or servers.json.</para>
/// <para>A secret is filed under its source and a key built from what it opens: the host, port and
/// user of a gateway; the profile, host, port and user of a session. The profile identifier alone
/// would not do: it is minted from the folder and the name of the session, so a file that points a
/// session at another host keeps it. A reload of the shared file finds a secret again as long as
/// those are unchanged; a file that moves the session to another host, port or user names another
/// key and is asked again, so a secret is never sent to a target it was not given for.</para>
/// </remarks>
public sealed class SharedSessionSecretStore
{
    /// <summary>The name of the document in the data root.</summary>
    public const string FileName = "shared-session-secrets.json";

    private const int FormatVersion = 1;
    private const char KeySeparator = '|';

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly object _gate = new();
    private List<SecretEntry>? _entries;

    /// <param name="path">The document the secrets are kept in.</param>
    public SharedSessionSecretStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    /// <summary>The store in the data root of this process.</summary>
    public static SharedSessionSecretStore ForDataRoot() =>
        new(Path.Combine(ApplicationDataPathResolver.Resolve(), FileName));

    /// <summary>The document the secrets are kept in.</summary>
    public string FilePath => _path;

    /// <summary>The key of a gateway's secret: its host (in any case), port and user.</summary>
    public static string GatewayKey(string host, int port, string user) =>
        string.Join(KeySeparator, Endpoint(host, port), user);

    /// <summary>The key of a session's secret: its profile, host (in any case), port and user.</summary>
    public static string SessionKey(string profileId, string host, int port, string user) =>
        string.Join(KeySeparator, profileId, Endpoint(host, port), user);

    private static string Endpoint(string host, int port) =>
        host.Trim().ToLowerInvariant() + KeySeparator + port.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The sealed secret remembered for <paramref name="key"/>, or null.</summary>
    public string? Find(SharedSecretKind kind, string sourceId, string key)
    {
        lock (_gate)
        {
            return Entries().FirstOrDefault(entry => entry.Matches(kind, sourceId, key))?.Secret;
        }
    }

    /// <summary>How many secrets are remembered for <paramref name="sourceId"/>.</summary>
    public int CountFor(string sourceId)
    {
        lock (_gate)
        {
            return Entries().Count(entry => string.Equals(entry.SourceId, sourceId, StringComparison.Ordinal));
        }
    }

    /// <summary>Remembers <paramref name="sealedSecret"/> for <paramref name="key"/>, replacing any.</summary>
    public Task RememberAsync(SharedSecretKind kind, string sourceId, string key, string sealedSecret)
    {
        ArgumentException.ThrowIfNullOrEmpty(sealedSecret);
        return ChangeAsync(entries =>
        {
            entries.RemoveAll(entry => entry.Matches(kind, sourceId, key));
            entries.Add(new SecretEntry { Kind = kind, SourceId = sourceId, Key = key, Secret = sealedSecret });
            return true;
        });
    }

    /// <summary>Forgets the secret of <paramref name="key"/>, if any.</summary>
    public Task ForgetAsync(SharedSecretKind kind, string sourceId, string key) =>
        ChangeAsync(entries => entries.RemoveAll(entry => entry.Matches(kind, sourceId, key)) > 0);

    /// <summary>Forgets every secret of <paramref name="sourceId"/>.</summary>
    public Task ForgetSourceAsync(string sourceId) =>
        ChangeAsync(entries => entries.RemoveAll(entry => string.Equals(entry.SourceId, sourceId, StringComparison.Ordinal)) > 0);

    /// <summary>Forgets every secret of a source not in <paramref name="keptSourceIds"/>.</summary>
    public Task ForgetAllExceptAsync(IReadOnlySet<string> keptSourceIds)
    {
        ArgumentNullException.ThrowIfNull(keptSourceIds);
        return ChangeAsync(entries => entries.RemoveAll(entry => !keptSourceIds.Contains(entry.SourceId)) > 0);
    }

    private async Task ChangeAsync(Func<List<SecretEntry>, bool> change)
    {
        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            List<SecretEntry> next;
            lock (_gate)
            {
                next = [.. Entries()];
            }

            if (!change(next))
            {
                return;
            }

            try
            {
                string json = JsonSerializer.Serialize(new SecretDocument { FormatVersion = FormatVersion, Entries = next }, JsonOptions);
                string? directory = Path.GetDirectoryName(Path.GetFullPath(_path));
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                await SecureFileWriter.WriteAllTextAtomicAsync(_path, json).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The secrets stay as they were in memory too: what is on screen must be what the
                // next start finds.
                FileLogger.Warn($"Shared sessions: the remembered passwords could not be saved: {ex.Message}");
                return;
            }

            lock (_gate)
            {
                _entries = next;
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>The entries, read from the document the first time. Called under <see cref="_gate"/>.</summary>
    private List<SecretEntry> Entries()
    {
        if (_entries is not null)
        {
            return _entries;
        }

        _entries = [];
        if (!File.Exists(_path))
        {
            return _entries;
        }

        try
        {
            SecretDocument? document = JsonSerializer.Deserialize<SecretDocument>(File.ReadAllText(_path));
            if (document?.FormatVersion == FormatVersion && document.Entries is not null)
            {
                _entries = [.. document.Entries.Where(entry => entry.IsComplete)];
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            FileLogger.Warn($"Shared sessions: the remembered passwords could not be read: {ex.Message}");
        }

        return _entries;
    }

    private sealed class SecretDocument
    {
        public int FormatVersion { get; set; }

        public List<SecretEntry>? Entries { get; set; }
    }

    private sealed class SecretEntry
    {
        public SharedSecretKind Kind { get; set; }

        public string SourceId { get; set; } = string.Empty;

        public string Key { get; set; } = string.Empty;

        public string Secret { get; set; } = string.Empty;

        public bool IsComplete =>
            SharedSessionIds.IsValidSourceId(SourceId) && Key.Length > 0 && Secret.Length > 0;

        public bool Matches(SharedSecretKind kind, string sourceId, string key) =>
            Kind == kind
            && string.Equals(SourceId, sourceId, StringComparison.Ordinal)
            && string.Equals(Key, key, StringComparison.Ordinal);
    }
}
