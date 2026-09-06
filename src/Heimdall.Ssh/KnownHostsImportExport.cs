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
using Heimdall.Core.Logging;
using Heimdall.Core.Ssh;

namespace Heimdall.Ssh;

/// <summary>
/// A known_hosts line whose key differs from the stored one. The imported public
/// key blob travels with the conflict so a "replace" decision can store the key
/// it actually refers to instead of inheriting the rejected blob.
/// </summary>
public sealed record KnownHostsImportConflict(
    string Host,
    int Port,
    string ExistingFingerprint,
    string ImportedFingerprint,
    string Algorithm,
    int SourceLineNumber,
    string? ImportedPublicKeyBase64 = null);

public sealed record KnownHostsImportReport(
    int Imported,
    int Matched,
    IReadOnlyList<KnownHostsImportConflict> Conflicts);

public sealed record KnownHostsExportReport(
    int Written,
    int Preserved,
    int SkippedWithoutPublicKey);

public sealed class KnownHostsImporter(IHostKeyTrustService trustService)
{
    private readonly IHostKeyTrustService _trustService = trustService;

    public KnownHostsImportReport ImportFile(string? path = null, DateTimeOffset? importedAt = null)
    {
        path ??= GetDefaultKnownHostsPath();
        if (!File.Exists(path))
        {
            return new KnownHostsImportReport(0, 0, []);
        }

        KnownHostsParseResult parseResult;
        try
        {
            var fileInfo = new FileInfo(path);
            if (fileInfo.Length > KnownHostsParser.MaxFileSizeBytes)
            {
                FileLogger.Warn(
                    $"known_hosts import refused: file '{path}' exceeds {KnownHostsParser.MaxFileSizeBytes} bytes ({fileInfo.Length} bytes).");
                return new KnownHostsImportReport(0, 0, []);
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            parseResult = KnownHostsParser.Parse(reader);
        }
        catch (IOException ex)
        {
            FileLogger.Warn($"known_hosts import skipped: I/O error reading '{path}': {ex.Message}");
            return new KnownHostsImportReport(0, 0, []);
        }
        catch (UnauthorizedAccessException ex)
        {
            FileLogger.Warn($"known_hosts import skipped: access denied to '{path}': {ex.Message}");
            return new KnownHostsImportReport(0, 0, []);
        }
        catch (DecoderFallbackException ex)
        {
            FileLogger.Warn($"known_hosts import skipped: decoding error in '{path}': {ex.Message}");
            return new KnownHostsImportReport(0, 0, []);
        }

        return Import(parseResult, importedAt ?? DateTimeOffset.UtcNow);
    }

    public KnownHostsImportReport Import(KnownHostsParseResult parseResult, DateTimeOffset importedAt)
    {
        ArgumentNullException.ThrowIfNull(parseResult);

        var imported = 0;
        var matched = 0;
        var conflicts = new List<KnownHostsImportConflict>();

        foreach (var diagnostic in parseResult.Diagnostics)
        {
            LogDiagnostic(diagnostic);
        }

        foreach (var entry in parseResult.Entries)
        {
            var fingerprint = HostKeyFormats.ComputeSha256Fingerprint(entry.Base64Key);
            var existing = _trustService.GetEntry(entry.Host, entry.Port);
            if (existing is null)
            {
                _trustService.Import(
                    entry.Host,
                    entry.Port,
                    fingerprint,
                    entry.KeyType,
                    importedAt,
                    Convert.ToBase64String(entry.Base64Key));
                imported++;
                continue;
            }

            if (string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                _trustService.Verify(entry.Host, entry.Port, fingerprint, entry.KeyType);
                matched++;
                FileLogger.Info($"known_hosts import matched existing trust entry for {entry.Host}:{entry.Port}");
                continue;
            }

            conflicts.Add(new KnownHostsImportConflict(
                entry.Host,
                entry.Port,
                existing.Fingerprint,
                fingerprint,
                entry.KeyType,
                entry.SourceLineNumber,
                Convert.ToBase64String(entry.Base64Key)));
            FileLogger.Warn(
                $"known_hosts import conflict for {entry.Host}:{entry.Port}: existing={existing.Fingerprint} imported={fingerprint}");
        }

        return new KnownHostsImportReport(imported, matched, conflicts);
    }

    public static string GetDefaultKnownHostsPath()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(userProfile, ".ssh", "known_hosts");
    }

    private static void LogDiagnostic(KnownHostsImportDiagnostic diagnostic)
    {
        var message = $"known_hosts line {diagnostic.SourceLineNumber}: {diagnostic.Code}"
            + (diagnostic.Context is null ? string.Empty : $" ({diagnostic.Context})");

        if (diagnostic.Level == KnownHostsDiagnosticLevel.Warning)
        {
            FileLogger.Warn(message);
        }
        else
        {
            FileLogger.Info(message);
        }
    }
}

public sealed class KnownHostsExporter(
    IHostKeyTrustService trustService,
    Action<string, string>? writeAllText = null)
{
    private readonly IHostKeyTrustService _trustService = trustService;
    // OpenSSH reads known_hosts byte for byte and does not strip a UTF-8 BOM, so a
    // preamble would make the first line fail to match its host on every ssh call.
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly Action<string, string> _writeAllText = writeAllText ?? ((path, content) => File.WriteAllText(path, content, Utf8NoBom));

    public KnownHostsExportReport ExportFile(string? path = null)
    {
        path ??= KnownHostsImporter.GetDefaultKnownHostsPath();
        // Known, accepted TOCTOU: an external writer appending to this
        // OpenSSH-format interop file between the read and the atomic rewrite
        // can be lost. Heimdall's trust store is the source of truth; this file
        // is only an export convenience.
        var existingLines = File.Exists(path)
            ? File.ReadAllLines(path, Encoding.UTF8).ToList()
            : [];

        var entries = _trustService.GetAllEntries();
        var writableEntries = entries
            .Where(static entry => TryExportLine(entry.HostPort, entry.Entry, out _))
            .ToDictionary(static entry => entry.HostPort, static entry => entry.Entry, StringComparer.Ordinal);
        var written = new HashSet<string>(StringComparer.Ordinal);
        var output = new List<string>();
        var preserved = 0;

        foreach (var line in existingLines)
        {
            // A line is rewritten only when every host token on it is a managed host.
            // A line that also names hosts Heimdall does not manage is kept verbatim so
            // those hosts survive the export; the managed entries are appended below.
            IReadOnlyList<string> managedKeys = FindFullyManagedHostKeys(line, writableEntries);
            if (managedKeys.Count == 0)
            {
                output.Add(line);
                preserved++;
                continue;
            }

            foreach (string matchedKey in managedKeys)
            {
                if (written.Add(matchedKey)
                    && TryExportLine(matchedKey, writableEntries[matchedKey], out var replacement))
                {
                    output.Add(replacement);
                }
            }
        }

        foreach (var (hostPort, entry) in writableEntries)
        {
            if (written.Add(hostPort) && TryExportLine(hostPort, entry, out var line))
            {
                output.Add(line);
            }
        }

        var skipped = entries.Count - writableEntries.Count;
        WriteAtomic(path, string.Join(Environment.NewLine, output) + Environment.NewLine);
        return new KnownHostsExportReport(written.Count, preserved, skipped);
    }

    private void WriteAtomic(string path, string content)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = Path.Combine(directory ?? string.Empty, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        _writeAllText(temp, content);

        if (File.Exists(path))
        {
            File.Replace(temp, path, null, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(temp, path);
        }
    }

    /// <summary>
    /// Returns the managed <c>host:port</c> keys of a known_hosts line when every host token
    /// on the line maps to a managed entry, and an empty list otherwise. The parser emits one
    /// entry per clear or hashed token and a diagnostic per token it cannot represent, so a
    /// line with any diagnostic, any hashed token or any unmanaged host is not fully managed.
    /// </summary>
    private static IReadOnlyList<string> FindFullyManagedHostKeys(
        string line,
        IReadOnlyDictionary<string, HostKeyEntry> entries)
    {
        KnownHostsParseResult parsed = KnownHostsParser.Parse(line);
        if (parsed.Diagnostics.Count > 0 || parsed.Entries.Count == 0)
        {
            return [];
        }

        List<string> keys = new List<string>(parsed.Entries.Count);
        foreach (KnownHostsRawEntry entry in parsed.Entries)
        {
            if (entry.IsHashedHost)
            {
                return [];
            }

            string key = HostKeyFormats.MakeKey(entry.Host, entry.Port);
            if (!entries.ContainsKey(key))
            {
                return [];
            }

            keys.Add(key);
        }

        return keys;
    }

    private static bool TryExportLine(string hostPort, HostKeyEntry entry, out string line)
    {
        line = string.Empty;
        if (string.IsNullOrWhiteSpace(entry.PublicKeyBase64)
            || !HostKeyFormats.TryParseKey(hostPort, out var host, out var port)
            || host.StartsWith("|1|", StringComparison.Ordinal))
        {
            return false;
        }

        var hostField = port == 22 ? host : $"[{host}]:{port}";
        line = $"{hostField} {entry.Algorithm} {entry.PublicKeyBase64}";
        return true;
    }
}
