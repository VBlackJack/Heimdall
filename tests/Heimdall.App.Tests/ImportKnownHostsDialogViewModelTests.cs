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

    // B-08: FileTooLarge and FileReadError had no locale key, so the dialog showed the
    // raw enum name; they are file-level diagnostics carrying line 0, which must not be
    // rendered as a line number either.
    [Theory]
    [InlineData(KnownHostsDiagnosticCode.FileTooLarge, "1234567 bytes")]
    [InlineData(KnownHostsDiagnosticCode.FileReadError, "access denied")]
    public async Task Initialize_FileLevelDiagnosticWithLineZero_IsLocalizedWithoutALineNumber(
        KnownHostsDiagnosticCode code,
        string context)
    {
        ImportKnownHostsDialogViewModel viewModel = await CreateViewModelAsync();

        await viewModel.InitializeAsync(new KnownHostsImportPreview(
            [],
            [new KnownHostsImportDiagnostic(KnownHostsDiagnosticLevel.Warning, 0, code, context)]));

        KnownHostDiagnosticViewModel diagnostic = Assert.Single(viewModel.Diagnostics);
        Assert.NotEqual(code.ToString(), diagnostic.Message);
        Assert.Contains(context, diagnostic.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("0", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Initialize_LegacyKeyTypeDiagnostic_IsLocalizedWithLineAndKeyType()
    {
        ImportKnownHostsDialogViewModel viewModel = await CreateViewModelAsync();

        await viewModel.InitializeAsync(new KnownHostsImportPreview(
            [],
            [new KnownHostsImportDiagnostic(KnownHostsDiagnosticLevel.Info, 7, KnownHostsDiagnosticCode.LegacyKeyType, "ssh-dss")]));

        KnownHostDiagnosticViewModel diagnostic = Assert.Single(viewModel.Diagnostics);
        Assert.NotEqual(KnownHostsDiagnosticCode.LegacyKeyType.ToString(), diagnostic.Message);
        Assert.Contains("7", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("ssh-dss", diagnostic.Message, StringComparison.Ordinal);
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
        Assert.Equal("SHA256:abcdefghij...789ABCDEF=", item.FingerprintDisplay);
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
