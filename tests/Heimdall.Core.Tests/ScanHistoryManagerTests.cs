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

using Heimdall.Core.Discovery;

namespace Heimdall.Core.Tests;

public class ScanHistoryManagerTests
{
    // A directory of this test run that is never created: these tests only read, and a
    // default directory would be the developer's own scan history.
    private static readonly string HistoryDir =
        Path.Combine(Path.GetTempPath(), "heimdall-scan-history-tests", Guid.NewGuid().ToString("N"));

    // ── LoadSnapshot path validation ────────────────────────────────

    [Theory]
    [InlineData("../../../etc/passwd")]
    [InlineData("..\\..\\windows\\system32\\config\\sam")]
    [InlineData("subdir/file.json")]
    [InlineData("subdir\\file.json")]
    public void LoadSnapshot_PathTraversal_ReturnsNull(string maliciousName)
    {
        var result = ScanHistoryManager.LoadSnapshot(HistoryDir, maliciousName);
        Assert.Null(result);
    }

    [Theory]
    [InlineData("notascan.json")]
    [InlineData("malicious_file.json")]
    [InlineData("settings.json")]
    public void LoadSnapshot_NonScanPrefix_ReturnsNull(string fileName)
    {
        Assert.Null(ScanHistoryManager.LoadSnapshot(HistoryDir, fileName));
    }

    [Fact]
    public void LoadSnapshot_EmptyFileName_ReturnsNull()
    {
        Assert.Null(ScanHistoryManager.LoadSnapshot(HistoryDir, ""));
    }

    [Fact]
    public void LoadSnapshot_WhitespaceFileName_ReturnsNull()
    {
        Assert.Null(ScanHistoryManager.LoadSnapshot(HistoryDir, "   "));
    }

    [Fact]
    public void LoadSnapshot_NonExistentFile_ReturnsNull()
    {
        Assert.Null(ScanHistoryManager.LoadSnapshot(HistoryDir, "nonexistent_scan.json"));
    }

    // ── ComputeDiff edge cases ──────────────────────────────────────

    [Fact]
    public void ComputeDiff_BothEmpty_AllEmpty()
    {
        var older = CreateSnapshot([]);
        var newer = CreateSnapshot([]);

        var diff = ScanHistoryManager.ComputeDiff(older, newer);

        Assert.Empty(diff.NewHosts);
        Assert.Empty(diff.RemovedHosts);
        Assert.Empty(diff.ModifiedHosts);
    }

    [Fact]
    public void ComputeDiff_HostnameChanged_DetectedAsChange()
    {
        var older = CreateSnapshot(["192.168.1.1"], hostname: "old-host");
        var newer = CreateSnapshot(["192.168.1.1"], hostname: "new-host");

        var diff = ScanHistoryManager.ComputeDiff(older, newer);

        Assert.Single(diff.ModifiedHosts);
        var changes = diff.ModifiedHosts[0].Changes;
        Assert.Contains(changes, c => c.Type == HostChangeType.HostnameChanged);
        Assert.Contains(changes, c => c.OldValue == "old-host" && c.NewValue == "new-host");
    }

    [Fact]
    public void ComputeDiff_RoleChanged_DetectedAsChange()
    {
        var older = CreateSnapshot(["192.168.1.1"],
            role: new RoleMatch("SSH Server", 50, ["port:22"]));
        var newer = CreateSnapshot(["192.168.1.1"],
            role: new RoleMatch("Web Server", 70, ["port:80"]));

        var diff = ScanHistoryManager.ComputeDiff(older, newer);

        Assert.Single(diff.ModifiedHosts);
        Assert.Contains(diff.ModifiedHosts[0].Changes,
            c => c.Type == HostChangeType.RoleChanged);
    }

    [Fact]
    public void ComputeDiff_PortRemoved_DetectedAsChange()
    {
        var older = CreateSnapshot(["192.168.1.1"], ports: [22, 80, 443]);
        var newer = CreateSnapshot(["192.168.1.1"], ports: [22, 80]);

        var diff = ScanHistoryManager.ComputeDiff(older, newer);

        Assert.Single(diff.ModifiedHosts);
        Assert.Contains(diff.ModifiedHosts[0].Changes,
            c => c.Type == HostChangeType.PortRemoved && c.Port == 443);
    }

    [Fact]
    public void ComputeDiff_MultiplePortChanges_AllDetected()
    {
        var older = CreateSnapshot(["192.168.1.1"], ports: [22, 80]);
        var newer = CreateSnapshot(["192.168.1.1"], ports: [80, 443, 8080]);

        var diff = ScanHistoryManager.ComputeDiff(older, newer);

        var changes = diff.ModifiedHosts[0].Changes;
        Assert.Contains(changes, c => c.Type == HostChangeType.PortRemoved && c.Port == 22);
        Assert.Contains(changes, c => c.Type == HostChangeType.PortAdded && c.Port == 443);
        Assert.Contains(changes, c => c.Type == HostChangeType.PortAdded && c.Port == 8080);
    }

    [Fact]
    public void ComputeDiff_ManufacturerChanged_DetectedAsChange()
    {
        var older = CreateSnapshot(["192.168.1.1"], manufacturer: "VMware");
        var newer = CreateSnapshot(["192.168.1.1"], manufacturer: "Dell");

        var diff = ScanHistoryManager.ComputeDiff(older, newer);

        Assert.Single(diff.ModifiedHosts);
        Assert.Contains(diff.ModifiedHosts[0].Changes,
            c => c.Type == HostChangeType.ManufacturerChanged);
    }

    [Fact]
    public void ComputeDiff_NetBiosChanged_DetectedAsChange()
    {
        var older = CreateSnapshot(["192.168.1.1"], netBiosName: "OLDPC");
        var newer = CreateSnapshot(["192.168.1.1"], netBiosName: "NEWPC");

        var diff = ScanHistoryManager.ComputeDiff(older, newer);

        Assert.Single(diff.ModifiedHosts);
        Assert.Contains(diff.ModifiedHosts[0].Changes,
            c => c.Type == HostChangeType.NetBiosChanged);
    }

    // ── Retention policy ────────────────────────────────────────────

    [Fact]
    public void EnforceRetentionPolicy_KeepsMaxFiles()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"heimdall_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            // Create more files than the retention limit
            for (var i = 0; i < ScanHistoryManager.MaxRetainedSnapshots + 5; i++)
            {
                var path = Path.Combine(tempDir, $"scan_{i:D4}.json");
                File.WriteAllText(path, "{}");
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-i));
            }

            ScanHistoryManager.EnforceRetentionPolicy(tempDir);

            var remaining = Directory.GetFiles(tempDir, "scan_*.json");
            Assert.Equal(ScanHistoryManager.MaxRetainedSnapshots, remaining.Length);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void EnforceRetentionPolicy_UnderLimit_DeletesNothing()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"heimdall_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            for (var i = 0; i < 5; i++)
            {
                File.WriteAllText(Path.Combine(tempDir, $"scan_{i:D4}.json"), "{}");
            }

            ScanHistoryManager.EnforceRetentionPolicy(tempDir);

            var remaining = Directory.GetFiles(tempDir, "scan_*.json");
            Assert.Equal(5, remaining.Length);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static NetworkScanSnapshot CreateSnapshot(
        string[] ips,
        int[]? ports = null,
        string? hostname = null,
        RoleMatch? role = null,
        OsFingerprint? os = null,
        string? manufacturer = null,
        string? netBiosName = null)
    {
        var openPorts = ports ?? [];
        var hosts = ips.Select(ip => new HostScanResult(
            ip, hostname, true, 0,
            openPorts.Select(p => new ServiceResult(p, true, null, null, null, 0)).ToList(),
            role, role is not null ? [role] : [],
            Manufacturer: manufacturer,
            OsFingerprint: os,
            NetBiosName: netBiosName
        )).ToList();

        return new NetworkScanSnapshot(
            "test", DateTime.UtcNow,
            new ScanProfile("192.168.1.0/24", ScanDepth.Quick, null, 50, 2000, false, false),
            null, TimeSpan.Zero, hosts);
    }
}
