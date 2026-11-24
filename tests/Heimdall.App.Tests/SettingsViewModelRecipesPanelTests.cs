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
using Heimdall.App.Services;
using Heimdall.App.ViewModels;
using Heimdall.App.ViewModels.Settings;
using Heimdall.Core.Certificates;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;
using Heimdall.Core.Security;
using Heimdall.Core.Security.Vault;
using Heimdall.Core.Updates;
using Heimdall.Ssh;

namespace Heimdall.App.Tests;

public sealed partial class SettingsViewModelTests
{
    private static SettingsViewModel CreateRecipePanel(
        FakeConfigManager config,
        LocalizationManager? localizer = null,
        ToolRegistry? toolRegistry = null,
        RecipeSettingsViewModel? recipes = null,
        IUiDispatcher? uiDispatcher = null,
        FakeDialogService? dialog = null)
    {
        localizer ??= new LocalizationManager();
        dialog ??= new FakeDialogService();
        TrustedHostKeysSettingsViewModel trustedHostKeys = new(
            new HostKeyTrustService(new HostKeyStore()),
            () => new KnownHostsImportReport(0, 0, []),
            () => new KnownHostsExportReport(0, 0, 0),
            localizer,
            dialog,
            new FakeClipboardService(),
            new FakeUiDispatcher());
        TrustedRdpCertificatesSettingsViewModel trustedRdpCertificates = new(
            new RdpCertificateTrustStore(),
            () => Task.FromResult<IReadOnlyList<ServerProfileDto>>([]),
            localizer,
            dialog,
            new FakeUiDispatcher());

        return new SettingsViewModel(
            config,
            localizer,
            dialog,
            trustedHostKeys,
            trustedRdpCertificates,
            new PinManager(),
            new VaultLifecycleService(config),
            new FakeUpdateService(),
            new AppVersionProvider("2026.061501"),
            new FakeUpdateInstallFlow(),
            new StubBrowserLauncher(),
            profileImportService: null,
            toolRegistry: toolRegistry,
            recipes: recipes,
            uiDispatcher: uiDispatcher);
    }

    private static ToolPackSwitchViewModel RecipesSwitch(SettingsViewModel viewModel)
        => Assert.Single(viewModel.ToolPackSwitches, item => item.Pack == ToolPack.Recipes);

    // ---- the switch follows the marker, and turning it on records the choice --------------------

    [Fact]
    public void WithoutTheMarkerTheRecipesSwitchReadsOffEvenWhenTheListDoesNotNameThePack()
    {
        FakeConfigManager config = new() { Settings = new AppSettings { DisabledToolPacks = [nameof(ToolPack.Audit)] } };
        SettingsViewModel viewModel = CreateRecipePanel(config);
        viewModel.LoadFromSettings(config.Settings);

        Assert.False(RecipesSwitch(viewModel).IsEnabled);
        Assert.True(SwitchOfPack(viewModel, ToolPack.Essentials).IsEnabled);
    }

    [Fact]
    public void WithTheMarkerTheRecipesSwitchFollowsTheList()
    {
        FakeConfigManager config = new() { Settings = new AppSettings { RecipesPackMigrated = true } };
        SettingsViewModel viewModel = CreateRecipePanel(config);
        viewModel.LoadFromSettings(config.Settings);

        Assert.True(RecipesSwitch(viewModel).IsEnabled);
    }

    [Fact]
    public async Task TurningTheSwitchOnAndSavingSetsTheMarkerAndLeavesTheListAlone()
    {
        FakeConfigManager config = new() { Settings = new AppSettings { DisabledToolPacks = [nameof(ToolPack.Audit)] } };
        SettingsViewModel viewModel = CreateRecipePanel(config);
        viewModel.LoadFromSettings(config.Settings);
        Assert.False(viewModel.IsDirty);

        RecipesSwitch(viewModel).IsEnabled = true;

        Assert.True(RecipesSwitch(viewModel).IsEnabled);
        Assert.True(viewModel.IsDirty);
        await viewModel.SaveCommand.ExecuteAsync(null);
        AppSettings saved = Assert.IsType<AppSettings>(config.SavedSettings);
        Assert.True(saved.RecipesPackMigrated);
        Assert.Equal([nameof(ToolPack.Audit)], saved.DisabledToolPacks);
    }

    [Fact]
    public async Task SavingWithoutTheChoiceDoesNotSetTheMarker()
    {
        FakeConfigManager config = new() { Settings = new AppSettings { DisabledToolPacks = [nameof(ToolPack.Audit)] } };
        SettingsViewModel viewModel = CreateRecipePanel(config);
        viewModel.LoadFromSettings(config.Settings);
        SwitchOfPack(viewModel, ToolPack.Audit).IsEnabled = true;

        await viewModel.SaveCommand.ExecuteAsync(null);

        AppSettings saved = Assert.IsType<AppSettings>(config.SavedSettings);
        Assert.False(saved.RecipesPackMigrated);
        Assert.Empty(saved.DisabledToolPacks);
    }

    [Fact]
    public async Task TurningTheSwitchOffAfterOnLeavesThePackListedAndTheMarkerSet()
    {
        FakeConfigManager config = new() { Settings = new AppSettings() };
        SettingsViewModel viewModel = CreateRecipePanel(config);
        viewModel.LoadFromSettings(config.Settings);
        RecipesSwitch(viewModel).IsEnabled = true;

        RecipesSwitch(viewModel).IsEnabled = false;
        await viewModel.SaveCommand.ExecuteAsync(null);

        AppSettings saved = Assert.IsType<AppSettings>(config.SavedSettings);
        Assert.Contains(nameof(ToolPack.Recipes), saved.DisabledToolPacks);
        Assert.True(saved.RecipesPackMigrated);
    }

    [Fact]
    public async Task ASaveNeverTakesTheMarkerAway()
    {
        FakeConfigManager config = new() { Settings = new AppSettings { RecipesPackMigrated = true } };
        SettingsViewModel viewModel = CreateRecipePanel(config);
        viewModel.LoadFromSettings(config.Settings);
        viewModel.TerminalFontFamily = "Iosevka Term";

        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.True(Assert.IsType<AppSettings>(config.SavedSettings).RecipesPackMigrated);
    }

    private static ToolPackSwitchViewModel SwitchOfPack(SettingsViewModel viewModel, ToolPack pack)
        => Assert.Single(viewModel.ToolPackSwitches, item => item.Pack == pack);

    // ---- importing a settings file --------------------------------------------------------------

    private static async Task<(SettingsViewModel ViewModel, FakeDialogService Dialog)> ImportAsync(AppSettings current, string fileText)
    {
        string file = Path.Combine(Path.GetTempPath(), "heimdall-settings-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(file, fileText);
            FakeConfigManager config = new() { Settings = current };
            FakeDialogService dialog = new() { ConfirmResult = true };
            LocalizationManager localizer = new();
            await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "en");
            SettingsViewModel viewModel = CreateRecipePanel(config, localizer, dialog: dialog);
            viewModel.LoadFromSettings(config.Settings);
            viewModel.SettingsImportPathProvider = () => file;

            await viewModel.ImportSettingsCommand.ExecuteAsync(null);
            return (viewModel, dialog);
        }
        finally
        {
            File.Delete(file);
        }
    }

    private const string OldSettingsFile = "{\"format\":\"heimdall-settings\",\"version\":1,\"settings\":{\"DisabledToolPacks\":[\"Audit\"]}}";

    private const string NewSettingsFileWithThePackOn = "{\"format\":\"heimdall-settings\",\"version\":1,\"settings\":{\"DisabledToolPacks\":[\"Audit\"],\"RecipesPackMigrated\":true}}";

    [Fact]
    public async Task ImportingAnOldFileLeavesTheRecipesPackOffAndTellsNothingAboutIt()
    {
        AppSettings current = new() { RecipesPackMigrated = true, DisabledToolPacks = [nameof(ToolPack.Audit), nameof(ToolPack.Recipes), nameof(ToolPack.Administration)] };

        (SettingsViewModel viewModel, FakeDialogService dialog) = await ImportAsync(current, OldSettingsFile);

        Assert.Contains(nameof(ToolPack.Recipes), viewModel.DisabledToolPacks);
        Assert.DoesNotContain(nameof(ToolPack.Administration), viewModel.DisabledToolPacks);
        Assert.False(RecipesSwitch(viewModel).IsEnabled);
        Assert.DoesNotContain("Turns the Recipes pack on", Assert.Single(dialog.ConfirmCalls).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImportingAFileFromThisVersionWithThePackOnTurnsItOnAndTheConfirmationSaysSo()
    {
        AppSettings current = new() { RecipesPackMigrated = true, DisabledToolPacks = [nameof(ToolPack.Audit), nameof(ToolPack.Recipes)] };

        (SettingsViewModel viewModel, FakeDialogService dialog) = await ImportAsync(current, NewSettingsFileWithThePackOn);

        Assert.DoesNotContain(nameof(ToolPack.Recipes), viewModel.DisabledToolPacks);
        Assert.True(RecipesSwitch(viewModel).IsEnabled);
        Assert.Contains("Turns the Recipes pack on", Assert.Single(dialog.ConfirmCalls).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImportingAnOldFileOverAnInstallWhoseMarkerIsMissingKeepsThePackOff()
    {
        AppSettings current = new() { DisabledToolPacks = [nameof(ToolPack.Audit)] };

        (SettingsViewModel viewModel, _) = await ImportAsync(current, OldSettingsFile);

        Assert.False(RecipesSwitch(viewModel).IsEnabled);
    }

    // ---- the recipe list is read when it is first shown, never when the panel is built ----------

    [Fact]
    public async Task BuildingThePanelLoadingItAndChangingTheLanguageNeverReadTheRecipeFolder()
    {
        using RecipeLazyRig rig = await RecipeLazyRig.CreateAsync();
        FakeConfigManager config = new();

        SettingsViewModel viewModel = CreateRecipePanel(config, rig.Localizer, recipes: rig.ViewModel);
        viewModel.LoadFromSettings(config.Settings);
        await rig.Localizer.SwitchLocaleAsync("fr");

        Assert.Equal(0, rig.Store.LoadCount);
        Assert.Equal(0, rig.Store.ListCount);
        Assert.Same(rig.ViewModel, viewModel.Recipes);
    }

    // ---- the registry event can come from any thread ---------------------------------------------

    private static async Task<(SettingsViewModel ViewModel, ToolRegistry Registry)> PanelOverARegistryAsync(IUiDispatcher? dispatcher)
    {
        ToolRegistry registry = new();
        FakeConfigManager config = new();
        SettingsViewModel viewModel = CreateRecipePanel(config, toolRegistry: registry, uiDispatcher: dispatcher);
        viewModel.LoadFromSettings(config.Settings);
        Assert.Equal(0, RecipesSwitch(viewModel).ToolCount);
        await Task.CompletedTask;
        return (viewModel, registry);
    }

    private static void RegisterTwo(ToolRegistry registry)
        => registry.RegisterRecipeTools(
            [RecipeTestFiles.Recipe("acme.one"), RecipeTestFiles.Recipe("acme.two")],
            _ => throw new InvalidOperationException("never built"));

    [Fact]
    public async Task ARegistryEventOffTheUiThreadIsHandedToTheUiThreadAndNothingChangesBeforeItRuns()
    {
        List<Action> queued = [];
        FakeUiDispatcher dispatcher = new(checkAccess: false) { InvokeAsyncActionHandler = queued.Add };
        (SettingsViewModel viewModel, ToolRegistry registry) = await PanelOverARegistryAsync(dispatcher);

        await Task.Run(() => RegisterTwo(registry));

        Assert.Equal(1, dispatcher.InvokeAsyncCalls);
        Assert.Equal(0, RecipesSwitch(viewModel).ToolCount);
        foreach (Action action in queued)
        {
            action();
        }

        Assert.Equal(2, RecipesSwitch(viewModel).ToolCount);
    }

    [Fact]
    public async Task ARegistryEventOnTheUiThreadIsAppliedAtOnce()
    {
        FakeUiDispatcher dispatcher = new(checkAccess: true);
        (SettingsViewModel viewModel, ToolRegistry registry) = await PanelOverARegistryAsync(dispatcher);

        RegisterTwo(registry);

        Assert.Equal(0, dispatcher.InvokeAsyncCalls);
        Assert.Equal(2, RecipesSwitch(viewModel).ToolCount);
    }
}

/// <summary>The recipe list of Settings over a store that counts what it is asked, in the shipped English locale.</summary>
internal sealed class RecipeLazyRig : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "heimdall-recipe-lazy-tests", Guid.NewGuid().ToString("N"));

    private RecipeLazyRig(LocalizationManager localizer)
    {
        Localizer = localizer;
        Store.Recipes.Add(RecipeBuilders.Loaded(RecipeBuilders.Recipe(id: "acme.one", name: "One")));
        ViewModel = new RecipeSettingsViewModel(
            Store,
            new RecipeImportService(Store, new FakeRecipeProgramLocator(), new FakeToolAvailability()),
            new ConfigManager(AppContext.BaseDirectory, Path.Combine(_root, "config")),
            new RecipeRecordingDialogs(),
            localizer,
            new FakeRecipeFileDialogs(),
            () => Reloaded++);
    }

    public LocalizationManager Localizer { get; }

    public FakeRecipeStore Store { get; } = new();

    public RecipeSettingsViewModel ViewModel { get; }

    public int Reloaded { get; private set; }

    public static async Task<RecipeLazyRig> CreateAsync()
    {
        LocalizationManager localizer = new();
        await localizer.LoadAsync(Path.Combine(RecipeViewModelHarness.RepositoryRoot(), "locales"), "en");
        return new RecipeLazyRig(localizer);
    }

    public void Dispose() => ViewModel.Dispose();
}
