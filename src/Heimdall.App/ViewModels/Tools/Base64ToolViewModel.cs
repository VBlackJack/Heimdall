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
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Heimdall.App.Services;
using Heimdall.Core.Localization;

namespace Heimdall.App.ViewModels.Tools;

public sealed partial class Base64ToolViewModel : ObservableObject, IDisposable
{
    public const long MaxFileSizeBytes = 5L * 1024 * 1024;

    private readonly IBase64ToolService _service;
    private LocalizationManager? _localizer;
    private CancellationTokenSource? _cts;
    private byte[]? _fileBytes;
    private byte[]? _lastDecodedBytes;
    private bool _initialized;
    private bool _isExecuting;
    private bool _disposed;
    private Base64StatusKind _lastStatusKind = Base64StatusKind.None;
    private long _lastStatusByteCount;
    private string _lastStatusTextArg = string.Empty;

    [ObservableProperty] private string _inputText = string.Empty;
    [ObservableProperty] private string _outputText = string.Empty;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private string _statusForegroundBrushKey = "TextSecondaryBrush";
    [ObservableProperty] private bool _isUrlSafe;
    [ObservableProperty] private bool _isFileMode;
    [ObservableProperty] private bool _isInputReadOnly;
    [ObservableProperty] private bool _isResultsPanelVisible;
    [ObservableProperty] private bool _isEmptyStateVisible = true;
    [ObservableProperty] private bool _isBrowseFileButtonVisible;

    public Base64ToolViewModel(IBase64ToolService? service = null)
    {
        _service = service ?? new Base64ToolService();
    }

    public bool IsEncodeEnabled => !_isExecuting;

    /// <summary>
    /// Whether the decode entry point accepts a new run.
    /// </summary>
    /// <remarks>
    /// The decode button binds this instead of a command, because its Click handler has work to
    /// do after the command completes. Without it the button stayed enabled for the whole decode
    /// and told the user an operation was available while one was already running.
    /// </remarks>
    public bool IsDecodeEnabled => CanExecuteAction();

    public void Initialize(LocalizationManager? localizer)
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

        RefreshLocalizedState();
    }

    public async Task PrefillInput(string? input)
    {
        InputText = input ?? string.Empty;

        // Goes through the same gate as the commands. This entry point used to reach the
        // codec directly, without checking that nothing was in flight and without marking
        // that something now was, which left the "not while another run is in flight"
        // contract false for every path that started here.
        if (!CanExecuteAction())
        {
            _initialized = true;
            return;
        }

        using var cts = ReplaceCancellationTokenSource();
        try
        {
            await EncodeCoreAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        finally
        {
            CompleteOperation(cts);
        }

        _initialized = true;
    }

    public void MarkInitialized() => _initialized = true;

    public async Task LoadFileAsync(string path, CancellationToken ct)
    {
        if (_disposed)
        {
            return;
        }

        var outcome = await _service.LoadFileAsync(path, MaxFileSizeBytes, ct).ConfigureAwait(false);
        switch (outcome.Error)
        {
            case FileLoadError.None:
                _fileBytes = outcome.Bytes;
                InputText = string.Format(
                    CultureInfo.CurrentCulture,
                    L("ToolBase64FileLoaded"),
                    outcome.FileName ?? string.Empty,
                    outcome.Bytes?.LongLength ?? 0);
                IsInputReadOnly = true;
                break;
            case FileLoadError.FileTooLarge:
                ApplyStatus(Base64StatusKind.FileTooLarge);
                break;
            case FileLoadError.IoFailure:
                ApplyStatus(Base64StatusKind.Error, outcome.ErrorMessage ?? string.Empty);
                break;
        }
    }

    public async Task SaveFileAsync(string path, CancellationToken ct)
    {
        if (_disposed || _lastDecodedBytes is null)
        {
            return;
        }

        try
        {
            await _service.SaveFileAsync(path, _lastDecodedBytes, ct).ConfigureAwait(false);
            ApplyStatus(Base64StatusKind.Saved, path);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ApplyStatus(Base64StatusKind.Error, ex.Message);
        }
    }

    public byte[]? TryGetLastDecodedBytes() => _lastDecodedBytes;

    public void OnInputTextChangedFromView()
    {
        if (!_initialized)
        {
            return;
        }

        OutputText = string.Empty;
        _lastDecodedBytes = null;
        IsResultsPanelVisible = false;
        IsEmptyStateVisible = true;
        ClearStatus();
    }

    public void ReportStatus(string text, bool isError)
    {
        StatusText = text ?? string.Empty;
        StatusForegroundBrushKey = isError ? "ErrorTextBrush" : "TextSecondaryBrush";
        _lastStatusKind = isError ? Base64StatusKind.Error : Base64StatusKind.None;
        _lastStatusByteCount = 0;
        _lastStatusTextArg = text ?? string.Empty;
    }

    [RelayCommand(CanExecute = nameof(CanExecuteAction))]
    private async Task EncodeAsync()
    {
        // Re-checked here, not only through CanExecute. AsyncRelayCommand.ExecuteAsync invokes
        // the delegate without consulting CanExecute, and the view calls ExecuteAsync directly
        // from the Ctrl+Enter shortcut, so the declared "not while another run is in flight"
        // contract only holds if the body enforces it too.
        if (!CanExecuteAction())
        {
            return;
        }

        using var cts = ReplaceCancellationTokenSource();
        try
        {
            await EncodeCoreAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        finally
        {
            CompleteOperation(cts);
        }
    }

    [RelayCommand(CanExecute = nameof(CanExecuteAction))]
    private async Task DecodeAsync()
    {
        // Same reason as EncodeAsync, and it matters more here: the decode button is wired to a
        // Click handler on purpose, because a file-mode decode opens a save dialog afterwards.
        // A Click handler cannot be gated by the command, so without this the button stayed live
        // for the whole decode and a second click re-entered the body and cancelled the first.
        if (!CanExecuteAction())
        {
            return;
        }

        using var cts = ReplaceCancellationTokenSource();
        try
        {
            var decoded = await _service.DecodeAsync(InputText.Trim(), IsUrlSafe, cts.Token).ConfigureAwait(false);
            _lastDecodedBytes = decoded;

            if (IsFileMode)
            {
                return;
            }

            OutputText = Encoding.UTF8.GetString(decoded);
            IsResultsPanelVisible = true;
            IsEmptyStateVisible = false;
            ApplyStatus(Base64StatusKind.Decoded, decoded.LongLength);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (FormatException)
        {
            OutputText = string.Empty;
            _lastDecodedBytes = null;
            IsResultsPanelVisible = false;
            IsEmptyStateVisible = true;
            ApplyStatus(Base64StatusKind.InvalidInput);
        }
        catch (Exception ex)
        {
            OutputText = string.Empty;
            _lastDecodedBytes = null;
            IsResultsPanelVisible = false;
            IsEmptyStateVisible = true;
            ApplyStatus(Base64StatusKind.Error, ex.Message);
        }
        finally
        {
            CompleteOperation(cts);
        }
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
        _fileBytes = null;
        _lastDecodedBytes = null;
        GC.SuppressFinalize(this);
    }

    partial void OnIsFileModeChanged(bool value)
    {
        IsBrowseFileButtonVisible = value;

        if (value)
        {
            return;
        }

        _fileBytes = null;
        IsInputReadOnly = false;
        _lastDecodedBytes = null;
        InputText = string.Empty;
        OutputText = string.Empty;
        IsResultsPanelVisible = false;
        IsEmptyStateVisible = true;
        ClearStatus();
    }

    private bool CanExecuteAction() => !_disposed && !_isExecuting;

    private async Task EncodeCoreAsync(CancellationToken ct)
    {
        var data = IsFileMode && _fileBytes is not null
            ? _fileBytes
            : Encoding.UTF8.GetBytes(InputText);

        var encoded = await _service.EncodeAsync(data, IsUrlSafe, ct).ConfigureAwait(false);
        OutputText = encoded;
        _lastDecodedBytes = null;
        IsResultsPanelVisible = true;
        IsEmptyStateVisible = false;
        ApplyStatus(Base64StatusKind.Encoded, data.LongLength);
    }

    private CancellationTokenSource ReplaceCancellationTokenSource()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        _isExecuting = true;
        OnPropertyChanged(nameof(IsEncodeEnabled));
        OnPropertyChanged(nameof(IsDecodeEnabled));
        EncodeCommand.NotifyCanExecuteChanged();
        DecodeCommand.NotifyCanExecuteChanged();
        return _cts;
    }

    private void CompleteOperation(CancellationTokenSource cts)
    {
        if (_cts == cts)
        {
            _cts.Dispose();
            _cts = null;
            _isExecuting = false;
            OnPropertyChanged(nameof(IsEncodeEnabled));
            OnPropertyChanged(nameof(IsDecodeEnabled));
            EncodeCommand.NotifyCanExecuteChanged();
            DecodeCommand.NotifyCanExecuteChanged();
        }
    }

    private void ClearStatus()
    {
        StatusText = string.Empty;
        StatusForegroundBrushKey = "TextSecondaryBrush";
        _lastStatusKind = Base64StatusKind.None;
        _lastStatusByteCount = 0;
        _lastStatusTextArg = string.Empty;
    }

    private void ApplyStatus(Base64StatusKind kind, long byteCount)
    {
        _lastStatusKind = kind;
        _lastStatusByteCount = byteCount;
        _lastStatusTextArg = string.Empty;
        RefreshStatusText();
    }

    private void ApplyStatus(Base64StatusKind kind)
    {
        _lastStatusKind = kind;
        _lastStatusByteCount = 0;
        _lastStatusTextArg = string.Empty;
        RefreshStatusText();
    }

    private void ApplyStatus(Base64StatusKind kind, string textArg)
    {
        _lastStatusKind = kind;
        _lastStatusByteCount = 0;
        _lastStatusTextArg = textArg ?? string.Empty;
        RefreshStatusText();
    }

    private void RefreshLocalizedState()
    {
        RefreshStatusText();
    }

    private void RefreshStatusText()
    {
        switch (_lastStatusKind)
        {
            case Base64StatusKind.None:
                StatusText = string.Empty;
                StatusForegroundBrushKey = "TextSecondaryBrush";
                break;
            case Base64StatusKind.Encoded:
                StatusText = string.Format(CultureInfo.CurrentCulture, L("ToolBase64StatusEncoded"), _lastStatusByteCount);
                StatusForegroundBrushKey = "TextSecondaryBrush";
                break;
            case Base64StatusKind.Decoded:
                StatusText = string.Format(CultureInfo.CurrentCulture, L("ToolBase64StatusDecoded"), _lastStatusByteCount);
                StatusForegroundBrushKey = "TextSecondaryBrush";
                break;
            case Base64StatusKind.Saved:
                StatusText = string.Format(CultureInfo.CurrentCulture, L("ToolBase64StatusSaved"), _lastStatusTextArg);
                StatusForegroundBrushKey = "TextSecondaryBrush";
                break;
            case Base64StatusKind.InvalidInput:
                StatusText = L("ToolBase64StatusInvalidInput");
                StatusForegroundBrushKey = "ErrorTextBrush";
                break;
            case Base64StatusKind.Error:
                StatusText = string.Format(CultureInfo.CurrentCulture, L("ToolBase64StatusError"), _lastStatusTextArg);
                StatusForegroundBrushKey = "ErrorTextBrush";
                break;
            case Base64StatusKind.FileTooLarge:
                StatusText = L("ToolBase64ErrorFileTooLarge");
                StatusForegroundBrushKey = "ErrorTextBrush";
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    private void OnLocaleChanged(string _) => RefreshLocalizedState();

    private string L(string key) => _localizer?[key] ?? key;

    private enum Base64StatusKind
    {
        None,
        Encoded,
        Decoded,
        Saved,
        InvalidInput,
        Error,
        FileTooLarge,
    }
}
