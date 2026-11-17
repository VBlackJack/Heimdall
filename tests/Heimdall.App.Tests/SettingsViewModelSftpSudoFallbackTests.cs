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
using System.Runtime.ExceptionServices;
using System.Windows.Controls;
using System.Xml.Linq;
using Heimdall.App.Tests.Views.EmbeddedRdp;
using Heimdall.App.ViewModels;
using Heimdall.App.ViewModels.Settings;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;
using Heimdall.Core.Security;

namespace Heimdall.App.Tests;

/// <summary>
/// The "retry refused operations as root" setting in the Settings panel: its round trip, the
/// organisation cap on its choices, its line on the security posture card, and its place in the
/// settings search.
/// </summary>
public sealed partial class SettingsViewModelTests
{
    private const string SftpSudoFallbackControl = "Mw_SettingsSftpSudoFallback";

    private static SftpSudoFallbackPolicy CapAt(int? raw) => new(() => raw);

    [Theory]
    [InlineData(SftpSudoFallbackMode.Never)]
    [InlineData(SftpSudoFallbackMode.Ask)]
    [InlineData(SftpSudoFallbackMode.Automatic)]
    public async Task TheSftpSudoFallbackRoundTripsThroughThePanel(SftpSudoFallbackMode mode)
    {
        FakeConfigManager config = new() { Settings = new AppSettings { SftpSudoFallback = mode } };
        SettingsViewModel viewModel = CreateViewModel(config);

        viewModel.LoadFromSettings(config.Settings);
        Assert.Equal(mode.ToString(), viewModel.SftpSudoFallback);

        SftpSudoFallbackMode other = mode == SftpSudoFallbackMode.Never ? SftpSudoFallbackMode.Automatic : SftpSudoFallbackMode.Never;
        viewModel.SftpSudoFallback = other.ToString();
        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.Equal(other, Assert.IsType<AppSettings>(config.SavedSettings).SftpSudoFallback);
    }

    [Fact]
    public async Task APanelValueNoModeMatchesIsSavedAsAsk()
    {
        FakeConfigManager config = new();
        SettingsViewModel viewModel = CreateViewModel(config);
        viewModel.LoadFromSettings(config.Settings);

        viewModel.SftpSudoFallback = "Always";
        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.Equal(SftpSudoFallbackMode.Ask, Assert.IsType<AppSettings>(config.SavedSettings).SftpSudoFallback);
    }

    [Fact]
    public void ChangingTheChoiceMarksThePanelDirtyAndModifiedFromDefault()
    {
        FakeConfigManager config = new();
        SettingsViewModel viewModel = CreateViewModel(config);
        viewModel.LoadFromSettings(config.Settings);
        viewModel.IsDirty = false;
        Assert.False(viewModel.IsModifiedFromDefault(nameof(SettingsViewModel.SftpSudoFallback)));

        viewModel.SftpSudoFallback = nameof(SftpSudoFallbackMode.Automatic);

        Assert.True(viewModel.IsDirty);
        Assert.True(viewModel.IsModifiedFromDefault(nameof(SettingsViewModel.SftpSudoFallback)));
    }

    [Fact]
    public void WithoutACapEveryChoiceIsOfferedAndNoLineSaysSo()
    {
        SettingsViewModel viewModel = CreateViewModel(new FakeConfigManager(), sftpSudoFallbackPolicy: CapAt(null));

        Assert.False(viewModel.IsSftpSudoFallbackCapped);
        Assert.True(viewModel.IsSftpSudoFallbackAskAllowed);
        Assert.True(viewModel.IsSftpSudoFallbackAutomaticAllowed);
        Assert.Equal(string.Empty, viewModel.SftpSudoFallbackCapText);
    }

    [Theory]
    [InlineData(0, false, false)]
    [InlineData(1, true, false)]
    [InlineData(2, true, true)]
    public void ACapDisablesTheChoicesLooserThanIt(int cap, bool ask, bool automatic)
    {
        SettingsViewModel viewModel = CreateViewModel(new FakeConfigManager(), sftpSudoFallbackPolicy: CapAt(cap));

        Assert.True(viewModel.IsSftpSudoFallbackCapped);
        Assert.Equal(ask, viewModel.IsSftpSudoFallbackAskAllowed);
        Assert.Equal(automatic, viewModel.IsSftpSudoFallbackAutomaticAllowed);
    }

    [Fact]
    public async Task TheCapLineSaysTheOrganisationSetItAndWhatIsInEffect()
    {
        LocalizationManager localizer = new();
        await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "en");
        SettingsViewModel viewModel = CreateViewModel(new FakeConfigManager(), localizer: localizer, sftpSudoFallbackPolicy: CapAt(1));

        viewModel.SftpSudoFallback = nameof(SftpSudoFallbackMode.Automatic);
        Assert.Equal("Set by your organisation: at most Ask first. In effect now: Ask first.", viewModel.SftpSudoFallbackCapText);

        viewModel.SftpSudoFallback = nameof(SftpSudoFallbackMode.Never);
        Assert.Equal("Set by your organisation: at most Ask first. In effect now: Never.", viewModel.SftpSudoFallbackCapText);
    }

    [Fact]
    public async Task ThePostureLineIsRiskyOnlyForAutomatic()
    {
        (SettingsViewModel viewModel, FakeConfigManager _) = await CreatePostureViewModelAsync(new AppSettings());
        SecurityPostureLine line = viewModel.SecurityPosture.Get(SecurityPostureKey.SftpSudoFallback);
        Assert.False(line.IsRisky);
        Assert.Equal("SFTP retry as root: Ask first", line.Text);

        viewModel.SftpSudoFallback = nameof(SftpSudoFallbackMode.Automatic);

        Assert.True(line.IsRisky);
        Assert.True(line.IsUnsaved);
        Assert.Equal(1, viewModel.SecurityPostureRiskCount);
        Assert.Equal(SecurityPosture.SftpSudoFallbackTarget, line.TargetSettingId);

        viewModel.SftpSudoFallback = nameof(SftpSudoFallbackMode.Never);

        Assert.False(line.IsRisky);
        Assert.Equal("SFTP retry as root: Never", line.Text);
    }

    [Fact]
    public async Task ACapThatLowersTheChoiceShowsAManagedLineThatIsNotRisky()
    {
        LocalizationManager localizer = new();
        await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "en");
        AppSettings saved = new() { SftpSudoFallback = SftpSudoFallbackMode.Automatic };
        FakeConfigManager config = new() { Settings = saved };
        SettingsViewModel viewModel = CreateViewModel(config, localizer: localizer, sftpSudoFallbackPolicy: CapAt(0));
        viewModel.LoadFromSettings(saved);
        await viewModel.WhenSavedSettingsLoadedAsync();

        SecurityPostureLine line = viewModel.SecurityPosture.Get(SecurityPostureKey.SftpSudoFallback);

        Assert.False(line.IsRisky);
        Assert.Equal("SFTP retry as root: Set by your organisation: Never", line.Text);
        Assert.Equal(0, viewModel.SecurityPostureRiskCount);
    }

    // The posture target is a name the markup must carry on a control the user can focus.
    [Fact]
    public async Task TheControlSitsOnTheSftpTabWithItsLabelItsChoicesAndAWordingInEveryLanguage()
    {
        XElement root = XDocument.Load(Path.Combine(ViewSource.RepoRoot(), "src", "Heimdall.App", "MainWindow.xaml")).Root!;
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement combo = root.Descendants()
            .Single(element => element.Attribute(xaml + "Name")?.Value == SftpSudoFallbackControl);
        XElement tab = combo.Ancestors().First(element => element.Name.LocalName == "TabItem");
        Assert.Equal("Mw_SettingsSshSubTabSftpX11", tab.Attribute(xaml + "Name")!.Value);
        Assert.Equal("ComboBox", combo.Name.LocalName);

        List<string> keys =
        [
            TranslateKey(NamedElement(root, "Mw_SettingsSftpSudoFallbackLabel").Attribute("Text")!.Value),
            .. combo.Elements().Select(item => TranslateKey(item.Attribute("Content")!.Value)),
            TranslateKey(NamedElement(root, "Mw_SettingsSftpSudoFallbackHint").Attribute("Text")!.Value),
        ];
        Assert.Equal(5, keys.Count);

        string[] otherKeys =
        [
            "SettingsSftpSudoFallbackCapped",
            "SecurityPostureStateManaged",
            "SecurityPostureLabelSftpSudoFallback",
            "SecurityPostureWarningSftpSudoFallback",
        ];
        foreach (string locale in new[] { "en", "fr", "es" })
        {
            LocalizationManager localizer = new();
            await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), locale);
            foreach (string key in keys.Concat(otherKeys))
            {
                Assert.True(localizer.HasKey(key), $"{locale}: {key}");
                Assert.False(string.IsNullOrWhiteSpace(localizer[key]), $"{locale}: {key}");
            }
        }
    }

    // The search walks the settings tab and indexes labels and combo choices; this builds the
    // control from the strings the markup binds and checks the words a user may type find it.
    [Fact]
    public async Task TheSettingsSearchFindsTheLabelAndEveryChoice()
    {
        LocalizationManager localizer = new();
        await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "en");
        string label = localizer["SettingsLabelSftpSudoFallback"];
        string[] choices =
        [
            localizer["SettingsSftpSudoFallbackNever"],
            localizer["SettingsSftpSudoFallbackAsk"],
            localizer["SettingsSftpSudoFallbackAutomatic"],
        ];

        RunOnSta(() =>
        {
            StackPanel panel = new();
            panel.Children.Add(new TextBlock { Text = label });
            ComboBox combo = new();
            foreach (string choice in choices)
            {
                combo.Items.Add(new ComboBoxItem { Content = choice });
            }

            panel.Children.Add(combo);
            List<MainWindow.SettingsSearchEntry> entries = [];
            MainWindow.AddSettingsSearchEntries(panel, new TabItem(), entries);
            List<string> texts = entries.Select(MainWindow.GetSettingsSearchEntryText).ToList();

            Assert.Contains(label, texts);
            foreach (string choice in choices)
            {
                Assert.Contains(choice, texts);
            }

            Assert.Contains(texts, text => MainWindow.SettingsSearchMatches(text, "root"));
            Assert.Contains(texts, text => MainWindow.SettingsSearchMatches(text, "automatic"));
        });
    }

    // The import preview and the catalog guard name the setting by this label.
    [Fact]
    public void TheSettingHasAPanelPlaceAndTravelsWithAnExport()
    {
        Assert.Empty(SettingsLabelCatalog.Unplaced([nameof(AppSettings.SftpSudoFallback)]));
        Assert.Contains(nameof(AppSettings.SftpSudoFallback), SettingsTransfer.TransferableKeys);
    }

    private static XElement NamedElement(XElement root, string name)
    {
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        return root.Descendants().Single(element => element.Attribute(xaml + "Name")?.Value == name);
    }

    private static string TranslateKey(string binding)
    {
        const string Prefix = "{loc:Translate ";
        Assert.StartsWith(Prefix, binding, StringComparison.Ordinal);
        return binding[Prefix.Length..].TrimEnd('}');
    }

    private static void RunOnSta(Action action)
    {
        Exception? exception = null;
        Thread thread = new(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                exception = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (exception is not null)
        {
            ExceptionDispatchInfo.Capture(exception).Throw();
        }
    }
}
