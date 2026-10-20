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

using System.Collections.Specialized;
using Heimdall.App.ViewModels;
using Heimdall.App.ViewModels.CommandLibrary;
using TwinShell.Core.Enums;
using TwinShell.Core.Models;
using ActionModel = TwinShell.Core.Models.Action;

namespace Heimdall.App.Tests;

/// <summary>
/// Pins the command line each action row shows under its title: which template it comes
/// from, how it is shortened, and that a filter change re-reads it.
/// </summary>
public sealed class CommandLibraryCommandPreviewTests
{
    private const string WindowsPattern = "Get-Service -Name {name}";
    private const string LinuxPattern = "systemctl status {name}";

    [Fact]
    public async Task ThePreviewFollowsThePlatformFilter()
    {
        using var viewModel = await CreateAsync(DualAction());
        var entry = viewModel.AllEntries.Single();

        viewModel.PlatformFilterIndex = CommandLibraryViewModel.PlatformFilterAll;
        Assert.Equal(WindowsPattern, entry.CommandPreview);

        viewModel.PlatformFilterIndex = CommandLibraryViewModel.PlatformFilterLinux;
        Assert.Equal(LinuxPattern, entry.CommandPreview);
    }

    [Fact]
    public async Task AnActionWithOneTemplate_ShowsItWhateverTheFilter()
    {
        using var viewModel = await CreateAsync(
            CommandLibraryTestHelpers.CreateLinuxAction("linux-only", "Linux only", LinuxPattern));
        var entry = viewModel.AllEntries.Single();

        viewModel.PlatformFilterIndex = CommandLibraryViewModel.PlatformFilterWindows;

        Assert.Equal(LinuxPattern, entry.CommandPreview);
    }

    [Fact]
    public async Task ALongPattern_IsCutAtTheLimitWithTheGlyph()
    {
        var longPattern = new string('x', CommandPreviewText.MaxLength + 24);
        using var viewModel = await CreateAsync(
            CommandLibraryTestHelpers.CreateLinuxAction("long", "Long", longPattern));

        var preview = viewModel.AllEntries.Single().CommandPreview;

        Assert.Equal(new string('x', CommandPreviewText.MaxLength) + CommandPreviewText.EllipsisGlyph, preview);
    }

    [Fact]
    public void AMultiLinePattern_IsShownOnOneLine()
    {
        Assert.Equal("first second", CommandPreviewText.ToPreview("first\r\nsecond"));
    }

    // The rows read the preview at bind time; a filter change has to make the list re-bind
    // them, which is the reset the refresh raises.
    [Fact]
    public async Task ChangingThePlatformFilter_ResetsTheListSoRowsRebind()
    {
        using var viewModel = await CreateAsync(DualAction());
        var resets = 0;
        ((INotifyCollectionChanged)viewModel.ActionsView!).CollectionChanged += (_, args) =>
        {
            if (args.Action == NotifyCollectionChangedAction.Reset)
            {
                resets++;
            }
        };

        viewModel.PlatformFilterIndex = CommandLibraryViewModel.PlatformFilterLinux;

        Assert.True(resets > 0);
    }

    private static ActionModel DualAction() => new()
    {
        Id = "dual",
        Title = "Service status",
        Category = "Ops",
        Platform = Platform.Both,
        WindowsCommandTemplate = new CommandTemplate
        {
            Id = "dual-win",
            Name = "Windows",
            Platform = Platform.Windows,
            CommandPattern = WindowsPattern
        },
        LinuxCommandTemplate = new CommandTemplate
        {
            Id = "dual-linux",
            Name = "Linux",
            Platform = Platform.Linux,
            CommandPattern = LinuxPattern
        }
    };

    private static async Task<CommandLibraryViewModel> CreateAsync(params ActionModel[] actions)
    {
        var viewModel = new CommandLibraryViewModel(
            CommandLibraryTestHelpers.CreateResolverServiceProvider(actions),
            configManager: null!,
            await CommandLibraryTestHelpers.CreateAppLocalizerAsync(),
            new SilentDialogService(),
            gitSyncService: null!,
            transferService: null!);

        await viewModel.InitializeAsync(targetHost: null);
        return viewModel;
    }
}
