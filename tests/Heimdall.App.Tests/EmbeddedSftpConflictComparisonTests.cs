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
using Heimdall.App.ViewModels;
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.Sftp;

namespace Heimdall.App.Tests;

/// <summary>
/// The SFTP browser hands the conflict dialog the size and date of both sides of a collision, so
/// the dialog can say which file is newer.
/// </summary>
public sealed class EmbeddedSftpConflictComparisonTests
{
    private static readonly DateTime Noon = new(2026, 5, 4, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task ADownloadCollision_CarriesTheRemoteAndTheLocalSizeAndDate()
    {
        using SftpTestKit.ScratchFolder scratch = new();
        string existing = Path.Combine(scratch.Path, "d", "a.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(existing)!);
        await File.WriteAllBytesAsync(existing, new byte[10]);
        File.SetLastWriteTimeUtc(existing, Noon);
        ScriptedRemoteBrowser browser = new();
        browser.AddDirectory("/srv/d", SftpTestKit.File("/srv/d/a.bin", 99) with { LastModified = Noon.AddDays(3) });
        List<FileConflictRowViewModel> seen = [];
        ScriptedConflictPresenter presenter = new(dialog =>
        {
            seen.AddRange(dialog.Rows);
            return null;
        });
        EmbeddedSftpViewModel viewModel = SftpTestKit.CreateViewModel(browser, presenter);

        await viewModel.DownloadFilesAsync([SftpTestKit.Directory("/srv/d")], scratch.Path);

        FileConflictRowViewModel row = Assert.Single(seen);
        Assert.True(row.HasComparison);
        Assert.True(row.IsIncomingNewer);
        Assert.Empty(browser.Downloads);
    }

    [Fact]
    public async Task AnUploadCollision_CarriesTheLocalAndTheRemoteSizeAndDate()
    {
        using SftpTestKit.ScratchFolder scratch = new();
        string local = Path.Combine(scratch.Path, "a.txt");
        await File.WriteAllTextAsync(local, "payload");
        File.SetLastWriteTimeUtc(local, Noon.AddDays(-5));
        ScriptedRemoteBrowser browser = new();
        browser.AddDirectory("/srv", SftpTestKit.File("/srv/a.txt", 3) with { LastModified = Noon });
        List<FileConflictRowViewModel> seen = [];
        ScriptedConflictPresenter presenter = new(dialog =>
        {
            seen.AddRange(dialog.Rows);
            return null;
        });
        EmbeddedSftpViewModel viewModel = SftpTestKit.CreateViewModel(browser, presenter);

        await viewModel.UploadEntriesAsync([local], "/srv");

        FileConflictRowViewModel row = Assert.Single(seen);
        Assert.True(row.HasComparison);
        Assert.False(row.IsIncomingNewer);
        Assert.Empty(browser.Uploads);
    }
}
