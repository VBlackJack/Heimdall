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

using System.Runtime.Versioning;
using System.Security.AccessControl;
using Heimdall.Core.Discovery;

namespace Heimdall.Core.Tests;

/// <summary>
/// Saving a scan snapshot is one atomic write that leaves the file restricted, and it keeps the
/// file name <see cref="ScanHistoryManager.ListSnapshots"/> reads back by position.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ScanHistorySaveTests : IDisposable
{
    private static readonly DateTime SnapshotTime = new(2026, 10, 27, 14, 5, 9, DateTimeKind.Utc);
    private const string Subnet = "192.168.1.0/24";

    private readonly string _dir;

    public ScanHistorySaveTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "heimdall_scan_save_" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    /// <summary>
    /// A scanner, an indexer or a backup holds the previous snapshot open while sharing delete.
    /// The save used to rename over it with the classic replace and then delete and recreate
    /// the file to set its ACL, and the rename was refused as soon as the file was open.
    /// </summary>
    [Fact]
    public async Task SaveSnapshotAsync_TargetHeldOpenWithDeleteSharing_IsReplacedAndStaysRestricted()
    {
        await ScanHistoryManager.SaveSnapshotAsync(_dir, Snapshot(hostname: "first"));
        var path = SingleSnapshotPath();

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            await ScanHistoryManager.SaveSnapshotAsync(_dir, Snapshot(hostname: "second"));
        }

        Assert.Contains("second", await File.ReadAllTextAsync(path));
        AssertProtectedAcl(path);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp*"));
    }

    [Fact]
    public async Task SaveSnapshotAsync_FirstSave_CreatesARestrictedFileTheListParses()
    {
        await ScanHistoryManager.SaveSnapshotAsync(_dir, Snapshot(hostname: "first"));

        AssertProtectedAcl(SingleSnapshotPath());
        var listed = Assert.Single(ScanHistoryManager.ListSnapshots(_dir));
        Assert.Equal(SnapshotTime, listed.Timestamp);
        Assert.Equal(Subnet, listed.Subnet);
    }

    /// <summary>
    /// The name is the time to the second plus the subnet and is parsed by position, so it is
    /// not made unique: a second save in the same second replaces the first cleanly.
    /// </summary>
    [Fact]
    public async Task SaveSnapshotAsync_TwoSavesInTheSameSecond_LeaveOneParsableFileWithTheLaterContent()
    {
        await ScanHistoryManager.SaveSnapshotAsync(_dir, Snapshot(hostname: "first"));
        await ScanHistoryManager.SaveSnapshotAsync(_dir, Snapshot(hostname: "second"));

        var path = SingleSnapshotPath();
        Assert.Contains("second", await File.ReadAllTextAsync(path));
        var listed = Assert.Single(ScanHistoryManager.ListSnapshots(_dir));
        Assert.Equal(Path.GetFileName(path), listed.FileName);
        var loaded = ScanHistoryManager.LoadSnapshot(_dir, listed.FileName);
        Assert.Equal("second", Assert.Single(loaded!.Hosts).Hostname);
    }

    private string SingleSnapshotPath() => Assert.Single(Directory.GetFiles(_dir, "scan_*.json"));

    private static NetworkScanSnapshot Snapshot(string hostname)
    {
        var host = new HostScanResult("192.168.1.10", hostname, true, 0, [], null, []);
        return new NetworkScanSnapshot(
            "test",
            SnapshotTime,
            new ScanProfile(Subnet, ScanDepth.Quick, null, 50, 2000, false, false),
            null,
            TimeSpan.Zero,
            [host]);
    }

    private static void AssertProtectedAcl(string path)
    {
        FileSecurity acl = new FileInfo(path).GetAccessControl();
        Assert.True(acl.AreAccessRulesProtected, "the snapshot must not inherit a permissive ACL");
    }
}
