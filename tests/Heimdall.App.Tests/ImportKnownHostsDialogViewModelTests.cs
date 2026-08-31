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

using Heimdall.App.Services.Import;
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.Core.Ssh;
using Heimdall.Ssh;
using KnownHostsImporter = Heimdall.App.Services.Import.KnownHostsImporter;

namespace Heimdall.App.Tests;

public sealed class ImportKnownHostsDialogViewModelTests
{
    [Fact]
    public async Task Initialize_ConflictItems_IsSelectableFalse_AndIsSelectedFalse()
    {
        var viewModel = await CreateViewModelAsync();

        await viewModel.InitializeAsync(new KnownHostsImportPreview(
        [
            new KnownHostsPreviewRow(
                CreateCandidate("host", 22, "SHA256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"),
                KnownHostsCandidateStatus.Conflict,
                null)
        ],
        []));

        var item = Assert.Single(viewModel.Items);
        Assert.False(item.IsSelectable);
        Assert.False(item.IsSelected);
    }

    [Fact]
    public async Task ConfirmCommand_FiltersOutConflict_BeforeCallingImporter()
    {
        var viewModel = await CreateViewModelAsync();

        await viewModel.InitializeAsync(new KnownHostsImportPreview(
        [
            new KnownHostsPreviewRow(
                CreateCandidate("conflict", 22, "SHA256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"),
                KnownHostsCandidateStatus.Conflict,
                null),
            new KnownHostsPreviewRow(
                CreateCandidate("new", 22, "SHA256:BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB"),
                KnownHostsCandidateStatus.New,
                null)
        ],
        []));

        await viewModel.ConfirmCommand.ExecuteAsync(null);

        Assert.NotNull(viewModel.Result);
        Assert.Equal(1, viewModel.Result!.Imported);
        Assert.Equal(0, viewModel.Result!.SkippedConflict);
        Assert.Equal(0, viewModel.Result!.SkippedExisting);
    }

    [Fact]
    public void FingerprintDisplay_TruncatesCorrectly_AndTooltipCarriesFull()
    {
        const string fullFingerprint = "SHA256:abcdefghijklmnopqrstuvwxyz0123456789ABCDEF=";
        var item = new KnownHostItemViewModel(
            CreateCandidate("host", 22, fullFingerprint),
            "host",
            22,
            fullFingerprint,
            KnownHostsCandidateStatus.New,
            "New",
            string.Empty,
            "Known host host port 22, New");

        Assert.Equal(fullFingerprint, item.Fingerprint);
        Assert.Equal("SHA256:abcdefghij…789ABCDEF=", item.FingerprintDisplay);
    }

    private static async Task<ImportKnownHostsDialogViewModel> CreateViewModelAsync()
    {
        var localizer = await CommandLibraryTestHelpers.CreateAppLocalizerAsync();
        var importer = new KnownHostsImporter(new InMemoryConfigManager(), new HostKeyStore());
        return new ImportKnownHostsDialogViewModel(importer, localizer);
    }

    private static KnownHostsImportCandidate CreateCandidate(string host, int port, string fingerprint)
    {
        return new KnownHostsImportCandidate
        {
            Host = host,
            Port = port,
            Fingerprint = fingerprint,
            SourceLineNumber = 1
        };
    }
}
