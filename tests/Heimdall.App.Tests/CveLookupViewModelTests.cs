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
using Heimdall.App.ViewModels.Tools;
using Heimdall.Core.Localization;

namespace Heimdall.App.Tests;

public sealed class CveLookupViewModelTests
{
    [Fact]
    public async Task Initialize_PopulatesDbInfoAndHelpText()
    {
        var localizer = await CreateLocalizerAsync("en");
        var vm = new CveLookupViewModel();

        vm.Initialize(localizer);

        Assert.Contains("Offline database", vm.DbInfoText, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(vm.HelpText));
    }

    [Fact]
    public void SearchCommand_EmptyInput_ResetsToEmptyState()
    {
        var vm = new CveLookupViewModel
        {
            Input = "OpenSSH",
        };
        vm.SearchCommand.Execute(null);
        Assert.Equal(CveSearchState.HasResults, vm.State);

        vm.Input = "   ";
        vm.SearchCommand.Execute(null);

        Assert.Equal(CveSearchState.Empty, vm.State);
        Assert.Empty(vm.Results);
        Assert.False(vm.CopyCommand.CanExecute(null));
    }

    [Fact]
    public void SearchCommand_UnknownProduct_SetsNoResultsState()
    {
        var vm = new CveLookupViewModel
        {
            Input = "UnknownApp 1.0",
        };

        vm.SearchCommand.Execute(null);

        Assert.Equal(CveSearchState.NoResults, vm.State);
        Assert.Empty(vm.Results);
        Assert.False(vm.CopyCommand.CanExecute(null));
    }

    [Fact]
    public async Task SearchCommand_KnownProduct_ProjectsLocalizedResults()
    {
        var localizer = await CreateLocalizerAsync("en");
        var vm = new CveLookupViewModel();
        vm.Initialize(localizer);
        vm.Input = "OpenSSH 8.9";

        vm.SearchCommand.Execute(null);

        Assert.Equal(CveSearchState.HasResults, vm.State);
        Assert.NotEmpty(vm.Results);
        Assert.True(vm.CopyCommand.CanExecute(null));
        Assert.True(vm.Results.Count > 1, "the fixture must match several CVEs to pin the plural");
        Assert.StartsWith($"{vm.Results.Count} CVEs found for ", vm.SummaryText, StringComparison.Ordinal);
        Assert.Equal("Critical", vm.Results[0].SeverityLabel);
    }

    [Fact]
    public async Task SearchWith_PrefillsInputAndRunsSearch()
    {
        var localizer = await CreateLocalizerAsync("en");
        var vm = new CveLookupViewModel();
        vm.Initialize(localizer);

        vm.SearchWith("Apache Tomcat 9.0.80");

        Assert.Equal("Apache Tomcat 9.0.80", vm.Input);
        Assert.Equal(CveSearchState.HasResults, vm.State);
        Assert.NotEmpty(vm.Results);
        Assert.Equal("CVE-2024-50379", vm.Results[0].CveId);
    }

    [Fact]
    public async Task Initialize_NewLocale_ReprojectsExistingResults()
    {
        var en = await CreateLocalizerAsync("en");
        var fr = await CreateLocalizerAsync("fr");
        var vm = new CveLookupViewModel();
        vm.Initialize(en);
        vm.SearchWith("OpenSSH 8.9");
        var englishSummary = vm.SummaryText;
        var englishSeverity = vm.Results[0].SeverityLabel;

        vm.Initialize(fr);

        Assert.NotEqual(englishSummary, vm.SummaryText);
        Assert.NotEqual(englishSeverity, vm.Results[0].SeverityLabel);
        Assert.Equal("Critique", vm.Results[0].SeverityLabel);
    }

    [Fact]
    public void ToggleHelpCommand_TogglesVisibility()
    {
        var vm = new CveLookupViewModel();

        vm.ToggleHelpCommand.Execute(null);
        Assert.True(vm.IsHelpVisible);

        vm.ToggleHelpCommand.Execute(null);
        Assert.False(vm.IsHelpVisible);
    }

    [Fact]
    public void Dispose_CanBeCalledRepeatedly()
    {
        var vm = new CveLookupViewModel();

        vm.Dispose();
        vm.Dispose();
    }

    private static async Task<LocalizationManager> CreateLocalizerAsync(string locale)
    {
        var manager = new LocalizationManager();
        await manager.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), locale);
        return manager;
    }
}
