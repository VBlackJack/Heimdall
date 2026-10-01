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
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Heimdall.App.Services;
using Heimdall.Core.Localization;
using Heimdall.Core.Network;

namespace Heimdall.App.ViewModels.Tools;

/// <summary>
/// ViewModel for the DNS batch resolver tool. Owns input parsing, busy state,
/// localized shell strings, and the final results snapshot.
/// </summary>
public sealed partial class DnsBatchResolverViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan ResolveTimeout = TimeSpan.FromSeconds(30);

    private readonly IDnsBatchResolverService _service;
    private LocalizationManager? _localizer;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    private string? _lastErrorKey;
    private string _lastRawError = string.Empty;
    private int _lastResolvedCount;
    private DateTime _lastCompletedAt;

    [ObservableProperty] private string _hostnamesInput = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _showError;
    [ObservableProperty] private string _errorText = string.Empty;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private bool _hasResults;
    [ObservableProperty] private bool _isHelpVisible;
    [ObservableProperty] private string _helpText = string.Empty;
    [ObservableProperty] private string _inputPlaceholder = string.Empty;
    [ObservableProperty] private string _emptyStateText = string.Empty;

    public DnsBatchResolverViewModel(IDnsBatchResolverService? service = null)
    {
        _service = service ?? new DnsBatchResolverService();
        Results = [];
        Results.CollectionChanged += OnResultsCollectionChanged;
    }

    public ObservableCollection<DnsBatchResolveResult> Results { get; }

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

    [RelayCommand(CanExecute = nameof(CanResolve))]
    private async Task ResolveAsync()
    {
        if (_disposed || IsBusy)
        {
            return;
        }

        ResetTransientState();

        var hostnames = ParseDistinctHostnames();
        if (hostnames.Count == 0)
        {
            SetError("ToolValidationHostRequired");
            return;
        }

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        _cts.CancelAfter(ResolveTimeout);
        var token = _cts.Token;

        IsBusy = true;
        StatusText = L("ToolDnsBatchBtnResolve");

        try
        {
            var tasks = hostnames.Select(hostname => _service.ResolveAsync(hostname, token));
            var results = await Task.WhenAll(tasks).ConfigureAwait(true);

            if (token.IsCancellationRequested)
            {
                StatusText = string.Empty;
                return;
            }

            foreach (var result in results)
            {
                Results.Add(result);
            }

            _lastResolvedCount = Results.Count;
            _lastCompletedAt = DateTime.Now;
            StatusText = BuildStatusText(_lastResolvedCount, _lastCompletedAt);
        }
        catch (OperationCanceledException)
        {
            StatusText = string.Empty;
        }
        catch (Exception ex)
        {
            _lastErrorKey = "ToolDnsErrorLookupFailed";
            _lastRawError = ex.Message;
            ShowError = true;
            ErrorText = FormatError(_lastErrorKey, _lastRawError);
            StatusText = string.Empty;
        }
        finally
        {
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        _cts?.Cancel();
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
        Results.CollectionChanged -= OnResultsCollectionChanged;
        GC.SuppressFinalize(this);
    }

    private bool CanResolve() => !IsBusy;

    private bool CanCancel() => IsBusy;

    partial void OnIsBusyChanged(bool value)
    {
        ResolveCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }

    private void OnResultsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        HasResults = Results.Count > 0;
    }

    private void OnLocaleChanged(string _)
    {
        RefreshLocalizedMessages();
    }

    private List<string> ParseDistinctHostnames()
    {
        return (HostnamesInput ?? string.Empty)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(host => !string.IsNullOrWhiteSpace(host))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void ResetTransientState()
    {
        ShowError = false;
        ErrorText = string.Empty;
        StatusText = string.Empty;
        Results.Clear();
        _lastErrorKey = null;
        _lastRawError = string.Empty;
        _lastResolvedCount = 0;
        _lastCompletedAt = default;
    }

    private void SetError(string errorKey)
    {
        _lastErrorKey = errorKey;
        _lastRawError = string.Empty;
        ShowError = true;
        ErrorText = L(errorKey);
    }

    private void RefreshLocalizedMessages()
    {
        InputPlaceholder = L("ToolDnsBatchInputPlaceholder");
        EmptyStateText = L("ToolDnsBatchInputPlaceholder");
        HelpText = L("ToolHelpDNSBATCH").Replace("\\n", "\n", StringComparison.Ordinal);

        if (ShowError && !string.IsNullOrWhiteSpace(_lastErrorKey))
        {
            ErrorText = FormatError(_lastErrorKey, _lastRawError);
        }

        if (IsBusy)
        {
            StatusText = L("ToolDnsBatchBtnResolve");
            return;
        }

        if (_lastResolvedCount > 0)
        {
            StatusText = BuildStatusText(_lastResolvedCount, _lastCompletedAt);
        }
    }

    private string BuildStatusText(int resolvedCount, DateTime completedAt)
    {
        return _localizer?.FormatCount(
            resolvedCount,
            "ToolDnsBatchStatusOne",
            "ToolDnsBatchStatus",
            resolvedCount,
            completedAt.ToString("HH:mm:ss", CultureInfo.InvariantCulture))
            ?? "ToolDnsBatchStatus";
    }

    private string FormatError(string? key, string rawError)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return string.Empty;
        }

        var template = L(key);
        return string.IsNullOrWhiteSpace(rawError)
            ? template
            : string.Format(template, rawError);
    }

    private string L(string key) => _localizer?[key] ?? key;
}
