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

using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using Heimdall.App.ViewModels;
using Heimdall.Core.Localization;

namespace Heimdall.App.Services;

/// <summary>
/// Centralized observable source for the host inherited by network tools.
/// The provider owns localization so consumers can bind to ready-to-display
/// label properties without duplicating locale refresh logic.
/// </summary>
public sealed class ToolContextProvider : ObservableObject, IToolContextProvider
{
    private readonly LocalizationManager _localizer;
    private readonly IUiDispatcher _uiDispatcher;
    private string? _targetHost;
    private string _contextLabel = string.Empty;
    private string _contextTooltip = string.Empty;
    private bool _disposed;

    /// <summary>
    /// Initializes a new <see cref="ToolContextProvider"/>.
    /// </summary>
    public ToolContextProvider(LocalizationManager localizer, IUiDispatcher uiDispatcher)
    {
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        _uiDispatcher = uiDispatcher ?? throw new ArgumentNullException(nameof(uiDispatcher));
        _localizer.LocaleChanged += OnLocaleChanged;
        RefreshLocalizedText();
    }

    /// <inheritdoc />
    public string? TargetHost
    {
        get => _targetHost;
        private set
        {
            if (SetProperty(ref _targetHost, value))
            {
                OnPropertyChanged(nameof(HasTarget));
                OnPropertyChanged(nameof(ContextBrushKey));
                RefreshLocalizedText();
            }
        }
    }

    /// <inheritdoc />
    public bool HasTarget => !string.IsNullOrEmpty(TargetHost);

    /// <inheritdoc />
    public string ContextLabel
    {
        get => _contextLabel;
        private set => SetProperty(ref _contextLabel, value);
    }

    /// <inheritdoc />
    public string ContextTooltip
    {
        get => _contextTooltip;
        private set => SetProperty(ref _contextTooltip, value);
    }

    /// <inheritdoc />
    public string ContextBrushKey => HasTarget ? "AccentBrush" : "TextSecondaryBrush";

    /// <inheritdoc />
    public void SetSelectedServer(ServerItemViewModel? server)
    {
        TargetHost = NormalizeHost(server?.RemoteServer);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _localizer.LocaleChanged -= OnLocaleChanged;
    }

    private static string? NormalizeHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return null;
        }

        return host.Trim();
    }

    private void OnLocaleChanged(string _locale)
    {
        if (!_uiDispatcher.CheckAccess())
        {
            _ = _uiDispatcher.InvokeAsync(RefreshLocalizedText);
            return;
        }

        RefreshLocalizedText();
    }

    private void RefreshLocalizedText()
    {
        var text = HasTarget
            ? _localizer.Format("ToolsNetworkContextWith", TargetHost!)
            : _localizer["ToolsNetworkContextNone"];

        ContextLabel = text;
        ContextTooltip = text;
    }
}
