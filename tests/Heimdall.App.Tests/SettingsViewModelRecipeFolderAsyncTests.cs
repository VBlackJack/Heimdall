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

using Heimdall.App.Services;
using Heimdall.App.ViewModels;
using Heimdall.Core.Localization;
using Heimdall.Core.Recipes;

namespace Heimdall.App.Tests;

/// <summary>
/// A machine whose folder resolution can be held on a gate, can fail, and records the thread it ran on and
/// the paths it was asked about.
/// </summary>
internal sealed class GatedFolderMachine : IRecipeMachine
{
    public ManualResetEventSlim Entered { get; } = new(false);

    public ManualResetEventSlim Gate { get; } = new(true);

    public int? ResolveThreadId { get; private set; }

    public List<string> Resolved { get; } = [];

    public Exception? Failure { get; set; }

    public Dictionary<string, string?> Finals { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string WindowsDirectory => @"C:\Windows";

    public string ProgramFilesDirectory => @"C:\Program Files";

    public string ProgramFilesX86Directory => @"C:\Program Files (x86)";

    public IReadOnlyList<string> PathEntries => [];

    public bool FileExists(string path) => false;

    public string? ResolveFinalPath(string path)
    {
        ResolveThreadId = Environment.CurrentManagedThreadId;
        lock (Resolved)
        {
            Resolved.Add(path);
        }

        Entered.Set();
        if (!Gate.Wait(SettingsViewModelTests.AsyncFolderWait))
        {
            throw new TimeoutException("The gate was never released.");
        }

        if (Failure is not null)
        {
            throw Failure;
        }

        return Finals.TryGetValue(path, out string? final) ? final : null;
    }

    public string? ReadOriginalFileName(string path) => null;

    public RecipeItemAccess? ReadAccess(string path) => null;
}

public sealed partial class SettingsViewModelTests
{
    internal static readonly TimeSpan AsyncFolderWait = TimeSpan.FromSeconds(30);

    private static async Task<(SettingsViewModel ViewModel, GatedFolderMachine Machine, LocalizationManager Localizer)> GatedFolderPanelAsync()
    {
        LocalizationManager localizer = await LoadLocalizerForToolPacksAsync("en");
        FakeConfigManager config = new();
        SettingsViewModel viewModel = CreateViewModel(config, localizer: localizer);
        viewModel.LoadFromSettings(config.Settings);
        GatedFolderMachine machine = new();
        viewModel.RecipeFolderMachine = machine;
        return (viewModel, machine, localizer);
    }

    [Fact]
    public async Task TheFinalPathOfAFolderIsResolvedOffTheCallingThread()
    {
        (SettingsViewModel viewModel, GatedFolderMachine machine, _) = await GatedFolderPanelAsync();
        int callingThread = Environment.CurrentManagedThreadId;

        bool added = await viewModel.TryAddRecipeProgramFolderAsync(@"D:\Tools");

        Assert.True(added);
        Assert.NotNull(machine.ResolveThreadId);
        Assert.NotEqual(callingThread, machine.ResolveThreadId);
        Assert.Equal([@"D:\Tools"], viewModel.RecipeProgramFolders);
    }

    [Fact]
    public async Task ARefusalBeforeTheResolveNeverAsksTheMachineWhereTheFolderLeads()
    {
        (SettingsViewModel viewModel, GatedFolderMachine machine, _) = await GatedFolderPanelAsync();

        bool added = await viewModel.TryAddRecipeProgramFolderAsync(@"\\server\share\tools");

        Assert.False(added);
        Assert.Empty(machine.Resolved);
        Assert.True(viewModel.HasRecipeProgramFolderStatus);
        Assert.False(viewModel.HasRecipeProgramFolderChecking);
    }

    [Fact]
    public async Task AFolderAlreadyInTheListIsRefusedBeforeTheResolve()
    {
        (SettingsViewModel viewModel, GatedFolderMachine machine, _) = await GatedFolderPanelAsync();
        Assert.True(viewModel.TryAddRecipeProgramFolder(@"D:\Tools"));
        machine.Resolved.Clear();

        bool added = await viewModel.TryAddRecipeProgramFolderAsync(@"d:\tools");

        Assert.False(added);
        Assert.Empty(machine.Resolved);
        Assert.Equal([@"D:\Tools"], viewModel.RecipeProgramFolders);
    }

    [Fact]
    public async Task TheSameFolderAddedDuringTheWaitIsRefusedAsADuplicateAndListedOnce()
    {
        (SettingsViewModel viewModel, GatedFolderMachine machine, LocalizationManager localizer) = await GatedFolderPanelAsync();
        machine.Gate.Reset();

        Task<bool> first = viewModel.TryAddRecipeProgramFolderAsync(@"D:\Tools");
        Assert.True(machine.Entered.Wait(AsyncFolderWait));

        // The same folder lands in the list (another add, an import) while the first one is still being resolved.
        viewModel.RecipeProgramFolders = [@"D:\Tools"];
        machine.Gate.Set();
        bool added = await first;

        Assert.False(added);
        Assert.Equal([@"D:\Tools"], viewModel.RecipeProgramFolders);
        Assert.Equal(
            RecipeImportText.Describe(localizer, RecipeFolderRefusal.AlreadyListed, RecipeFolderRule.MaxFolders),
            viewModel.RecipeProgramFolderStatus);
    }

    [Fact]
    public async Task APanelDisposedDuringTheWaitAddsNothing()
    {
        (SettingsViewModel viewModel, GatedFolderMachine machine, _) = await GatedFolderPanelAsync();
        machine.Gate.Reset();

        Task<bool> pending = viewModel.TryAddRecipeProgramFolderAsync(@"D:\Tools");
        Assert.True(machine.Entered.Wait(AsyncFolderWait));
        viewModel.Dispose();
        machine.Gate.Set();
        bool added = await pending;

        Assert.False(added);
        Assert.Empty(viewModel.RecipeProgramFolders);
    }

    [Fact]
    public async Task TheCheckingTextIsShownDuringTheWaitAndClearedAfter()
    {
        (SettingsViewModel viewModel, GatedFolderMachine machine, LocalizationManager localizer) = await GatedFolderPanelAsync();
        machine.Gate.Reset();
        Assert.False(viewModel.HasRecipeProgramFolderChecking);

        Task<bool> pending = viewModel.TryAddRecipeProgramFolderAsync(@"D:\Tools");
        Assert.True(machine.Entered.Wait(AsyncFolderWait));

        Assert.True(viewModel.HasRecipeProgramFolderChecking);
        Assert.Equal(localizer["RecipeFolderChecking"], viewModel.RecipeProgramFolderChecking);
        Assert.StartsWith("Checking the folder", viewModel.RecipeProgramFolderChecking, StringComparison.Ordinal);
        Assert.False(viewModel.HasRecipeProgramFolderStatus);

        machine.Gate.Set();
        Assert.True(await pending);

        Assert.False(viewModel.HasRecipeProgramFolderChecking);
        Assert.Equal(string.Empty, viewModel.RecipeProgramFolderChecking);
    }

    [Fact]
    public async Task ALanguageSwitchDuringTheWaitRewordsTheCheckingText()
    {
        (SettingsViewModel viewModel, GatedFolderMachine machine, LocalizationManager localizer) = await GatedFolderPanelAsync();
        machine.Gate.Reset();
        string english = localizer["RecipeFolderChecking"];

        Task<bool> pending = viewModel.TryAddRecipeProgramFolderAsync(@"D:\Tools");
        Assert.True(machine.Entered.Wait(AsyncFolderWait));
        Assert.Equal(english, viewModel.RecipeProgramFolderChecking);

        await localizer.SwitchLocaleAsync("fr");

        string french = localizer["RecipeFolderChecking"];
        Assert.NotEqual(english, french);
        Assert.Equal(french, viewModel.RecipeProgramFolderChecking);

        machine.Gate.Set();
        Assert.True(await pending);
        Assert.False(viewModel.HasRecipeProgramFolderChecking);
    }

    [Fact]
    public async Task ALanguageSwitchWithNoCheckRunningLeavesTheCheckingTextEmpty()
    {
        (SettingsViewModel viewModel, _, LocalizationManager localizer) = await GatedFolderPanelAsync();

        await localizer.SwitchLocaleAsync("fr");

        Assert.False(viewModel.HasRecipeProgramFolderChecking);
        Assert.Equal(string.Empty, viewModel.RecipeProgramFolderChecking);
    }

    [Fact]
    public async Task AnOldRefusalIsClearedWhenANewCheckStarts()
    {
        (SettingsViewModel viewModel, GatedFolderMachine machine, _) = await GatedFolderPanelAsync();
        Assert.False(await viewModel.TryAddRecipeProgramFolderAsync(@"\\server\share\tools"));
        Assert.True(viewModel.HasRecipeProgramFolderStatus);
        machine.Gate.Reset();

        Task<bool> pending = viewModel.TryAddRecipeProgramFolderAsync(@"D:\Tools");
        Assert.True(machine.Entered.Wait(AsyncFolderWait));

        Assert.True(viewModel.HasRecipeProgramFolderChecking);
        Assert.False(viewModel.HasRecipeProgramFolderStatus);

        machine.Gate.Set();
        Assert.True(await pending);
    }

    [Fact]
    public async Task AResolveThatThrowsReachesTheCallerLikeTheSyncPathDoesAddsNothingAndClearsTheCheckingText()
    {
        (SettingsViewModel viewModel, GatedFolderMachine machine, _) = await GatedFolderPanelAsync();
        machine.Failure = new InvalidOperationException("The drive failed.");

        await Assert.ThrowsAsync<InvalidOperationException>(() => viewModel.TryAddRecipeProgramFolderAsync(@"D:\Tools"));

        Assert.Empty(viewModel.RecipeProgramFolders);
        Assert.False(viewModel.HasRecipeProgramFolderChecking);
        Assert.Throws<InvalidOperationException>(() => viewModel.TryAddRecipeProgramFolder(@"D:\Tools"));
    }

    [Fact]
    public async Task AFolderThatLeadsToADriveRootIsStillRefusedThroughTheAsyncPath()
    {
        (SettingsViewModel viewModel, GatedFolderMachine machine, LocalizationManager localizer) = await GatedFolderPanelAsync();
        machine.Finals[@"D:\link"] = @"C:\";

        bool added = await viewModel.TryAddRecipeProgramFolderAsync(@"D:\link");

        Assert.False(added);
        Assert.Empty(viewModel.RecipeProgramFolders);
        Assert.Equal(localizer["RecipeFolderRefusalLeadsToDriveRoot"], viewModel.RecipeProgramFolderStatus);
        Assert.False(viewModel.HasRecipeProgramFolderChecking);
    }

    [Fact]
    public async Task AFolderUnderWindowsAddedThroughTheAsyncPathSetsTheNotice()
    {
        (SettingsViewModel viewModel, _, _) = await GatedFolderPanelAsync();

        bool added = await viewModel.TryAddRecipeProgramFolderAsync(@"C:\Windows\Tools");

        Assert.True(added);
        Assert.True(viewModel.HasRecipeProgramFolderNotice);
    }
}
