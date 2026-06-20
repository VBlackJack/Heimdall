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
using TwinShell.Core.Enums;

namespace Heimdall.App.Tests;

public sealed class CommandLibraryActionEntryTests
{
    [Theory]
    [InlineData(CriticalityLevel.Info, "TextSecondaryBrush")]
    [InlineData(CriticalityLevel.Run, "WarningBrush")]
    [InlineData(CriticalityLevel.Dangerous, "ErrorBrush")]
    [InlineData((CriticalityLevel)999, "TextSecondaryBrush")]
    public void RiskBrushKey_MapsCriticalityLevel_ToExpectedResourceKey(
        CriticalityLevel level,
        string expectedResourceKey)
    {
        var action = CommandLibraryTestHelpers.CreateLinuxAction("action-1", "Test action", "echo ok");
        action.Level = level;

        var entry = new CommandLibraryActionEntry(action, null!);

        Assert.Equal(expectedResourceKey, entry.RiskBrushKey);
    }

    /// <summary>
    /// The entry must delegate its risk mappings to <see cref="CommandPresentationResolver"/>
    /// (the single source of truth), yielding identical brush key / label / badge for every
    /// level, including the out-of-range default branch.
    /// </summary>
    [Theory]
    [InlineData(CriticalityLevel.Info)]
    [InlineData(CriticalityLevel.Run)]
    [InlineData(CriticalityLevel.Dangerous)]
    [InlineData((CriticalityLevel)999)]
    public async Task RiskMappings_MatchResolver_ForEveryLevel(CriticalityLevel level)
    {
        var viewModel = await CreateLocalizerOnlyViewModelAsync();
        var action = CommandLibraryTestHelpers.CreateLinuxAction("action-1", "Test action", "echo ok");
        action.Level = level;

        var entry = new CommandLibraryActionEntry(action, viewModel);

        Assert.Equal(CommandPresentationResolver.ResolveRiskBrushKey(level), entry.RiskBrushKey);
        Assert.Equal(
            CommandPresentationResolver.ResolveRiskLabel(level, viewModel.LocalizeKey),
            entry.RiskLabel);
        Assert.Equal(
            CommandPresentationResolver.ResolveRiskBadge(level, viewModel.LocalizeKey),
            entry.RiskBadge);
    }

    /// <summary>
    /// The entry must delegate its platform label to <see cref="CommandPresentationResolver"/>,
    /// yielding an identical label for every platform, including the out-of-range default branch.
    /// </summary>
    [Theory]
    [InlineData(Platform.Windows)]
    [InlineData(Platform.Linux)]
    [InlineData(Platform.Both)]
    [InlineData((Platform)999)]
    public async Task PlatformLabel_MatchesResolver_ForEveryPlatform(Platform platform)
    {
        var viewModel = await CreateLocalizerOnlyViewModelAsync();
        var action = CommandLibraryTestHelpers.CreateLinuxAction("action-1", "Test action", "echo ok");
        action.Platform = platform;

        var entry = new CommandLibraryActionEntry(action, viewModel);

        Assert.Equal(
            CommandPresentationResolver.ResolvePlatformLabel(platform, viewModel.LocalizeKey),
            entry.PlatformLabel);
    }

    /// <summary>
    /// Builds a view-model with only a real localizer wired up. The constructor merely
    /// assigns its dependencies, so the unused services can be null for these label-only
    /// assertions that exercise <see cref="CommandLibraryViewModel.LocalizeKey"/>.
    /// </summary>
    private static async Task<CommandLibraryViewModel> CreateLocalizerOnlyViewModelAsync()
    {
        var localizer = await CommandLibraryTestHelpers.CreateAppLocalizerAsync();
        return new CommandLibraryViewModel(null!, null!, localizer, null!, null!, null!);
    }
}
