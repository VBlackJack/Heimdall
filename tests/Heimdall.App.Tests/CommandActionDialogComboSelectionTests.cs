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
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Threading;
using System.Xml.Linq;
using Heimdall.App.ViewModels;
using Heimdall.App.ViewModels.Dialogs;
using Microsoft.Extensions.DependencyInjection;
using TwinShell.Core.Enums;
using TwinShell.Core.Interfaces;
using TwinShell.Core.Services;
using ActionModel = TwinShell.Core.Models.Action;

namespace Heimdall.App.Tests;

/// <summary>
/// The Platform and Risk combos of the action editor show the value of the action being
/// edited, copied or created.
/// </summary>
/// <remarks>
/// <para>
/// Both combos bound <c>SelectedIndex</c> straight to an enum property. WPF does not convert
/// an enum to the index of an item, so the binding failed silently: the combos opened empty
/// on every path (Duplicate, Edit, New). The model was right the whole time, which is why a
/// test on the view model alone cannot see it.
/// </para>
/// <para>
/// These tests read the real markup of the two combos out of CommandActionDialog.xaml and load
/// them with the framework's own parser, as ScheduledGridMarkupTests does for the scheduled
/// grid. A window is never created: it would seal the application resources of the test
/// process. Only what the application resources supply is replaced, the shared style and the
/// localized labels.
/// </para>
/// </remarks>
public sealed class CommandActionDialogComboSelectionTests
{
    private const string DialogRelativePath = "src/Heimdall.App/Views/Dialogs/CommandActionDialog.xaml";
    private const string PlatformComboName = "CmbPlatform";
    private const string RiskComboName = "CmbRisk";

    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly string[] AttributesThatNeedTheApplication = ["Style"];

    [Fact]
    public void EveryPlatformIsShownSelectedByItsCombo()
    {
        StaDispatcherRunner.Run(() =>
        {
            foreach (var platform in Enum.GetValues<Platform>())
            {
                var vm = new CommandActionDialogViewModel { Platform = platform };
                var combo = LoadCombo(PlatformComboName, vm);

                Assert.NotNull(combo.SelectedItem);
                Assert.Equal(platform, ((ComboBoxItem)combo.SelectedItem).Tag);
            }

            return Task.CompletedTask;
        });
    }

    [Fact]
    public void EveryRiskIsShownSelectedByItsCombo()
    {
        StaDispatcherRunner.Run(() =>
        {
            foreach (var level in Enum.GetValues<CriticalityLevel>())
            {
                var vm = new CommandActionDialogViewModel { Level = level };
                var combo = LoadCombo(RiskComboName, vm);

                Assert.NotNull(combo.SelectedItem);
                Assert.Equal(level, ((ComboBoxItem)combo.SelectedItem).Tag);
            }

            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// The other direction: what the operator picks must still reach the view model, or the
    /// save writes whatever the dialog opened with. This one held before the fix too; it guards
    /// the switch to a value-matched selection.
    /// </summary>
    [Fact]
    public void APickInTheCombosReachesTheViewModel()
    {
        StaDispatcherRunner.Run(() =>
        {
            var vm = new CommandActionDialogViewModel { Platform = Platform.Both, Level = CriticalityLevel.Info };
            var platformCombo = LoadCombo(PlatformComboName, vm);
            var riskCombo = LoadCombo(RiskComboName, vm);

            platformCombo.SelectedIndex = (int)Platform.Windows;
            riskCombo.SelectedIndex = (int)CriticalityLevel.Dangerous;

            Assert.Equal(Platform.Windows, vm.Platform);
            Assert.Equal(CriticalityLevel.Dangerous, vm.Level);
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// The reported path: "Duplicate into my library" on a shipped action opens a dialog whose
    /// category, platform and risk equal the source's, and whose combos show them.
    /// </summary>
    [Fact]
    public void DuplicatingOpensCombosShowingTheSourceCategoryPlatformAndRisk()
    {
        StaDispatcherRunner.Run(async () =>
        {
            var source = CommandLibraryTestHelpers.CreateLinuxAction("seed-audit", "Show the audit rules", "auditctl -l");
            source.PublicId = Guid.NewGuid();
            source.IsUserCreated = false;
            source.LinuxCommandTemplate!.PublicId = Guid.NewGuid();
            source.Level = CriticalityLevel.Dangerous;

            var viewModel = await CreateViewModelAsync(new FakeActionService([source]));
            using var _ = viewModel;

            CommandActionDialogViewModel? shown = null;
            ComboBox? platformCombo = null;
            ComboBox? riskCombo = null;
            viewModel.ShowActionDialogAsync = vm =>
            {
                shown = vm;
                platformCombo = LoadCombo(PlatformComboName, vm);
                riskCombo = LoadCombo(RiskComboName, vm);
                return Task.FromResult(false);
            };
            viewModel.SelectedEntry = viewModel.AllEntries.Single();

            await viewModel.DuplicateSelectedAsync();

            Assert.NotNull(shown);
            Assert.Equal(source.Platform, shown.Platform);
            Assert.Equal(source.Level, shown.Level);
            Assert.Contains(shown.Category, shown.AvailableCategories);

            Assert.Equal(source.Platform, ((ComboBoxItem)platformCombo!.SelectedItem).Tag);
            Assert.Equal(source.Level, ((ComboBoxItem)riskCombo!.SelectedItem).Tag);
        });
    }

    private static ComboBox LoadCombo(string name, CommandActionDialogViewModel vm)
    {
        var path = Path.Combine(FindRepoRoot(), DialogRelativePath.Replace('/', Path.DirectorySeparatorChar));
        var source = XDocument.Load(path)
            .Descendants(Presentation + "ComboBox")
            .Single(combo => string.Equals((string?)combo.Attribute(Xaml + "Name"), name, StringComparison.Ordinal));

        var combo = new XElement(source);
        foreach (var attribute in AttributesThatNeedTheApplication)
        {
            combo.Attribute(attribute)?.Remove();
        }

        // The labels and the accessible name come from the localizer, which the application
        // resources initialise; the selection does not depend on them.
        combo.Attribute("AutomationProperties.Name")?.Remove();
        foreach (var item in combo.Elements(Presentation + "ComboBoxItem"))
        {
            item.Attribute("Content")?.Remove();
        }

        // A clone keeps the namespace declarations of the document root only when they are
        // declared again, and the markup uses the enums prefix.
        XNamespace enums = "clr-namespace:TwinShell.Core.Enums;assembly=TwinShell.Core";
        combo.SetAttributeValue(XNamespace.Xmlns + "enums", enums.NamespaceName);
        combo.SetAttributeValue(XNamespace.Xmlns + "x", Xaml.NamespaceName);

        var loaded = (ComboBox)XamlReader.Parse(combo.ToString());
        loaded.DataContext = vm;

        // The binding engine attaches a parsed binding through the dispatcher, so the
        // selection is only there once the queue has been drained.
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        return loaded;
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "Heimdall.slnx")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new DirectoryNotFoundException(
            $"Cannot find repository root containing Heimdall.slnx from: {AppContext.BaseDirectory}");
    }

    private static async Task<CommandLibraryViewModel> CreateViewModelAsync(FakeActionService actions)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILocalizationService, FakeTwinShellLocalizationService>();
        services.AddScoped<IActionService>(_ => actions);
        services.AddScoped<IFavoritesService, FakeFavoritesService>();
        services.AddScoped<ISearchService, SearchService>();
        services.AddScoped<ICommandGeneratorService, CommandGeneratorService>();

        var viewModel = new CommandLibraryViewModel(
            services.BuildServiceProvider(),
            configManager: null!,
            await CommandLibraryTestHelpers.CreateAppLocalizerAsync(),
            new SilentDialogService(),
            gitSyncService: null!,
            transferService: null!);

        await viewModel.InitializeAsync(targetHost: null);
        return viewModel;
    }
}
