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
using Heimdall.Core.Logging;
using Heimdall.Core.Network;

namespace Heimdall.App.ViewModels.Tools;

/// <summary>
/// ViewModel for the Wi-Fi networks tool.
/// </summary>
public sealed partial class WifiNetworksViewModel : ObservableObject, IDisposable
{
    private readonly IWifiScanService _service;
    private LocalizationManager? _localizer;
    private bool _disposed;
    private int _lastNetworkCount;
    private DateTime _lastCompletedAt;
    private bool _hasScanSnapshot;

    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private string _helpText = string.Empty;
    [ObservableProperty] private bool _isHelpVisible;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private string _errorText = string.Empty;

    public WifiNetworksViewModel(IWifiScanService? service = null)
    {
        _service = service ?? new WifiScanService();
        Networks = [];
        Networks.CollectionChanged += OnNetworksCollectionChanged;
    }

    public ObservableCollection<WifiEntry> Networks { get; }

    /// <summary>The sentence the empty results panel shows before a scan.</summary>
    public string EmptyStateText => L("ToolWifiEmptyState");

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

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanAsync()
    {
        if (_disposed || IsBusy)
        {
            return;
        }

        Networks.Clear();
        HasError = false;
        ErrorText = string.Empty;
        IsBusy = true;

        try
        {
            var entries = await _service.ScanAsync().ConfigureAwait(true);

            foreach (var entry in entries)
            {
                Networks.Add(entry);
            }

            _lastNetworkCount = Networks.Count;
            _lastCompletedAt = DateTime.Now;
            _hasScanSnapshot = true;
            StatusText = BuildStatusText(_lastNetworkCount, _lastCompletedAt);
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"WifiNetworks scan failed: {ex.Message}");
            ErrorText = ex.Message;
            HasError = true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanCopyResults))]
    private void CopyResults()
    {
        if (Networks.Count == 0)
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

        Networks.CollectionChanged -= OnNetworksCollectionChanged;
        GC.SuppressFinalize(this);
    }

    private bool CanScan() => !IsBusy;

    private bool CanCopyResults() => Networks.Count > 0;

    partial void OnIsBusyChanged(bool value)
    {
        ScanCommand.NotifyCanExecuteChanged();
    }

    private void OnNetworksCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        CopyResultsCommand.NotifyCanExecuteChanged();
    }

    private void OnLocaleChanged(string _)
    {
        RefreshLocalizedMessages();
    }

    private void RefreshLocalizedMessages()
    {
        HelpText = L("ToolHelpWIFI").Replace("\\n", "\n", StringComparison.Ordinal);

        if (_hasScanSnapshot)
        {
            StatusText = BuildStatusText(_lastNetworkCount, _lastCompletedAt);
        }
    }

    private string BuildStatusText(int count, DateTime completedAt)
    {
        return _localizer?.FormatCount(
            count,
            "ToolWifiStatusOne",
            "ToolWifiStatus",
            count,
            completedAt.ToString("HH:mm:ss", CultureInfo.InvariantCulture))
            ?? "ToolWifiStatus";
    }

    private string BuildClipboardText()
    {
        var sb = new StringBuilder();
        sb.Append(L("ToolWifiColSsid")).Append('\t')
          .Append(L("ToolWifiColBssid")).Append('\t')
          .Append(L("ToolWifiColSignal")).Append('\t')
          .Append(L("ToolWifiColChannel")).Append('\t')
          .Append(L("ToolWifiColAuth")).Append('\t')
          .Append(L("ToolWifiColEncryption")).Append('\t')
          .AppendLine(L("ToolWifiColRadio"));

        foreach (var entry in Networks)
        {
            sb.Append(entry.Ssid).Append('\t')
              .Append(entry.Bssid).Append('\t')
              .Append(entry.Signal).Append('\t')
              .Append(entry.Channel).Append('\t')
              .Append(entry.Auth).Append('\t')
              .Append(entry.Encryption).Append('\t')
              .AppendLine(entry.RadioType);
        }

        return sb.ToString();
    }

    private string L(string key) => _localizer?[key] ?? key;
}
