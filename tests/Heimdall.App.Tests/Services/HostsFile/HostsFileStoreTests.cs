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
using System.Text;
using FluentAssertions;
using Heimdall.App.Services.HostsFile;
using Microsoft.Extensions.Time.Testing;

namespace Heimdall.App.Tests.Services.HostsFile;

/// <summary>
/// Saving through the store, always on a temporary file: the backup is written before the
/// overwrite, and nothing is written when the backup fails, a row is invalid, or another
/// program changed the file after it was loaded.
/// </summary>
public sealed class HostsFileStoreTests : IDisposable
{
    private const string Original = "# header\r\n127.0.0.1 localhost\r\n# between\r\n10.0.0.5 lab\r\n";

    private readonly string _directory;
    private readonly string _hostsPath;
    private readonly string _backupDirectory;
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero));

    public HostsFileStoreTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "heimdall-hosts-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _hostsPath = Path.Combine(_directory, "hosts");
        _backupDirectory = Path.Combine(_directory, "data", "hosts-backups");
        File.WriteAllBytes(_hostsPath, Encoding.UTF8.GetBytes(Original));
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }

    private HostsFileStore CreateStore() => new(_hostsPath, _backupDirectory, _clock);

    private string OriginalBackupPath => Path.Combine(_backupDirectory, HostsFileStore.OriginalBackupFileName);

    [Fact]
    public void Save_Copies_The_Current_File_To_The_Backup_Folder_Before_Overwriting()
    {
        var store = CreateStore();
        var document = store.Load();
        document.Entries[1].IpAddress = "10.0.0.6";

        var result = store.Save(document);

        result.Status.Should().Be(HostsFileSaveStatus.Saved);
        File.ReadAllText(store.ListTimestampedBackups().Should().ContainSingle().Subject).Should().Be(Original);
        File.ReadAllText(OriginalBackupPath).Should().Be(Original);
        File.ReadAllText(_hostsPath).Should().Be(
            "# header\r\n127.0.0.1 localhost\r\n# between\r\n10.0.0.6\tlab\r\n");
        Directory.GetFiles(_directory).Should().ContainSingle("nothing is written next to the hosts file");
        document.IsDirty.Should().BeFalse();
    }

    [Fact]
    public void Original_Backup_Is_Written_Once_And_Never_Replaced()
    {
        var store = CreateStore();
        var document = store.Load();

        document.Entries[0].Hostname = "first";
        store.Save(document).Status.Should().Be(HostsFileSaveStatus.Saved);
        _clock.Advance(TimeSpan.FromSeconds(1));
        document.Entries[0].Hostname = "second";
        store.Save(document).Status.Should().Be(HostsFileSaveStatus.Saved);

        File.ReadAllText(OriginalBackupPath).Should().Be(Original);
        File.ReadAllText(store.ListTimestampedBackups()[^1]).Should().Contain("127.0.0.1\tfirst");
    }

    [Fact]
    public void Only_The_Newest_Timestamped_Backups_Are_Kept()
    {
        var store = CreateStore();
        var document = store.Load();
        int saves = HostsFileStore.MaxTimestampedBackups + 3;

        for (int i = 0; i < saves; i++)
        {
            document.Entries[0].Hostname = "host" + i;
            store.Save(document).Status.Should().Be(HostsFileSaveStatus.Saved);
            _clock.Advance(TimeSpan.FromSeconds(1));
        }

        var backups = store.ListTimestampedBackups();
        backups.Should().HaveCount(HostsFileStore.MaxTimestampedBackups);
        // The newest backup holds the file as the next-to-last save wrote it.
        File.ReadAllText(backups[^1]).Should().Contain("\thost" + (saves - 2));
        File.ReadAllText(OriginalBackupPath).Should().Be(Original);
    }

    [Fact]
    public void Save_Without_Edits_Writes_The_Same_Bytes()
    {
        var store = CreateStore();
        var before = File.ReadAllBytes(_hostsPath);

        store.Save(store.Load()).Status.Should().Be(HostsFileSaveStatus.Saved);

        File.ReadAllBytes(_hostsPath).Should().Equal(before);
    }

    [Fact]
    public void Save_Is_Refused_When_The_Backup_Cannot_Be_Written()
    {
        // A file where the backup folder goes makes creating it fail deterministically.
        Directory.CreateDirectory(Path.GetDirectoryName(_backupDirectory)!);
        File.WriteAllText(_backupDirectory, "in the way");
        var store = CreateStore();
        var document = store.Load();
        document.Entries[0].Hostname = "changed";

        var result = store.Save(document);

        result.Status.Should().Be(HostsFileSaveStatus.BackupFailed);
        result.ErrorMessage.Should().NotBeNullOrWhiteSpace();
        File.ReadAllText(_hostsPath).Should().Be(Original);
        document.IsDirty.Should().BeTrue();
    }

    [Fact]
    public void Save_Is_Refused_When_Another_Program_Changed_The_File_After_Load()
    {
        var store = CreateStore();
        var document = store.Load();
        document.Entries[0].Hostname = "mine";
        const string theirs = Original + "# Added by Docker Desktop\r\n10.1.2.3 host.docker.internal\r\n";
        File.WriteAllText(_hostsPath, theirs);

        var result = store.Save(document);

        result.Status.Should().Be(HostsFileSaveStatus.ExternallyModified);
        File.ReadAllText(_hostsPath).Should().Be(theirs);
        Directory.Exists(_backupDirectory).Should().BeFalse();
        document.IsDirty.Should().BeTrue();
    }

    [Fact]
    public void A_Second_Save_Compares_Against_What_The_First_One_Wrote()
    {
        var store = CreateStore();
        var document = store.Load();
        document.Entries[0].Hostname = "one";
        store.Save(document).Status.Should().Be(HostsFileSaveStatus.Saved);
        _clock.Advance(TimeSpan.FromSeconds(1));

        document.Entries[0].Hostname = "two";

        store.Save(document).Status.Should().Be(HostsFileSaveStatus.Saved);
    }

    [Fact]
    public void File_Created_By_Another_Program_After_A_Missing_Load_Is_Not_Overwritten()
    {
        var path = Path.Combine(_directory, "absent-hosts");
        var store = new HostsFileStore(path, _backupDirectory, _clock);
        var document = HostsFileDocument.CreateEmpty();
        var entry = document.AddEntry();
        entry.IpAddress = "10.0.0.1";
        entry.Hostname = "a";
        File.WriteAllText(path, "10.9.9.9 theirs\r\n");

        store.Save(document).Status.Should().Be(HostsFileSaveStatus.ExternallyModified);

        File.ReadAllText(path).Should().Be("10.9.9.9 theirs\r\n");
    }

    [Fact]
    public void Row_With_An_Empty_Field_Blocks_The_Save()
    {
        var store = CreateStore();
        var document = store.Load();
        var added = document.AddEntry();
        added.IpAddress = "10.0.0.9";

        var result = store.Save(document);

        result.Status.Should().Be(HostsFileSaveStatus.InvalidEntries);
        result.Problems.Should().ContainSingle()
            .Which.Problem.Should().Be(HostEntryProblem.MissingHostname);
        File.ReadAllText(_hostsPath).Should().Be(Original);
        Directory.Exists(_backupDirectory).Should().BeFalse();
    }

    [Theory]
    [InlineData("127.0.0.256")]
    [InlineData("127.1")]
    [InlineData("0x7f.0.0.1")]
    public void Invalid_Address_Blocks_The_Save(string ip)
    {
        var store = CreateStore();
        var document = store.Load();
        document.Entries[0].IpAddress = ip;

        var result = store.Save(document);

        result.Status.Should().Be(HostsFileSaveStatus.InvalidEntries);
        result.Problems.Should().ContainSingle()
            .Which.Problem.Should().Be(HostEntryProblem.InvalidIpAddress);
        File.ReadAllText(_hostsPath).Should().Be(Original);
    }

    [Fact]
    public void Odd_Lines_Already_In_The_File_Do_Not_Block_The_Save()
    {
        const string withOddLines = "127.1 shorthand\r\n0x7f.0.0.1 hex\r\n1 bare\r\n127.0.0.1 localhost\r\n";
        File.WriteAllText(_hostsPath, withOddLines);
        var store = CreateStore();
        var document = store.Load();
        document.Entries.Should().ContainSingle("the shorthand addresses stay raw lines");
        document.Entries[0].Comment = "edited";

        store.Save(document).Status.Should().Be(HostsFileSaveStatus.Saved);

        File.ReadAllText(_hostsPath).Should().Be(
            "127.1 shorthand\r\n0x7f.0.0.1 hex\r\n1 bare\r\n127.0.0.1\tlocalhost\t# edited\r\n");
    }

    [Fact]
    public void Edited_Compressed_Ipv6_Entry_Saves()
    {
        var store = CreateStore();
        var document = store.Load();
        document.Entries[0].IpAddress = "fe80::1";

        store.Save(document).Status.Should().Be(HostsFileSaveStatus.Saved);

        File.ReadAllText(_hostsPath).Should().Contain("fe80::1\tlocalhost");
    }

    [Fact]
    public void Missing_File_Is_Created_Without_A_Backup()
    {
        var path = Path.Combine(_directory, "new-hosts");
        var store = new HostsFileStore(path, _backupDirectory, _clock);
        var document = HostsFileDocument.CreateEmpty();
        var entry = document.AddEntry();
        entry.IpAddress = "10.0.0.1";
        entry.Hostname = "a";

        store.Save(document).Status.Should().Be(HostsFileSaveStatus.Saved);

        File.ReadAllBytes(path).Should().Equal(Encoding.ASCII.GetBytes("10.0.0.1\ta\r\n"));
        Directory.Exists(_backupDirectory).Should().BeFalse();
    }
}
