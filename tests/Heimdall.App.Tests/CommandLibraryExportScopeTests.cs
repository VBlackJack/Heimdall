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
using Heimdall.App.Services.Import;
using Heimdall.App.ViewModels;
using Heimdall.App.ViewModels.CommandLibrary;
using TwinShell.Core.Interfaces;
using ActionModel = TwinShell.Core.Models.Action;

namespace Heimdall.App.Tests;

/// <summary>
/// Pins what an export writes for each answer to "which commands should the file contain".
/// </summary>
public sealed class CommandLibraryExportScopeTests
{
    private const string SeededTitle = "Seeded tail";
    private const string MineTitle = "My grep";
    private const string OtherMineTitle = "My uptime";

    [Fact]
    public async Task MyActions_ExcludesASeededAction()
    {
        var transfer = new RecordingTransferService();
        using var viewModel = await CreateAsync(CommandLibraryExportScope.Mine, transfer);

        await viewModel.ExportAsync();

        Assert.Equal([MineTitle, OtherMineTitle], transfer.Titles);
    }

    [Fact]
    public async Task CurrentFilter_FollowsTheActiveSearch()
    {
        var transfer = new RecordingTransferService();
        using var viewModel = await CreateAsync(CommandLibraryExportScope.CurrentFilter, transfer);
        await viewModel.ApplySearchAsync("grep");

        await viewModel.ExportAsync();

        Assert.Equal([MineTitle], transfer.Titles);
    }

    [Fact]
    public async Task Everything_WritesEveryAction()
    {
        var transfer = new RecordingTransferService();
        using var viewModel = await CreateAsync(CommandLibraryExportScope.Everything, transfer);

        await viewModel.ExportAsync();

        Assert.Equal(3, transfer.Titles.Count);
    }

    [Fact]
    public async Task Cancelling_TheChoice_ExportsNothingAndAsksNothingElse()
    {
        var transfer = new RecordingTransferService();
        var dialog = new SilentDialogService { ConfirmResult = true, ChoiceResult = null };
        using var viewModel = await CreateAsync(dialog, transfer);
        var pathAsked = false;
        viewModel.ShowSaveFileDialog = (_, _) => { pathAsked = true; return "unused.json"; };

        await viewModel.ExportAsync();

        Assert.False(pathAsked);
        Assert.Empty(dialog.ConfirmMessages);
        Assert.Empty(transfer.Titles);
    }

    // The envelope says how many it holds; that number has to be what was written, not the size
    // of the catalogue the choice narrowed.
    [Fact]
    public async Task TheWrittenEnvelope_CountsWhatItHolds()
    {
        var path = Path.Combine(Path.GetTempPath(), "heimdall-export-scope-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            using var viewModel = await CreateAsync(CommandLibraryExportScope.Mine, new CommandLibraryTransferService());
            viewModel.ShowSaveFileDialog = (_, _) => path;

            await viewModel.ExportAsync();

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            var written = document.RootElement.GetProperty("actions").GetArrayLength();
            Assert.Equal(2, written);
            Assert.Equal(written, document.RootElement.GetProperty("totalActions").GetInt32());
        }
        finally
        {
            File.Delete(path);
        }
    }

    // The secret warning is still shown, once, after the choice.
    [Fact]
    public async Task TheSecretWarning_IsShownOnceAfterTheChoice()
    {
        var dialog = new SilentDialogService { ConfirmResult = true, ChoiceResult = (int)CommandLibraryExportScope.Mine };
        using var viewModel = await CreateAsync(dialog, new RecordingTransferService());

        await viewModel.ExportAsync();

        Assert.Single(dialog.Choices);
        Assert.Single(dialog.ConfirmMessages);
    }

    private static Task<CommandLibraryViewModel> CreateAsync(
        CommandLibraryExportScope scope,
        ICommandLibraryTransferService transfer)
        => CreateAsync(new SilentDialogService { ConfirmResult = true, ChoiceResult = (int)scope }, transfer);

    private static async Task<CommandLibraryViewModel> CreateAsync(
        SilentDialogService dialog,
        ICommandLibraryTransferService transfer)
    {
        var seeded = CommandLibraryTestHelpers.CreateLinuxAction("seeded", SeededTitle, "tail -f /var/log/syslog");
        var mine = CommandLibraryTestHelpers.CreateLinuxAction("mine", MineTitle, "grep error /var/log/syslog");
        mine.IsUserCreated = true;
        var otherMine = CommandLibraryTestHelpers.CreateLinuxAction("other", OtherMineTitle, "uptime");
        otherMine.IsUserCreated = true;

        var viewModel = new CommandLibraryViewModel(
            CommandLibraryTestHelpers.CreateResolverServiceProvider(seeded, mine, otherMine),
            configManager: null!,
            await CommandLibraryTestHelpers.CreateAppLocalizerAsync(),
            dialog,
            gitSyncService: null!,
            transfer);

        await viewModel.InitializeAsync(targetHost: null);
        viewModel.ShowSaveFileDialog = (_, _) => "export.json";
        return viewModel;
    }

    private sealed class RecordingTransferService : ICommandLibraryTransferService
    {
        public List<string> Titles { get; } = [];

        public Task<int> ExportAsync(IReadOnlyList<ActionModel> actions, string path)
        {
            Titles.AddRange(actions.Select(action => action.Title));
            return Task.FromResult(actions.Count);
        }

        public Task<CommandLibraryImportResult> ImportAsync(IActionService actionService, string path)
            => Task.FromResult(CommandLibraryImportResult.InvalidFormat());
    }
}
