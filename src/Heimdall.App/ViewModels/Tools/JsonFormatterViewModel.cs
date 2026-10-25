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

using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Heimdall.App.Services;
using Heimdall.Core.Codecs;
using Heimdall.Core.Localization;

namespace Heimdall.App.ViewModels.Tools;

public sealed partial class JsonFormatterViewModel : ObservableObject, IDisposable
{
    private readonly IJsonFormatterToolService _service;
    private LocalizationManager? _localizer;
    private CancellationTokenSource? _cts;
    private bool _initialized;
    private bool _disposed;
    private JsonStatusKind _lastStatusKind = JsonStatusKind.None;
    private int _lastStatusLength;
    private long? _lastStatusLine;
    private long? _lastStatusColumn;
    private string _lastStatusMessage = string.Empty;

    [ObservableProperty] private string _inputText = string.Empty;
    [ObservableProperty] private string _outputText = string.Empty;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private string _statusForegroundBrushKey = "TextSecondaryBrush";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmptyState))]
    private bool _hasError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmptyState))]
    private bool _hasResult;

    [ObservableProperty] private bool _isProcessing;

    public bool IsEmptyState => !HasError && !HasResult;

    public JsonFormatterViewModel(IJsonFormatterToolService? service = null)
    {
        _service = service ?? new JsonFormatterToolService();
    }

    public void Initialize(LocalizationManager? localizer)
    {
        if (ReferenceEquals(_localizer, localizer))
        {
            _initialized = true;
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

        _initialized = true;
        RefreshStatusText();
    }

    public void PrefillInput(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return;
        }

        InputText = input;
    }

    [RelayCommand(CanExecute = nameof(CanFormat))]
    private Task PrettifyAsync() => FormatAsync(true);

    [RelayCommand(CanExecute = nameof(CanFormat))]
    private Task MinifyAsync() => FormatAsync(false);

    partial void OnInputTextChanged(string value)
    {
        if (!_initialized)
        {
            return;
        }

        OutputText = string.Empty;
        HasError = false;
        HasResult = false;
        SetStatus(JsonStatusKind.None);
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
        GC.SuppressFinalize(this);
    }

    private bool CanFormat() => !_disposed && !IsProcessing;

    private async Task FormatAsync(bool indented)
    {
        if (_disposed)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(InputText))
        {
            OutputText = string.Empty;
            HasError = false;
            HasResult = false;
            SetStatus(JsonStatusKind.None);
            return;
        }

        using var cts = ReplaceCancellationTokenSource();
        try
        {
            SetStatus(JsonStatusKind.Processing);
            var result = await _service.FormatAsync(InputText, indented, cts.Token).ConfigureAwait(false);
            ApplyResult(result, indented);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        finally
        {
            CompleteOperation(cts);
        }
    }

    private void ApplyResult(JsonFormatResult result, bool indented)
    {
        switch (result.Status)
        {
            case JsonFormatStatus.Success:
                OutputText = result.Output;
                HasError = false;
                HasResult = true;
                SetStatus(indented ? JsonStatusKind.Prettified : JsonStatusKind.Minified, result.Output.Length);
                break;
            case JsonFormatStatus.Empty:
                OutputText = string.Empty;
                HasError = false;
                HasResult = false;
                SetStatus(JsonStatusKind.None);
                break;
            case JsonFormatStatus.InputTooLarge:
                OutputText = string.Empty;
                HasError = true;
                HasResult = false;
                SetStatus(JsonStatusKind.InputTooLarge);
                break;
            case JsonFormatStatus.ParseError:
                OutputText = string.Empty;
                HasError = true;
                HasResult = false;
                SetStatus(
                    result.LineNumber.HasValue && result.ColumnNumber.HasValue
                        ? JsonStatusKind.ParseErrorAtPosition
                        : JsonStatusKind.ParseError,
                    message: result.ErrorMessage,
                    line: result.LineNumber,
                    column: result.ColumnNumber);
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    private CancellationTokenSource ReplaceCancellationTokenSource()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        IsProcessing = true;
        PrettifyCommand.NotifyCanExecuteChanged();
        MinifyCommand.NotifyCanExecuteChanged();
        return _cts;
    }

    private void CompleteOperation(CancellationTokenSource cts)
    {
        if (_cts == cts)
        {
            _cts.Dispose();
            _cts = null;
            IsProcessing = false;
            PrettifyCommand.NotifyCanExecuteChanged();
            MinifyCommand.NotifyCanExecuteChanged();
        }
    }

    private void SetStatus(JsonStatusKind kind, int length = 0, long? line = null, long? column = null, string? message = null)
    {
        _lastStatusKind = kind;
        _lastStatusLength = length;
        _lastStatusLine = line;
        _lastStatusColumn = column;
        _lastStatusMessage = message ?? string.Empty;
        RefreshStatusText();
    }

    private void RefreshStatusText()
    {
        switch (_lastStatusKind)
        {
            case JsonStatusKind.None:
            case JsonStatusKind.Empty:
                StatusText = string.Empty;
                StatusForegroundBrushKey = "TextSecondaryBrush";
                break;
            case JsonStatusKind.Processing:
                StatusText = L("ToolJsonStatusProcessing");
                StatusForegroundBrushKey = "TextSecondaryBrush";
                break;
            case JsonStatusKind.Prettified:
                StatusText = string.Format(CultureInfo.CurrentCulture, L("ToolJsonStatusPrettified"), _lastStatusLength);
                StatusForegroundBrushKey = "TextSecondaryBrush";
                break;
            case JsonStatusKind.Minified:
                StatusText = string.Format(CultureInfo.CurrentCulture, L("ToolJsonStatusMinified"), _lastStatusLength);
                StatusForegroundBrushKey = "TextSecondaryBrush";
                break;
            case JsonStatusKind.ParseErrorAtPosition:
                StatusText = string.Format(
                    CultureInfo.CurrentCulture,
                    L("ToolJsonStatusErrorAtPosition"),
                    _lastStatusLine.GetValueOrDefault() + 1,
                    _lastStatusColumn.GetValueOrDefault() + 1,
                    _lastStatusMessage);
                StatusForegroundBrushKey = "ErrorTextBrush";
                break;
            case JsonStatusKind.ParseError:
                StatusText = string.Format(CultureInfo.CurrentCulture, L("ToolJsonStatusError"), _lastStatusMessage);
                StatusForegroundBrushKey = "ErrorTextBrush";
                break;
            case JsonStatusKind.InputTooLarge:
                StatusText = L("ToolJsonErrorInputTooLarge");
                StatusForegroundBrushKey = "ErrorTextBrush";
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    private void OnLocaleChanged(string _)
    {
        RefreshStatusText();
    }

    private string L(string key) => _localizer?[key] ?? key;

    private enum JsonStatusKind
    {
        None,
        Empty,
        Processing,
        Prettified,
        Minified,
        ParseError,
        ParseErrorAtPosition,
        InputTooLarge,
    }
}
