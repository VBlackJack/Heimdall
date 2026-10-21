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

using Heimdall.App.ViewModels;
using Heimdall.App.ViewModels.CommandLibrary;
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.Core.Localization;
using Microsoft.Extensions.DependencyInjection;
using TwinShell.Core.Models;
using TwinShell.Core.Services;
using ActionModel = TwinShell.Core.Models.Action;

namespace Heimdall.App.Tests;

/// <summary>
/// A search finds an action by the category label the operator reads, and still by the key
/// the category is stored as.
/// </summary>
/// <remarks>
/// "chiffrement" appears only in the French label of the security category, and
/// "monitoring" only in the monitoring-logs key, whose French label is "Supervision et
/// journaux": each term can reach its action through one of the two and nothing else.
/// </remarks>
public sealed class CommandCategorySearchTests
{
    [Fact]
    public void TheLibrarySearch_MatchesTheLocalizedCategoryLabel()
    {
        StaDispatcherRunner.Run(async () =>
        {
            LocalizationManager localizer = await CommandLibraryTestHelpers.CreateAppLocalizerAsync("fr");
            using var viewModel = new CommandLibraryViewModel(
                CommandLibraryTestHelpers.CreateResolverServiceProvider(SecurityAction(), OtherAction()),
                configManager: null!,
                localizer,
                new SilentDialogService(),
                gitSyncService: null!,
                transferService: null!);
            await viewModel.InitializeAsync(targetHost: null);

            await viewModel.ApplySearchAsync("chiffrement");

            var visible = viewModel.ActionsView!.Cast<CommandLibraryActionEntry>().Select(e => e.Source.Id).ToList();
            Assert.Equal(new[] { "sec" }, visible);
        });
    }

    [Fact]
    public async Task TheSearchService_StillMatchesTheStoredKey()
    {
        LocalizationManager localizer = await CommandLibraryTestHelpers.CreateAppLocalizerAsync("fr");
        var monitoring = CommandLibraryTestHelpers.CreateLinuxAction("mon", "Tail", "tail -f x");
        monitoring.Category = CommandCategories.MonitoringLogs;

        var found = await new SearchService().SearchAsync(
            [monitoring, OtherAction()],
            "monitoring",
            category => CommandPresentationResolver.ResolveCategoryLabel(category, key => localizer[key]));

        Assert.Equal("mon", Assert.Single(found).Id);
    }

    [Fact]
    public async Task ThePickerSearch_MatchesTheLabelAndTheKey()
    {
        LocalizationManager localizer = await CommandLibraryTestHelpers.CreateAppLocalizerAsync("fr");
        var provider = CommandLibraryTestHelpers.CreateResolverServiceProvider(SecurityAction(), OtherAction());
        var picker = new CommandLibraryPickerDialogViewModel(localizer, provider.GetRequiredService<IServiceScopeFactory>());
        await picker.InitializeAsync(null);

        picker.SearchText = "chiffrement";
        Assert.Equal("sec", Assert.Single(picker.ActionsView.Cast<CommandLibraryPickerItem>()).ActionId);

        picker.SearchText = "security";
        Assert.Equal("sec", Assert.Single(picker.ActionsView.Cast<CommandLibraryPickerItem>()).ActionId);
    }

    private static ActionModel SecurityAction()
    {
        var action = CommandLibraryTestHelpers.CreateLinuxAction("sec", "Audit", "echo audit");
        action.Category = CommandCategories.Security;
        return action;
    }

    private static ActionModel OtherAction()
    {
        var action = CommandLibraryTestHelpers.CreateLinuxAction("other", "List files", "ls -la");
        action.Category = CommandCategories.FilesStorage;
        return action;
    }
}
