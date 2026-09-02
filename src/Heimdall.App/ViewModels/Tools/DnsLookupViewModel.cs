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
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Heimdall.App.Services;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Network;
using Heimdall.Core.Security;

namespace Heimdall.App.ViewModels.Tools;

/// <summary>
/// ViewModel for the DNS lookup tool. Owns observable UI state and delegates
/// the actual query to <see cref="IDnsLookupService"/>. All localized strings
/// are re-projected when the culture changes via <see cref="LocalizationManager.LocaleChanged"/>.
/// </summary>
public sealed partial class DnsLookupViewModel : ObservableObject, IDisposable
{
    /// <summary>
    /// Client-side hard cap on a single lookup attempt. Mirrors the previous
    /// behaviour of the code-behind view.
    /// </summary>
    private static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(5);

    private static readonly DnsRecordType[] AllRecordTypes =
    [
        DnsRecordType.A,
        DnsRecordType.AAAA,
        DnsRecordType.MX,
        DnsRecordType.CNAME,
        DnsRecordType.TXT,
        DnsRecordType.NS,
        DnsRecordType.PTR,
        DnsRecordType.SOA,
        DnsRecordType.ANY,
    ];

    private readonly IDnsLookupService _service;
    private CancellationTokenSource? _cts;
    private LocalizationManager? _localizer;
    private bool _disposed;
    private bool _userCancelled;

    // Snapshot of the last successful lookup, used for re-projection.
    private string _lastOutput = string.Empty;
    private string _lastRecordToken = string.Empty;
    private string _lastHostname = string.Empty;
    private long _lastElapsedMs;

    // Snapshot of the last failure, used for re-projection on locale change.
    private string? _lastErrorKey;
    private object[] _lastErrorArgs = [];

    [ObservableProperty] private string _hostname = string.Empty;
    [ObservableProperty] private int _selectedRecordTypeIndex;
    [ObservableProperty] private int _selectedDnsServerIndex;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _showError;
    [ObservableProperty] private string _errorText = string.Empty;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private bool _hasResults;
    [ObservableProperty] private string _resultHeader = string.Empty;
    [ObservableProperty] private string _results = string.Empty;
    [ObservableProperty] private bool _isHelpVisible;
    [ObservableProperty] private string _helpText = string.Empty;
    [ObservableProperty] private string _hostnameWatermark = string.Empty;
    [ObservableProperty] private string _emptyStateText = string.Empty;

    public DnsLookupViewModel(IDnsLookupService? service = null)
    {
        _service = service ?? new DnsLookupService();
        RecordTypes = new ObservableCollection<string>(AllRecordTypes.Select(t => t.ToWireFormat()));
        DnsServers = [];
        RebuildDnsServerLabels();
    }

    /// <summary>
    /// Record type tokens (uppercase), kept untranslated.
    /// </summary>
    public ObservableCollection<string> RecordTypes { get; }

    /// <summary>
    /// Localized DNS server preset labels, in the same order as
    /// <see cref="NetworkToolPresets.DnsServers"/>. Null address entries map
    /// to the system default resolver.
    /// </summary>
    public ObservableCollection<string> DnsServers { get; }

    public void Initialize(LocalizationManager? localizer) => UpdateLocalizer(localizer);

    public void SetGateway(SshGatewayDto? gateway) => _service.SetGateway(gateway);

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

        RebuildDnsServerLabels();
        RefreshLocalizedMessages();
    }

    [RelayCommand(CanExecute = nameof(CanLookup))]
    private async Task LookupAsync()
    {
        if (IsBusy)
        {
            return;
        }

        ResetTransientState();

        var hostname = (Hostname ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(hostname))
        {
            SetError("ToolValidationHostRequired");
            return;
        }

        if (!InputValidator.ValidateDomain(hostname))
        {
            SetError("ToolValidationInvalidHost");
            return;
        }

        var recordType = AllRecordTypes[ClampIndex(SelectedRecordTypeIndex, AllRecordTypes.Length)];
        var recordToken = recordType.ToWireFormat();
        var dnsServer = ResolveSelectedDnsServer();

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        _cts.CancelAfter(LookupTimeout);
        var token = _cts.Token;

        _userCancelled = false;
        IsBusy = true;
        StatusText = L("ToolDnsStatusQuerying");

        try
        {
            var request = new DnsLookupRequest(hostname, recordType, dnsServer);
            var result = await _service.LookupAsync(request, L, token).ConfigureAwait(true);

            if (token.IsCancellationRequested && !result.Success && _userCancelled)
            {
                StatusText = string.Empty;
                return;
            }

            if (result.Success)
            {
                _lastOutput = result.Output;
                _lastRecordToken = recordToken;
                _lastHostname = hostname;
                _lastElapsedMs = result.ElapsedMs;
                _lastErrorKey = null;
                _lastErrorArgs = [];

                HasResults = true;
                ResultHeader = string.Format(L("ToolDnsResultHeader"), recordToken, hostname);
                Results = _lastOutput;
                StatusText = string.Format(L("ToolDnsStatusComplete"), _lastElapsedMs);
                return;
            }

            _lastErrorKey = result.ErrorKey;
            _lastErrorArgs = result.ErrorArg is null ? [] : [result.ErrorArg];
            ShowError = true;
            ErrorText = FormatError(_lastErrorKey, _lastErrorArgs);
            StatusText = string.Empty;
        }
        catch (OperationCanceledException)
        {
            if (_userCancelled)
            {
                StatusText = string.Empty;
                return;
            }

            _lastErrorKey = "ToolDnsErrorTimeout";
            _lastErrorArgs = [];
            ShowError = true;
            ErrorText = L("ToolDnsErrorTimeout");
        }
        catch (Exception ex)
        {
            _lastErrorKey = "ToolDnsErrorLookupFailed";
            _lastErrorArgs = [ex.Message];
            ShowError = true;
            ErrorText = string.Format(L("ToolDnsErrorLookupFailed"), ex.Message);
        }
        finally
        {
            IsBusy = false;
            _userCancelled = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        _userCancelled = true;
        _cts?.Cancel();
    }

    [RelayCommand(CanExecute = nameof(CanCopyResults))]
    private void CopyResults()
    {
        if (string.IsNullOrEmpty(Results))
        {
            return;
        }

        try
        {
            Clipboard.SetText(Results);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Clipboard locked by another process - swallow and let the UI stay responsive.
        }
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

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        GC.SuppressFinalize(this);
    }

    private bool CanLookup() => !IsBusy;

    private bool CanCancel() => IsBusy;

    private bool CanCopyResults() => !string.IsNullOrEmpty(Results);

    partial void OnIsBusyChanged(bool value)
    {
        LookupCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }

    partial void OnResultsChanged(string value)
    {
        CopyResultsCommand.NotifyCanExecuteChanged();
    }

    private void OnLocaleChanged(string _)
    {
        RebuildDnsServerLabels();
        RefreshLocalizedMessages();
    }

    private void ResetTransientState()
    {
        ShowError = false;
        ErrorText = string.Empty;
        HasResults = false;
        ResultHeader = string.Empty;
        Results = string.Empty;
        StatusText = string.Empty;
        _lastOutput = string.Empty;
        _lastRecordToken = string.Empty;
        _lastHostname = string.Empty;
        _lastElapsedMs = 0;
        _lastErrorKey = null;
        _lastErrorArgs = [];
    }

    private void SetError(string errorKey)
    {
        _lastErrorKey = errorKey;
        _lastErrorArgs = [];
        ShowError = true;
        ErrorText = L(errorKey);
    }

    private string? ResolveSelectedDnsServer()
    {
        var presets = NetworkToolPresets.DnsServers;
        var index = ClampIndex(SelectedDnsServerIndex, presets.Length);
        return presets[index].Address;
    }

    private static int ClampIndex(int value, int length)
    {
        if (length <= 0)
        {
            return 0;
        }

        if (value < 0)
        {
            return 0;
        }

        if (value >= length)
        {
            return length - 1;
        }

        return value;
    }

    private void RebuildDnsServerLabels()
    {
        DnsServers.Clear();
        foreach (var preset in NetworkToolPresets.DnsServers)
        {
            DnsServers.Add(L(preset.LabelKey));
        }

        if (SelectedDnsServerIndex < 0 || SelectedDnsServerIndex >= DnsServers.Count)
        {
            SelectedDnsServerIndex = 0;
        }
    }

    private void RefreshLocalizedMessages()
    {
        HostnameWatermark = L("ToolWatermarkExampleDomain");
        EmptyStateText = L("ToolEmptyStateDns");
        HelpText = L("ToolHelpDNS").Replace("\\n", "\n", StringComparison.Ordinal);

        if (ShowError && !string.IsNullOrWhiteSpace(_lastErrorKey))
        {
            ErrorText = FormatError(_lastErrorKey, _lastErrorArgs);
        }

        if (IsBusy)
        {
            StatusText = L("ToolDnsStatusQuerying");
            return;
        }

        if (HasResults && _lastElapsedMs > 0)
        {
            ResultHeader = string.Format(L("ToolDnsResultHeader"), _lastRecordToken, _lastHostname);
            StatusText = string.Format(L("ToolDnsStatusComplete"), _lastElapsedMs);
        }
    }

    private string FormatError(string? key, object[] args)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return string.Empty;
        }

        var template = L(key);
        return args.Length == 0 ? template : string.Format(template, args);
    }

    private string L(string key) => _localizer?[key] ?? key;
}
