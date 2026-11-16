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
using System.Reflection;
using System.Text.Json;
using Heimdall.App.ViewModels;
using Heimdall.Core.Localization;
using Heimdall.Sftp;

namespace Heimdall.App.Tests;

public sealed class PrivilegedPathRefusalMessageTests
{
    private const string PathLinkKey = "SftpRootRefusedPathLink";
    private const string HardLinkKey = "SftpRootRefusedHardLink";

    [Theory]
    [InlineData(EntryRefusalReason.PathLink, PathLinkKey)]
    [InlineData(EntryRefusalReason.HardLink, HardLinkKey)]
    public async Task DescribeTransferError_MapsEachReasonToItsKeyWithThePath(EntryRefusalReason reason, string key)
    {
        (EmbeddedSftpViewModel viewModel, LocalizationManager localizer) = await CreateAsync();

        string message = viewModel.DescribeTransferError(
            new PrivilegedTransferRefusedException(reason, "/srv/www/site.conf"));

        Assert.Equal(localizer.Format(key, "/srv/www/site.conf"), message);
        Assert.NotEqual(key, message);
    }

    [Theory]
    [InlineData(EntryRefusalReason.PathLink, PathLinkKey)]
    [InlineData(EntryRefusalReason.HardLink, HardLinkKey)]
    public async Task DescribeTransferRefusal_UsesTheSameKeys(EntryRefusalReason reason, string key)
    {
        (EmbeddedSftpViewModel viewModel, LocalizationManager localizer) = await CreateAsync();

        Assert.Equal(localizer.Format(key, "data"), viewModel.DescribeTransferRefusal(reason, "data"));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("fr")]
    [InlineData("es")]
    public void BothKeysExistInEveryLanguageWithTheirPlaceholder(string language)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "locales", language + ".json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));

        foreach (string key in new[] { PathLinkKey, HardLinkKey })
        {
            string value = document.RootElement.GetProperty(key).GetString()!;
            Assert.Contains("{0}", value, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EnsureSudoSucceeded_ThePathMarkerWinsOverASudoLikeMessage()
    {
        string stderr = "sorry, try again.\n"
            + PrivilegedFileCommands.TransferRefusedMarker + " pathlink\n";

        PrivilegedTransferRefusedException refusal = Assert.Throws<PrivilegedTransferRefusedException>(
            () => EmbeddedSftpViewModel.EnsureSudoSucceeded(73, stderr, "atomic write", "/srv/a"));

        Assert.Equal(EntryRefusalReason.PathLink, refusal.Reason);
        Assert.Equal("/srv/a", refusal.RemotePath);
    }

    [Fact]
    public void EnsureSudoSucceeded_AReadRefusalUsesStatus74()
    {
        string stderr = PrivilegedFileCommands.TransferRefusedMarker + " hardlink\n";

        PrivilegedTransferRefusedException refusal = Assert.Throws<PrivilegedTransferRefusedException>(
            () => EmbeddedSftpViewModel.EnsureSudoSucceeded(74, stderr, "base64", "/srv/b"));

        Assert.Equal(EntryRefusalReason.HardLink, refusal.Reason);
    }

    [Fact]
    public void EnsureSudoSucceeded_TheMarkerWithAnotherStatusIsNotARefusal()
    {
        string stderr = PrivilegedFileCommands.TransferRefusedMarker + " pathlink\n";

        Assert.IsNotType<PrivilegedTransferRefusedException>(
            Record.Exception(() => EmbeddedSftpViewModel.EnsureSudoSucceeded(1, stderr, "atomic write", "/srv/a")));
    }

    [Theory]
    [InlineData(EntryRefusalReason.PathLink)]
    [InlineData(EntryRefusalReason.HardLink)]
    public void TheRefusalIsPerEntryAndNeverBatchFatal(EntryRefusalReason reason)
    {
        PrivilegedTransferRefusedException refusal = new(reason, "/srv/a");
        SudoBatchGuard guard = new();

        guard.Record(refusal);

        Assert.False(SudoBatchGuard.IsBatchFatal(refusal));
        Assert.False(guard.IsStopped);
        Assert.False(guard.StoppedOnAuthentication);
    }

    private static async Task<(EmbeddedSftpViewModel ViewModel, LocalizationManager Localizer)> CreateAsync()
    {
        LocalizationManager localizer = new();
        await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "en");
        EmbeddedSftpViewModel viewModel = new(new FakeUiDispatcher());
        FieldInfo field = typeof(EmbeddedSftpViewModel).GetField(
            "_localizer",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(viewModel, localizer);
        return (viewModel, localizer);
    }
}
