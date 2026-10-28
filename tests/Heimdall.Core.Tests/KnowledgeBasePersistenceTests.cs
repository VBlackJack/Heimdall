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
using System.Text;
using Heimdall.Core.Discovery;

namespace Heimdall.Core.Tests;

/// <summary>
/// The knowledge base file is read from and written to a path the caller gives, never the
/// profile's: a corrupt file is set aside rather than overwritten, a file that could not be
/// read is not saved over, and a save is one atomic restricted write.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class KnowledgeBasePersistenceTests : IDisposable
{
    private static readonly DateTime ScanTime = new(2026, 10, 27, 9, 0, 0, DateTimeKind.Utc);
    private const string CorruptMarker = ".corrupt-";

    private readonly string _dir;
    private readonly string _path;

    public KnowledgeBasePersistenceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "heimdall_kb_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "network-kb.json");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    [Fact]
    public async Task LoadAsync_NoFile_GivesAnEmptyWritableKnowledgeBase_AndCreatesNothing()
    {
        var kb = await KnowledgeBaseManager.LoadAsync(_path);

        Assert.Empty(kb.Hosts);
        Assert.False(kb.IsReadOnly);
        Assert.False(File.Exists(_path));
    }

    [Fact]
    public async Task SaveThenLoad_RoundTripsTheHosts_AndNeverStoresTheReadOnlyFlag()
    {
        await KnowledgeBaseManager.SaveAsync(KnowledgeBaseWithOneHost(), _path);

        var loaded = await KnowledgeBaseManager.LoadAsync(_path);

        Assert.Equal(1, KnowledgeBaseManager.HostCount(loaded));
        Assert.False(loaded.IsReadOnly);
        Assert.DoesNotContain("isReadOnly", await File.ReadAllTextAsync(_path), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A file that does not parse used to be forgotten ("starting fresh") and the next save wrote
    /// over it, so the one copy of what the operator had accumulated was gone.
    /// </summary>
    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("{}")]
    public async Task LoadAsync_CorruptFile_IsSetAsideWithItsBytes_AndASaveDoesNotTouchTheCopy(string content)
    {
        var original = Encoding.UTF8.GetBytes(content);
        await File.WriteAllBytesAsync(_path, original);

        var kb = await KnowledgeBaseManager.LoadAsync(_path);
        await KnowledgeBaseManager.SaveAsync(KnowledgeBaseWithOneHost(), _path);

        Assert.Empty(kb.Hosts);
        Assert.False(kb.IsReadOnly);
        var aside = Assert.Single(Directory.GetFiles(_dir, "network-kb.json" + CorruptMarker + "*"));
        Assert.Equal(original, await File.ReadAllBytesAsync(aside));
        Assert.Equal(1, KnowledgeBaseManager.HostCount(await KnowledgeBaseManager.LoadAsync(_path)));
    }

    /// <summary>
    /// A file that cannot be opened is not known to be corrupt. The instance it yields is read
    /// only, so a save is refused and the file keeps what it holds; a later load of the same
    /// path, once the file can be read, is writable again because the state belongs to the
    /// loaded instance and not to the process.
    /// </summary>
    [Fact]
    public async Task LoadAsync_FileThatCannotBeRead_IsReadOnly_AndASaveIsRefused()
    {
        await KnowledgeBaseManager.SaveAsync(KnowledgeBaseWithOneHost(), _path);
        var before = await File.ReadAllBytesAsync(_path);

        NetworkKnowledgeBase unreadable;
        using (new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            unreadable = await KnowledgeBaseManager.LoadAsync(_path);
        }

        Assert.True(unreadable.IsReadOnly);
        Assert.Empty(unreadable.Hosts);

        await KnowledgeBaseManager.SaveAsync(unreadable, _path);

        Assert.Equal(before, await File.ReadAllBytesAsync(_path));
        Assert.Empty(Directory.GetFiles(_dir, "*" + CorruptMarker + "*"));
        Assert.False((await KnowledgeBaseManager.LoadAsync(_path)).IsReadOnly);
    }

    [Fact]
    public async Task ReadOnlyKnowledgeBase_StaysReadOnlyAfterAMerge()
    {
        NetworkKnowledgeBase unreadable;
        await KnowledgeBaseManager.SaveAsync(KnowledgeBaseWithOneHost(), _path);
        using (new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            unreadable = await KnowledgeBaseManager.LoadAsync(_path);
        }

        var merged = KnowledgeBaseManager.MergeSnapshot(unreadable, Snapshot());

        Assert.True(merged.IsReadOnly);
        Assert.Equal(1, KnowledgeBaseManager.HostCount(merged));
    }

    /// <summary>
    /// A reader that shares delete (a scanner, an indexer, a backup) must not fail the save, and
    /// the file must end up with the protected ACL, as the other local data files do.
    /// </summary>
    [Fact]
    public async Task SaveAsync_FileHeldOpenWithDeleteSharing_IsReplacedAndRestricted()
    {
        await KnowledgeBaseManager.SaveAsync(KnowledgeBaseWithOneHost(), _path);

        using (new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            await KnowledgeBaseManager.SaveAsync(KnowledgeBaseManager.CreateEmpty(), _path);
        }

        Assert.Equal(0, KnowledgeBaseManager.HostCount(await KnowledgeBaseManager.LoadAsync(_path)));
        Assert.True(new FileInfo(_path).GetAccessControl().AreAccessRulesProtected);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp*"));
    }

    private static NetworkKnowledgeBase KnowledgeBaseWithOneHost() =>
        KnowledgeBaseManager.MergeSnapshot(KnowledgeBaseManager.CreateEmpty(), Snapshot());

    private static NetworkScanSnapshot Snapshot()
    {
        var host = new HostScanResult("10.0.0.5", "srv", true, 0, [], null, []);
        return new NetworkScanSnapshot(
            "test",
            ScanTime,
            new ScanProfile("10.0.0.0/24", ScanDepth.Quick, null, 50, 2000, false, false),
            null,
            TimeSpan.Zero,
            [host]);
    }
}
