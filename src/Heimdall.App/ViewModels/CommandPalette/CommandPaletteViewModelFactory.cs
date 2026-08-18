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
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Microsoft.Extensions.DependencyInjection;

namespace Heimdall.App.ViewModels.CommandPalette;

/// <summary>
/// Builds command palettes, holding the dependencies only the palette uses.
/// </summary>
/// <remarks>
/// These dependencies used to be declared on the owning view model purely so it could pass them on.
/// Holding them here means the owner no longer names collaborators it never speaks to.
/// </remarks>
public sealed class CommandPaletteViewModelFactory : ICommandPaletteViewModelFactory
{
    private readonly LocalizationManager _localizer;
    private readonly IDialogService _dialogService;
    private readonly ToolRegistry _toolRegistry;
    private readonly IConfigManager _configManager;
    private readonly IEmbeddedSessionManager _embeddedSessionManager;
    private readonly ExternalToolLaunchService _externalToolLaunchService;
    private readonly IRecentConnectionTracker _recentConnections;
    private readonly IServiceScopeFactory _scopeFactory;

    public CommandPaletteViewModelFactory(
        LocalizationManager localizer,
        IDialogService dialogService,
        ToolRegistry toolRegistry,
        IConfigManager configManager,
        IEmbeddedSessionManager embeddedSessionManager,
        ExternalToolLaunchService externalToolLaunchService,
        IRecentConnectionTracker recentConnections,
        IServiceScopeFactory scopeFactory)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(dialogService);
        ArgumentNullException.ThrowIfNull(toolRegistry);
        ArgumentNullException.ThrowIfNull(configManager);
        ArgumentNullException.ThrowIfNull(embeddedSessionManager);
        ArgumentNullException.ThrowIfNull(externalToolLaunchService);
        ArgumentNullException.ThrowIfNull(recentConnections);
        ArgumentNullException.ThrowIfNull(scopeFactory);

        _localizer = localizer;
        _dialogService = dialogService;
        _toolRegistry = toolRegistry;
        _configManager = configManager;
        _embeddedSessionManager = embeddedSessionManager;
        _externalToolLaunchService = externalToolLaunchService;
        _recentConnections = recentConnections;
        _scopeFactory = scopeFactory;
    }

    /// <inheritdoc />
    public CommandPaletteViewModel Create(MainViewModel owner)
    {
        ArgumentNullException.ThrowIfNull(owner);

        return new CommandPaletteViewModel(
            owner,
            _localizer,
            _dialogService,
            _toolRegistry,
            _configManager,
            _embeddedSessionManager,
            _externalToolLaunchService,
            _recentConnections,
            _scopeFactory);
    }
}
