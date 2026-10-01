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

using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Heimdall.App.Services;
using Heimdall.Core.Localization;
using Heimdall.Core.Network;

namespace Heimdall.App.ViewModels.Tools;

/// <summary>
/// ViewModel for the route table tool.
/// </summary>
public sealed partial class RouteTableViewModel : ObservableObject, IDisposable
{
    private readonly IRouteTableService _service;
    private LocalizationManager? _localizer;
    private bool _disposed;
    private int _lastRouteCount;
    private DateTime _lastRefreshedAt;
    private bool _hasRefreshSnapshot;

    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private string _helpText = string.Empty;
    [ObservableProperty] private bool _isHelpVisible;

    public RouteTableViewModel(IRouteTableService? service = null)
    {
        _service = service ?? new RouteTableService();
        Routes = [];
        Routes.CollectionChanged += OnRoutesCollectionChanged;
    }

    public ObservableCollection<RouteEntry> Routes { get; }

    public event EventHandler<string>? CopyResultsRequested;

    public void Initialize(LocalizationManager? localizer) => UpdateLocalizer(localizer);

    public void UpdateLocalizer(LocalizationManager? localizer)
    {
        if (ReferenceEquals(_localizer, localizer))
        {
            return;
        }

        if (_localizer is not null)
        {
            _localizer.LocaleChanged -= OnLocaleChanged;
        }

        _localizer = localizer;
        if (_localizer is not null)
        {
            _localizer.LocaleChanged += OnLocaleChanged;
        }

        RefreshLocalizedMessages();
    }

    [RelayCommand]
    private void Refresh()
    {
        if (_disposed)
        {
            return;
        }

        Routes.Clear();

        foreach (var entry in _service.Load())
        {
            Routes.Add(entry);
        }

        _lastRouteCount = Routes.Count;
        _lastRefreshedAt = DateTime.Now;
        _hasRefreshSnapshot = true;
        StatusText = BuildStatusText(_lastRouteCount, _lastRefreshedAt);
    }

    [RelayCommand(CanExecute = nameof(CanCopyResults))]
    private void CopyResults()
    {
        if (Routes.Count == 0)
        {
            return;
        }

        CopyResultsRequested?.Invoke(this, BuildClipboardText());
    }

    [RelayCommand]
    private void ToggleHelp()
    {
        IsHelpVisible = !IsHelpVisible;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_localizer is not null)
        {
            _localizer.LocaleChanged -= OnLocaleChanged;
            _localizer = null;
        }

        Routes.CollectionChanged -= OnRoutesCollectionChanged;
        GC.SuppressFinalize(this);
    }

    private bool CanCopyResults() => Routes.Count > 0;

    private void OnRoutesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        CopyResultsCommand.NotifyCanExecuteChanged();
    }

    private void OnLocaleChanged(string _)
    {
        RefreshLocalizedMessages();
    }

    private void RefreshLocalizedMessages()
    {
        HelpText = L("ToolHelpROUTES").Replace("\\n", "\n", StringComparison.Ordinal);

        if (_hasRefreshSnapshot)
        {
            StatusText = BuildStatusText(_lastRouteCount, _lastRefreshedAt);
        }
    }

    private string BuildStatusText(int count, DateTime refreshedAt)
    {
        return _localizer?.FormatCount(
            count,
            "ToolRouteTableStatusOne",
            "ToolRouteTableStatus",
            count,
            refreshedAt.ToString("HH:mm:ss", CultureInfo.InvariantCulture))
            ?? "ToolRouteTableStatus";
    }

    private string BuildClipboardText()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Destination\tMask\tGateway\tInterface\tMetric");
        foreach (var entry in Routes)
        {
            sb.Append(entry.Destination).Append('\t')
              .Append(entry.Mask).Append('\t')
              .Append(entry.Gateway).Append('\t')
              .Append(entry.Interface).Append('\t')
              .AppendLine(entry.Metric);
        }

        return sb.ToString();
    }

    private string L(string key) => _localizer?[key] ?? key;
}
