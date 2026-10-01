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
/// ViewModel for the open ports tool.
/// </summary>
public sealed partial class OpenPortsViewModel : ObservableObject, IDisposable
{
    private readonly IOpenPortsService _service;
    private LocalizationManager? _localizer;
    private bool _disposed;
    private int _lastPortCount;
    private DateTime _lastRefreshedAt;
    private bool _hasRefreshSnapshot;

    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private string _helpText = string.Empty;
    [ObservableProperty] private bool _isHelpVisible;

    public OpenPortsViewModel(IOpenPortsService? service = null)
    {
        _service = service ?? new OpenPortsService();
        Ports = [];
        Ports.CollectionChanged += OnPortsCollectionChanged;
    }

    public ObservableCollection<PortEntry> Ports { get; }

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

        Ports.Clear();

        foreach (var entry in _service.Load())
        {
            Ports.Add(entry);
        }

        _lastPortCount = Ports.Count;
        _lastRefreshedAt = DateTime.Now;
        _hasRefreshSnapshot = true;
        StatusText = BuildStatusText(_lastPortCount, _lastRefreshedAt);
    }

    [RelayCommand(CanExecute = nameof(CanCopyResults))]
    private void CopyResults()
    {
        if (Ports.Count == 0)
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

        Ports.CollectionChanged -= OnPortsCollectionChanged;
        GC.SuppressFinalize(this);
    }

    private bool CanCopyResults() => Ports.Count > 0;

    private void OnPortsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        CopyResultsCommand.NotifyCanExecuteChanged();
    }

    private void OnLocaleChanged(string _)
    {
        RefreshLocalizedMessages();
    }

    private void RefreshLocalizedMessages()
    {
        HelpText = L("ToolHelpOPENPORTS").Replace("\\n", "\n", StringComparison.Ordinal);

        if (_hasRefreshSnapshot)
        {
            StatusText = BuildStatusText(_lastPortCount, _lastRefreshedAt);
        }
    }

    private string BuildStatusText(int count, DateTime refreshedAt)
    {
        return _localizer?.FormatCount(
            count,
            "ToolOpenPortsStatusOne",
            "ToolOpenPortsStatus",
            count,
            refreshedAt.ToString("HH:mm:ss", CultureInfo.InvariantCulture))
            ?? "ToolOpenPortsStatus";
    }

    private string BuildClipboardText()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Protocol\tLocal Address\tLocal Port\tRemote Address\tRemote Port\tState\tPID\tProcess");
        foreach (var entry in Ports)
        {
            sb.Append(entry.Protocol).Append('\t')
              .Append(entry.LocalAddress).Append('\t')
              .Append(entry.LocalPort).Append('\t')
              .Append(entry.RemoteAddress).Append('\t')
              .Append(entry.RemotePort).Append('\t')
              .Append(entry.State).Append('\t')
              .Append(entry.Pid).Append('\t')
              .AppendLine(entry.ProcessName);
        }

        return sb.ToString();
    }

    private string L(string key) => _localizer?[key] ?? key;
}
