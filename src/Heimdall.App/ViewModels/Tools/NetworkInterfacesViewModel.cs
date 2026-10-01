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
/// ViewModel for the network interfaces tool.
/// </summary>
public sealed partial class NetworkInterfacesViewModel : ObservableObject, IDisposable
{
    private readonly INetworkInterfacesService _service;
    private LocalizationManager? _localizer;
    private bool _disposed;
    private int _lastInterfaceCount;
    private DateTime _lastRefreshedAt;
    private bool _hasRefreshSnapshot;
    private string _lastRawStatus = string.Empty;

    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private string _helpText = string.Empty;
    [ObservableProperty] private bool _isHelpVisible;

    public NetworkInterfacesViewModel(INetworkInterfacesService? service = null)
    {
        _service = service ?? new NetworkInterfacesService();
        Interfaces = [];
        Interfaces.CollectionChanged += OnInterfacesCollectionChanged;
    }

    public ObservableCollection<NicSnapshot> Interfaces { get; }

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

        Interfaces.Clear();
        _lastRawStatus = string.Empty;

        try
        {
            foreach (var snapshot in _service.Load())
            {
                Interfaces.Add(snapshot);
            }

            _lastInterfaceCount = Interfaces.Count;
            _lastRefreshedAt = DateTime.Now;
            _hasRefreshSnapshot = true;
            StatusText = BuildStatusText(_lastInterfaceCount, _lastRefreshedAt);
        }
        catch (Exception ex)
        {
            _lastRawStatus = ex.Message;
            _hasRefreshSnapshot = false;
            StatusText = _lastRawStatus;
        }
    }

    [RelayCommand(CanExecute = nameof(CanCopyResults))]
    private void CopyResults()
    {
        if (Interfaces.Count == 0)
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

        Interfaces.CollectionChanged -= OnInterfacesCollectionChanged;
        GC.SuppressFinalize(this);
    }

    private bool CanCopyResults() => Interfaces.Count > 0;

    private void OnInterfacesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        CopyResultsCommand.NotifyCanExecuteChanged();
    }

    private void OnLocaleChanged(string _)
    {
        RefreshLocalizedMessages();
    }

    private void RefreshLocalizedMessages()
    {
        HelpText = L("ToolHelpNETIF").Replace("\\n", "\n", StringComparison.Ordinal);

        if (!string.IsNullOrWhiteSpace(_lastRawStatus))
        {
            StatusText = _lastRawStatus;
            return;
        }

        if (_hasRefreshSnapshot)
        {
            StatusText = BuildStatusText(_lastInterfaceCount, _lastRefreshedAt);
        }
    }

    private string BuildStatusText(int count, DateTime refreshedAt)
    {
        return _localizer?.FormatCount(
            count,
            "ToolNetIfStatusOne",
            "ToolNetIfStatus",
            count,
            refreshedAt.ToString("HH:mm:ss", CultureInfo.InvariantCulture))
            ?? "ToolNetIfStatus";
    }

    private string BuildClipboardText()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Name\tType\tStatus\tSpeed\tMAC\tIPv4\tSubnet\tGateway\tDHCP");
        foreach (var entry in Interfaces)
        {
            sb.Append(entry.Name).Append('\t')
              .Append(entry.InterfaceType).Append('\t')
              .Append(entry.Status).Append('\t')
              .Append(entry.Speed).Append('\t')
              .Append(entry.Mac).Append('\t')
              .Append(entry.Ipv4).Append('\t')
              .Append(entry.Subnet).Append('\t')
              .Append(entry.Gateway).Append('\t')
              .AppendLine(entry.Dhcp);
        }

        return sb.ToString();
    }

    private string L(string key) => _localizer?[key] ?? key;
}
