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

namespace Heimdall.Core.Discovery;

/// <summary>
/// Persists and loads network scan snapshots to enable historical comparison
/// and diff analysis between scan runs.
/// </summary>
public static class ScanHistoryManager
{
    private static readonly JsonSerializerOptions SerializeOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly JsonSerializerOptions DeserializeOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>Maximum number of scan snapshots to retain on disk.</summary>
    internal const int MaxRetainedSnapshots = 20;

    private static string GetScanDir() =>
        ApplicationDataPathResolver.GetNetworkScansDirectory(
            ApplicationDataPathResolver.Resolve());

    /// <summary>
    /// Persists a scan snapshot to the history directory as JSON.
    /// Uses atomic temp-file-then-rename to prevent corruption on crash.
    /// On Windows, applies restrictive ACL via <see cref="Security.SecureFileWriter"/>.
    /// </summary>
    public static async Task SaveSnapshotAsync(NetworkScanSnapshot snapshot)
    {
        var dir = GetScanDir();
        Directory.CreateDirectory(dir);
        var fileName = $"scan_{snapshot.Timestamp:yyyyMMdd_HHmmss}_{snapshot.Profile.Subnet.Replace('/', '-')}.json";
        var targetPath = Path.Combine(dir, fileName);
        var json = JsonSerializer.Serialize(snapshot, SerializeOptions);

        // Atomic write: write to temp file, then rename
        var tempPath = targetPath + ".tmp";
        try
        {
            await File.WriteAllTextAsync(tempPath, json).ConfigureAwait(false);
            File.Move(tempPath, targetPath, overwrite: true);
        }
        catch
        {
            // Clean up temp file on failure
            try { File.Delete(tempPath); } catch { /* best effort */ }
            throw;
        }

        // Apply restrictive ACL on Windows
        if (OperatingSystem.IsWindows())
        {
            try { Security.SecureFileWriter.WriteAndProtect(targetPath, json); }
            catch { /* ACL enforcement is best-effort for scan history */ }
        }

        // Enforce retention policy
        EnforceRetentionPolicy(dir);
    }

    /// <summary>
    /// Lists all saved scan snapshots ordered by most recent first.
    /// </summary>
    public static List<(string FileName, DateTime Timestamp, string Subnet)> ListSnapshots()
    {
        var dir = GetScanDir();
        if (!Directory.Exists(dir)) return [];

        return Directory.GetFiles(dir, "scan_*.json")
            .Select(f =>
            {
                try
                {
                    // Parse metadata from filename (scan_yyyyMMdd_HHmmss_subnet.json)
                    // to avoid deserializing every snapshot file
                    var name = Path.GetFileNameWithoutExtension(f);
                    if (name.Length < 22 || !name.StartsWith("scan_", StringComparison.Ordinal))
                        return default;
                    var datePart = name[5..20]; // yyyyMMdd_HHmmss
                    if (!DateTime.TryParseExact(datePart, "yyyyMMdd_HHmmss",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal |
                        System.Globalization.DateTimeStyles.AdjustToUniversal,
                        out var timestamp))
                        return default;
                    var subnet = name[21..].Replace('-', '/');
                    return (FileName: Path.GetFileName(f), Timestamp: timestamp, Subnet: subnet);
                }
                catch { return default; }
            })
            .Where(x => x.FileName is not null)
            .OrderByDescending(x => x.Timestamp)
            .ToList();
    }

    /// <summary>
    /// Loads a specific scan snapshot from a history file.
    /// </summary>
    public static NetworkScanSnapshot? LoadSnapshot(string fileName)
    {
        // Path traversal prevention (CWE-22) + filename whitelist
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        var sanitized = Path.GetFileName(fileName);
        if (sanitized != fileName || fileName.Contains("..")) return null;
        if (!sanitized.StartsWith("scan_", StringComparison.Ordinal)) return null;

        var path = Path.Combine(GetScanDir(), sanitized);
        if (!File.Exists(path)) return null;
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<NetworkScanSnapshot>(json, DeserializeOptions);
    }

    /// <summary>
    /// Computes the differences between two scan snapshots: new hosts, removed hosts,
    /// and per-host changes (ports, hostname, role).
    /// </summary>
    public static ScanDiff ComputeDiff(NetworkScanSnapshot older, NetworkScanSnapshot newer)
    {
        var oldIps = older.Hosts.ToDictionary(h => h.IpAddress);
        var newIps = newer.Hosts.ToDictionary(h => h.IpAddress);

        var newHosts = newer.Hosts.Where(h => !oldIps.ContainsKey(h.IpAddress)).ToList();
        var removedHosts = older.Hosts.Where(h => !newIps.ContainsKey(h.IpAddress)).ToList();

        var modified = new List<(HostScanResult Old, HostScanResult New, List<HostChange> Changes)>();
        foreach (var newHost in newer.Hosts)
        {
            if (!oldIps.TryGetValue(newHost.IpAddress, out var oldHost)) continue;

            var changes = new List<HostChange>();

            var oldPorts = new HashSet<int>(oldHost.Services.Where(s => s.IsOpen).Select(s => s.Port));
            var newPorts = new HashSet<int>(newHost.Services.Where(s => s.IsOpen).Select(s => s.Port));

            foreach (var p in newPorts.Except(oldPorts))
                changes.Add(new HostChange(HostChangeType.PortAdded, null, null, p));
            foreach (var p in oldPorts.Except(newPorts))
                changes.Add(new HostChange(HostChangeType.PortRemoved, null, null, p));

            if (oldHost.Hostname != newHost.Hostname)
                changes.Add(new HostChange(HostChangeType.HostnameChanged, oldHost.Hostname, newHost.Hostname));

            if (oldHost.PrimaryRole?.Role != newHost.PrimaryRole?.Role)
                changes.Add(new HostChange(HostChangeType.RoleChanged, oldHost.PrimaryRole?.Role, newHost.PrimaryRole?.Role));

            if (oldHost.OsFingerprint?.OsGuess != newHost.OsFingerprint?.OsGuess)
                changes.Add(new HostChange(HostChangeType.OsChanged, oldHost.OsFingerprint?.OsGuess, newHost.OsFingerprint?.OsGuess));

            if (oldHost.NetBiosName != newHost.NetBiosName)
                changes.Add(new HostChange(HostChangeType.NetBiosChanged, oldHost.NetBiosName, newHost.NetBiosName));

            if (oldHost.Manufacturer != newHost.Manufacturer)
                changes.Add(new HostChange(HostChangeType.ManufacturerChanged, oldHost.Manufacturer, newHost.Manufacturer));

            if (changes.Count > 0)
                modified.Add((oldHost, newHost, changes));
        }

        return new ScanDiff(newHosts, removedHosts, modified);
    }

    /// <summary>
    /// Removes the oldest scan snapshots when the count exceeds <see cref="MaxRetainedSnapshots"/>.
    /// </summary>
    internal static void EnforceRetentionPolicy(string dir)
    {
        try
        {
            var files = Directory.GetFiles(dir, "scan_*.json")
                .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
                .ToList();

            if (files.Count <= MaxRetainedSnapshots) return;

            foreach (var file in files.Skip(MaxRetainedSnapshots))
            {
                try { File.Delete(file); }
                catch { /* best effort cleanup */ }
            }
        }
        catch { /* retention enforcement is best-effort */ }
    }
}
