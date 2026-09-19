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
using Heimdall.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using TwinShell.Core.Enums;
using TwinShell.Core.Interfaces;
using TwinShell.Core.Services;

namespace Heimdall.App.Tests;

/// <summary>
/// Shared harness for the Command Library broadcast tests.
/// </summary>
internal static class CommandLibraryBroadcastHarness
{
    internal static FakeBroadcaster Broadcaster(
        params (string Id, string Name, bool IsOrigin)[] targets)
    {
        var fake = new FakeBroadcaster();
        foreach (var (id, name, isOrigin) in targets)
        {
            fake.Targets.Add(new CommandBroadcastTarget(id, name, "SSH", isOrigin));
        }

        return fake;
    }

    internal static Task<CommandLibraryViewModel> CreateAsync(
        FakeBroadcaster? broadcaster, bool confirm = true)
        => CreateAsync(broadcaster, new SilentDialogService { ConfirmResult = confirm });

    internal static Task<CommandLibraryViewModel> CreateAsync(
        FakeBroadcaster? broadcaster, SilentDialogService dialog)
        => CreateAsync(broadcaster, dialog, CriticalityLevel.Info);

    internal static async Task<CommandLibraryViewModel> CreateAsync(
        FakeBroadcaster? broadcaster, SilentDialogService dialog, CriticalityLevel level)
    {
        var action = CommandLibraryTestHelpers.CreateLinuxAction("a", "Alpha", "uptime");
        action.Level = level;

        var services = new ServiceCollection();
        services.AddSingleton<ILocalizationService, FakeTwinShellLocalizationService>();
        services.AddScoped<IActionService>(_ => new FakeActionService([action]));
        services.AddScoped<IFavoritesService, FakeFavoritesService>();
        services.AddScoped<ISearchService, SearchService>();
        services.AddScoped<ICommandGeneratorService, CommandGeneratorService>();

        var viewModel = new CommandLibraryViewModel(
            services.BuildServiceProvider(),
            configManager: null!,
            await CommandLibraryTestHelpers.CreateAppLocalizerAsync(),
            dialog,
            gitSyncService: null!,
            transferService: null!)
        {
            CommandBroadcaster = broadcaster
        };

        await viewModel.InitializeAsync(targetHost: null);
        viewModel.RefreshBroadcastTargets();
        return viewModel;
    }

    /// <summary>
    /// Stands in for the session manager's broadcaster. It records what it was asked to send in
    /// order, which is what separates "reached three terminals" from "reached one, three times".
    /// </summary>
    internal sealed class FakeBroadcaster : ICommandBroadcaster
    {
        public List<CommandBroadcastTarget> Targets { get; } = [];

        /// <summary>Target ids that report the command was not delivered.</summary>
        public HashSet<string> Refuse { get; } = new(StringComparer.Ordinal);

        public List<(string TargetId, string Command)> Sent { get; } = [];

        public IReadOnlyList<CommandBroadcastTarget> GetTargets() => [.. Targets];

        public bool Send(string targetId, string command)
        {
            if (Refuse.Contains(targetId) || !Targets.Any(t => t.Id == targetId))
            {
                return false;
            }

            Sent.Add((targetId, command));
            return true;
        }
    }
}
