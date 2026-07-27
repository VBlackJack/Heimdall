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
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Heimdall.App.Services;
using Heimdall.Core.Configuration;
using Heimdall.Core.Discovery;
using Heimdall.Core.Localization;
using Heimdall.Core.Security;

namespace Heimdall.App.ViewModels.Tools;

/// <summary>
/// ViewModel for the SMB Enumerator tool.
/// </summary>
public sealed partial class SmbEnumeratorViewModel : ObservableObject, IDisposable
{
    private readonly ISmbEnumerationService _service;
    private LocalizationManager? _localizer;
    private CancellationTokenSource? _cts;
    private bool _disposed;
    private SmbEnumerationResult? _lastResult;
    private string? _lastErrorKey;
    private string _lastErrorArg = string.Empty;

    [ObservableProperty] private string _hostInput = string.Empty;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private string _errorMessage = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private SmbEnumerationResult? _result;

    public SmbEnumeratorViewModel(ISmbEnumerationService? service = null)
    {
        _service = service ?? new SmbEnumerationService();
        Findings = [];
    }

    /// <summary>
    /// Gets the localized findings collection bound by the view.
    /// </summary>
    public ObservableCollection<SmbFindingViewItem> Findings { get; }

    /// <summary>
    /// Gets whether a result is available.
    /// </summary>
    public bool HasResults => Result is not null;

    /// <summary>
    /// Gets whether the findings section should be visible.
    /// </summary>
    public bool HasFindings => Findings.Count > 0;

    /// <summary>
    /// Gets whether the protocol card should be visible.
    /// </summary>
    public bool HasProtocolSection => Result?.DialectRaw is not null;

    /// <summary>
    /// Gets whether enumeration can be started or the active enumeration can be cancelled.
    /// </summary>
    public bool CanToggleEnumeration => IsBusy || !string.IsNullOrWhiteSpace(HostInput);

    /// <summary>
    /// Gets the last localized report.
    /// </summary>
    public string LastReport => Result?.Report ?? string.Empty;

    /// <summary>
    /// Updates the localization source and reprojects existing data.
    /// </summary>
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

        Relocalize();
        RefreshLocalizedMessages();
    }

    /// <summary>
    /// Updates the gateway used by the service.
    /// </summary>
    public void SetGateway(SshGatewayDto? gateway)
    {
        _service.SetGateway(gateway);
    }

    [RelayCommand(CanExecute = nameof(CanExecuteEnumerate))]
    private async Task EnumerateAsync()
    {
        if (IsBusy)
        {
            return;
        }

        ResetState();

        var host = HostInput.Trim();
        if (string.IsNullOrWhiteSpace(host))
        {
            _lastErrorKey = "ToolValidationHostRequired";
            _lastErrorArg = string.Empty;
            HasError = true;
            ErrorMessage = L("ToolValidationHostRequired");
            return;
        }

        if (!InputValidator.Validate(host, "Address"))
        {
            _lastErrorKey = "ErrorInvalidHost";
            _lastErrorArg = string.Empty;
            HasError = true;
            ErrorMessage = L("ErrorInvalidHost");
            return;
        }

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        IsBusy = true;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var outcome = await _service.EnumerateAsync(new SmbEnumInputs(host), ct);
            if (ct.IsCancellationRequested)
            {
                return;
            }

            if (outcome.Result is null)
            {
                _lastErrorKey = outcome.ErrorKey ?? "ToolSmbErrorConnection";
                _lastErrorArg = outcome.ErrorArg ?? string.Empty;
                HasError = true;
                ErrorMessage = FormatError(_lastErrorKey, _lastErrorArg);
                return;
            }

            stopwatch.Stop();
            StatusMessage = $"{stopwatch.ElapsedMilliseconds} ms";
            _lastResult = outcome.Result;
            Relocalize();
        }
        catch (OperationCanceledException)
        {
            // Operation was cancelled by the user.
        }
        catch (Exception ex)
        {
            _lastErrorKey = "ToolSmbErrorConnection";
            _lastErrorArg = ex.Message;
            HasError = true;
            ErrorMessage = FormatError(_lastErrorKey, _lastErrorArg);
        }
        finally
        {
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    [RelayCommand(CanExecute = nameof(CanExecuteCancel))]
    private void Cancel()
    {
        _cts?.Cancel();
    }

    private bool CanExecuteEnumerate() => !IsBusy && !string.IsNullOrWhiteSpace(HostInput);

    private bool CanExecuteCancel() => IsBusy;

    partial void OnHostInputChanged(string value)
    {
        EnumerateCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanToggleEnumeration));
    }

    partial void OnIsBusyChanged(bool value)
    {
        EnumerateCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanToggleEnumeration));
    }

    partial void OnResultChanged(SmbEnumerationResult? value)
    {
        _ = value;
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(HasProtocolSection));
        OnPropertyChanged(nameof(LastReport));
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
        }

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    private void ResetState()
    {
        HasError = false;
        ErrorMessage = string.Empty;
        StatusMessage = string.Empty;
        _lastResult = null;
        _lastErrorKey = null;
        _lastErrorArg = string.Empty;
        Findings.Clear();
        Result = null;
        OnPropertyChanged(nameof(HasFindings));
    }

    private void OnLocaleChanged(string _)
    {
        Relocalize();
        RefreshLocalizedMessages();
    }

    private void Relocalize()
    {
        if (_lastResult is null)
        {
            Findings.Clear();
            Result = null;
            OnPropertyChanged(nameof(HasFindings));
            return;
        }

        var localizedResult = _lastResult with
        {
            Report = SmbEnumerationEngine.BuildReport(_lastResult, L),
        };

        Result = localizedResult;
        Findings.Clear();
        foreach (var finding in localizedResult.Findings)
        {
            Findings.Add(ProjectFinding(finding));
        }

        OnPropertyChanged(nameof(HasFindings));
    }

    private void RefreshLocalizedMessages()
    {
        if (HasError && !string.IsNullOrWhiteSpace(_lastErrorKey))
        {
            ErrorMessage = FormatError(_lastErrorKey, _lastErrorArg);
        }
    }

    private SmbFindingViewItem ProjectFinding(SmbFinding finding)
    {
        var template = L(finding.MessageKey);
        var text = finding.MessageArgs is { Count: > 0 }
            ? string.Format(template, finding.MessageArgs.Cast<object>().ToArray())
            : template;

        return new SmbFindingViewItem(finding.Severity, text);
    }

    private string L(string key) => _localizer?[key] ?? key;

    private string FormatError(string key, string arg)
    {
        var template = L(key);
        if (string.IsNullOrWhiteSpace(arg))
        {
            return template;
        }

        return template.Contains("{0}", StringComparison.Ordinal)
            ? string.Format(template, arg)
            : $"{template}: {arg}";
    }
}

/// <summary>
/// Localized finding projected for WPF bindings.
/// </summary>
public sealed record SmbFindingViewItem(SmbFindingSeverity Severity, string Text);
