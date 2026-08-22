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
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Heimdall.App.Services;
using Heimdall.Core.Hashing;
using Heimdall.Core.Localization;
using Heimdall.Core.Logging;
using Heimdall.Core.Utilities;

namespace Heimdall.App.ViewModels.Tools;

public sealed partial class HashGeneratorViewModel : ObservableObject, IDisposable
{
    private readonly IHashGeneratorService _service;
    private LocalizationManager? _localizer;
    private CancellationTokenSource? _textCts;
    private CancellationTokenSource? _fileCts;
    private HashInputSource _lastSource = HashInputSource.None;
    private long _lastTextByteLength;
    private long _lastFileSizeBytes;
    private string _lastFileName = string.Empty;
    private FileStatusKind _lastFileStatusKind = FileStatusKind.None;
    private bool _disposed;

    [ObservableProperty] private string _inputText = string.Empty;
    [ObservableProperty] private string _verifyInput = string.Empty;
    [ObservableProperty] private string _byteLengthText = string.Empty;
    [ObservableProperty] private string _fileStatusText = string.Empty;
    [ObservableProperty] private string _fileStatusBrushKey = "TextSecondaryBrush";
    [ObservableProperty] private double _hashProgress;
    [ObservableProperty] private bool _isFileHashing;
    [ObservableProperty] private bool _isFileMode;
    [ObservableProperty] private bool _isEmptyStateVisible = true;
    [ObservableProperty] private bool _isResultsVisible;
    [ObservableProperty] private string _verifyResultText = string.Empty;
    [ObservableProperty] private string _verifyForegroundBrushKey = "TextSecondaryBrush";

    public HashGeneratorViewModel(IHashGeneratorService? service = null)
    {
        _service = service ?? new HashGeneratorService();
        Results = [];
    }

    public ObservableCollection<HashResultViewModel> Results { get; }

    public bool IsTextInputEnabled => !IsFileMode;

    public event EventHandler<string>? CopyTextRequested;
    public event EventHandler<SaveFileRequest>? SaveFileRequested;

    public void Initialize(LocalizationManager? localizer)
    {
        UpdateLocalizer(localizer);
        if (Results.Count == 0)
        {
            PopulateResults();
        }
    }

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

    public void UpdateInputText(string text)
    {
        if (_disposed)
        {
            return;
        }

        InputText = text ?? string.Empty;
    }

    [RelayCommand(CanExecute = nameof(CanHashFile))]
    private async Task HashFileAsync(string? path)
    {
        if (_disposed || string.IsNullOrWhiteSpace(path) || IsFileHashing)
        {
            return;
        }

        _textCts?.Cancel();
        _textCts?.Dispose();
        _textCts = null;

        _fileCts?.Cancel();
        _fileCts?.Dispose();
        _fileCts = new CancellationTokenSource();

        _lastSource = HashInputSource.File;
        _lastFileName = Path.GetFileName(path);
        _lastFileSizeBytes = 0;
        _lastFileStatusKind = FileStatusKind.Hashing;

        ClearComputedHashes();
        ByteLengthText = string.Empty;
        _lastTextByteLength = 0;
        FileStatusBrushKey = "TextSecondaryBrush";
        FileStatusText = L("ToolHashFileHashing");
        VerifyResultText = string.Empty;
        VerifyForegroundBrushKey = "TextSecondaryBrush";
        HashProgress = 0;
        IsFileHashing = true;
        IsFileMode = true;
        IsResultsVisible = false;
        IsEmptyStateVisible = false;

        try
        {
            var progress = new Progress<double>(value => HashProgress = value);
            var result = await _service.ComputeFileHashesAsync(path, progress, _fileCts.Token);

            ApplyHashes(result.Hashes);
            _lastFileSizeBytes = result.FileSizeBytes;
            _lastFileStatusKind = FileStatusKind.Success;
            FileStatusBrushKey = "TextSecondaryBrush";
            FileStatusText = string.Format(L("ToolHashGenFileStatusFormat"), _lastFileName, FileSize.Format(result.FileSizeBytes));
            IsResultsVisible = true;
            IsEmptyStateVisible = false;
            UpdateVerifyResult();
        }
        catch (HashFileTooLargeException ex)
        {
            _lastFileStatusKind = FileStatusKind.TooLarge;
            _lastFileSizeBytes = 0;
            IsFileMode = false;
            ClearComputedHashes();
            FileStatusBrushKey = "ErrorBrush";
            FileStatusText = string.Format(L("ToolHashGenErrorFileTooLarge"), FileSize.Format(ex.LimitBytes));
            IsResultsVisible = false;
            IsEmptyStateVisible = true;
            UpdateVerifyResult();
        }
        catch (FileNotFoundException)
        {
            _lastFileStatusKind = FileStatusKind.NotFound;
            _lastFileSizeBytes = 0;
            IsFileMode = false;
            ClearComputedHashes();
            FileStatusBrushKey = "ErrorBrush";
            FileStatusText = L("ToolHashGenErrorFileNotFound");
            IsResultsVisible = false;
            IsEmptyStateVisible = true;
            UpdateVerifyResult();
        }
        catch (UnauthorizedAccessException)
        {
            _lastFileStatusKind = FileStatusKind.AccessDenied;
            _lastFileSizeBytes = 0;
            ClearComputedHashes();
            FileStatusBrushKey = "ErrorBrush";
            FileStatusText = L("ToolHashGenErrorFileAccessDenied");
            IsResultsVisible = false;
            IsEmptyStateVisible = true;
            UpdateVerifyResult();
        }
        catch (OperationCanceledException) when (_fileCts?.IsCancellationRequested == true)
        {
            if (!HasAnyHashes())
            {
                FileStatusText = string.Empty;
                FileStatusBrushKey = "TextSecondaryBrush";
                IsResultsVisible = false;
                IsEmptyStateVisible = true;
            }
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"HashGenerator file hash failed: {ex.Message}");
            _lastFileStatusKind = FileStatusKind.GenericError;
            _lastFileSizeBytes = 0;
            ClearComputedHashes();
            FileStatusBrushKey = "ErrorBrush";
            FileStatusText = L("ToolHashGenErrorGeneric");
            IsResultsVisible = false;
            IsEmptyStateVisible = true;
            UpdateVerifyResult();
        }
        finally
        {
            IsFileHashing = false;
            HashProgress = 0;
            _fileCts?.Dispose();
            _fileCts = null;
        }
    }

    [RelayCommand(CanExecute = nameof(CanClearFile))]
    private void ClearFile()
    {
        _fileCts?.Cancel();
        _fileCts?.Dispose();
        _fileCts = null;

        _lastSource = HashInputSource.None;
        _lastTextByteLength = 0;
        _lastFileSizeBytes = 0;
        _lastFileName = string.Empty;
        _lastFileStatusKind = FileStatusKind.None;

        IsFileHashing = false;
        IsFileMode = false;
        HashProgress = 0;
        FileStatusText = string.Empty;
        FileStatusBrushKey = "TextSecondaryBrush";
        ByteLengthText = string.Empty;
        ClearComputedHashes();
        IsResultsVisible = false;
        IsEmptyStateVisible = true;
        UpdateVerifyResult();
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

        _textCts?.Cancel();
        _textCts?.Dispose();
        _fileCts?.Cancel();
        _fileCts?.Dispose();
        GC.SuppressFinalize(this);
    }

    partial void OnInputTextChanged(string value)
    {
        if (_disposed || IsFileMode)
        {
            return;
        }

        _ = ComputeTextAsync(value);
    }

    partial void OnVerifyInputChanged(string value)
    {
        UpdateVerifyResult();
    }

    partial void OnIsFileHashingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanAcceptFile));
        HashFileCommand.NotifyCanExecuteChanged();
        ClearFileCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsFileModeChanged(bool value)
    {
        OnPropertyChanged(nameof(IsTextInputEnabled));
        ClearFileCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Whether the tool will take a file at all, independent of which file.
    /// </summary>
    /// <remarks>
    /// The single source for the three surfaces that announce it: the browse button, the
    /// drop-zone border, and the drag cursor. They used to hold two predicates for one
    /// decision - the button on <see cref="IsFileHashing"/> alone, the other two on
    /// <see cref="CanHashFile"/> - which left nothing to keep them in agreement.
    /// </remarks>
    public bool CanAcceptFile => !_disposed && !IsFileHashing;

    private bool CanHashFile(string? path) => CanAcceptFile && !string.IsNullOrWhiteSpace(path);

    private bool CanClearFile() => IsFileMode;

    private async Task ComputeTextAsync(string text)
    {
        _textCts?.Cancel();
        _textCts?.Dispose();
        _textCts = new CancellationTokenSource();
        var token = _textCts.Token;

        _lastSource = HashInputSource.Text;
        _lastFileStatusKind = FileStatusKind.None;
        _lastFileName = string.Empty;
        _lastFileSizeBytes = 0;
        FileStatusText = string.Empty;
        FileStatusBrushKey = "TextSecondaryBrush";

        if (string.IsNullOrEmpty(text))
        {
            _lastTextByteLength = 0;
            ClearComputedHashes();
            ByteLengthText = string.Empty;
            IsResultsVisible = false;
            IsEmptyStateVisible = true;
            UpdateVerifyResult();
            return;
        }

        try
        {
            var hashes = await _service.ComputeTextHashesAsync(text, token);
            if (token.IsCancellationRequested)
            {
                return;
            }

            ApplyHashes(hashes);
            _lastTextByteLength = Encoding.UTF8.GetByteCount(text);
            ByteLengthText = string.Format(L("ToolHashGenByteLengthFormat"), _lastTextByteLength);
            IsResultsVisible = true;
            IsEmptyStateVisible = false;
            UpdateVerifyResult();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Ignore.
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"HashGenerator text compute failed: {ex.Message}");
            _lastTextByteLength = 0;
            ClearComputedHashes();
            ByteLengthText = string.Empty;
            IsResultsVisible = false;
            IsEmptyStateVisible = true;
            UpdateVerifyResult();
        }
        finally
        {
            if (_textCts?.Token == token)
            {
                _textCts.Dispose();
                _textCts = null;
            }
        }
    }

    private void PopulateResults()
    {
        Results.Clear();
        foreach (var kind in HashAlgorithmCatalog.AllKinds)
        {
            Results.Add(new HashResultViewModel(
                kind,
                HashAlgorithmCatalog.DisplayName(kind),
                HashAlgorithmCatalog.IsSupported(kind),
                L("ToolHashGenUnsupportedAlgo"),
                OnCopyRow,
                OnSaveRow));
        }
    }

    private void RefreshLocalizedMessages()
    {
        var unsupportedText = L("ToolHashGenUnsupportedAlgo");
        foreach (var row in Results)
        {
            row.UpdateUnsupportedText(unsupportedText);
        }

        switch (_lastSource)
        {
            case HashInputSource.Text when !string.IsNullOrEmpty(InputText):
                ByteLengthText = string.Format(L("ToolHashGenByteLengthFormat"), _lastTextByteLength);
                FileStatusText = string.Empty;
                FileStatusBrushKey = "TextSecondaryBrush";
                break;
            case HashInputSource.File:
                RefreshLocalizedFileStatus();
                break;
            default:
                ByteLengthText = string.Empty;
                if (_lastFileStatusKind == FileStatusKind.None && !IsFileHashing)
                {
                    FileStatusText = string.Empty;
                    FileStatusBrushKey = "TextSecondaryBrush";
                }
                break;
        }

        UpdateVerifyResult();
    }

    private void RefreshLocalizedFileStatus()
    {
        if (IsFileHashing)
        {
            FileStatusText = L("ToolHashFileHashing");
            FileStatusBrushKey = "TextSecondaryBrush";
            return;
        }

        switch (_lastFileStatusKind)
        {
            case FileStatusKind.Success:
                FileStatusText = string.Format(L("ToolHashGenFileStatusFormat"), _lastFileName, FileSize.Format(_lastFileSizeBytes));
                FileStatusBrushKey = "TextSecondaryBrush";
                break;
            case FileStatusKind.TooLarge:
                FileStatusText = string.Format(L("ToolHashGenErrorFileTooLarge"), FileSize.Format(HashGeneratorService.MaxFileSizeBytes));
                FileStatusBrushKey = "ErrorBrush";
                break;
            case FileStatusKind.NotFound:
                FileStatusText = L("ToolHashGenErrorFileNotFound");
                FileStatusBrushKey = "ErrorBrush";
                break;
            case FileStatusKind.AccessDenied:
                FileStatusText = L("ToolHashGenErrorFileAccessDenied");
                FileStatusBrushKey = "ErrorBrush";
                break;
            case FileStatusKind.GenericError:
                FileStatusText = L("ToolHashGenErrorGeneric");
                FileStatusBrushKey = "ErrorBrush";
                break;
            default:
                break;
        }
    }

    private void ApplyHashes(IReadOnlyDictionary<HashAlgorithmKind, string> hashes)
    {
        foreach (var row in Results)
        {
            row.IsMatched = false;
            row.HashValue = hashes.TryGetValue(row.Kind, out var hash) ? hash : string.Empty;
        }
    }

    private void ClearComputedHashes()
    {
        foreach (var row in Results)
        {
            row.HashValue = string.Empty;
            row.IsMatched = false;
        }
    }

    private void UpdateVerifyResult()
    {
        foreach (var row in Results)
        {
            row.IsMatched = false;
        }

        var candidate = VerifyInput?.Trim().ToLowerInvariant() ?? string.Empty;
        var computed = Results
            .Where(row => row.IsSupported && !string.IsNullOrEmpty(row.HashValue))
            .ToDictionary(row => row.Kind, row => row.HashValue);

        if (string.IsNullOrEmpty(candidate) || computed.Count == 0)
        {
            VerifyResultText = string.Empty;
            VerifyForegroundBrushKey = "TextSecondaryBrush";
            return;
        }

        var result = HashVerifier.FindMatch(computed, candidate);
        if (result.Matched && result.MatchedKind is not null)
        {
            VerifyResultText = string.Format(L("ToolHashGenVerifyMatchFormat"), HashAlgorithmCatalog.DisplayName(result.MatchedKind.Value));
            VerifyForegroundBrushKey = "SuccessBrush";
            foreach (var row in Results)
            {
                row.IsMatched = row.Kind == result.MatchedKind.Value;
            }

            return;
        }

        VerifyResultText = L("ToolHashGenVerifyNoMatch");
        VerifyForegroundBrushKey = "ErrorBrush";
    }

    private bool HasAnyHashes() => Results.Any(row => !string.IsNullOrEmpty(row.HashValue));

    private void OnCopyRow(HashResultViewModel row)
    {
        if (!string.IsNullOrEmpty(row.HashValue))
        {
            CopyTextRequested?.Invoke(this, row.HashValue);
        }
    }

    private void OnSaveRow(HashResultViewModel row)
    {
        if (string.IsNullOrEmpty(row.HashValue))
        {
            return;
        }

        SaveFileRequested?.Invoke(
            this,
            new SaveFileRequest(
                row.HashValue,
                false,
                $"{L("BtnSave")} (*.txt)|*.txt|{L("ToolHashBrowseFilter")}",
                $"{row.AlgorithmDisplayName}.txt"));
    }

    private void OnLocaleChanged(string _) => RefreshLocalizedMessages();

    private string L(string key) => _localizer?[key] ?? key;

    private enum FileStatusKind
    {
        None,
        Hashing,
        Success,
        TooLarge,
        NotFound,
        AccessDenied,
        GenericError,
    }

    internal enum HashInputSource
    {
        None,
        Text,
        File,
    }
}

public sealed partial class HashResultViewModel : ObservableObject
{
    private readonly Action<HashResultViewModel> _copyAction;
    private readonly Action<HashResultViewModel> _saveAction;

    [ObservableProperty] private string _hashValue = string.Empty;
    [ObservableProperty] private bool _isSupported;
    [ObservableProperty] private bool _isMatched;
    [ObservableProperty] private string _unsupportedText;

    public HashResultViewModel(
        HashAlgorithmKind kind,
        string algorithmDisplayName,
        bool isSupported,
        string unsupportedText,
        Action<HashResultViewModel> copyAction,
        Action<HashResultViewModel> saveAction)
    {
        Kind = kind;
        AlgorithmDisplayName = algorithmDisplayName;
        _isSupported = isSupported;
        _unsupportedText = unsupportedText;
        _copyAction = copyAction;
        _saveAction = saveAction;
        CopyCommand = new RelayCommand(() => _copyAction(this), CanCopyOrSave);
        SaveCommand = new RelayCommand(() => _saveAction(this), CanCopyOrSave);
    }

    public HashAlgorithmKind Kind { get; }

    public string AlgorithmDisplayName { get; }

    public string DisplayText => IsSupported ? HashValue : UnsupportedText;

    public string ForegroundBrushKey => !IsSupported
        ? "TextDisabledBrush"
        : IsMatched
            ? "SuccessBrush"
            : "TextPrimaryBrush";

    public IRelayCommand CopyCommand { get; }

    public IRelayCommand SaveCommand { get; }

    public void UpdateUnsupportedText(string value)
    {
        UnsupportedText = value;
    }

    partial void OnHashValueChanged(string value)
    {
        OnPropertyChanged(nameof(DisplayText));
        NotifyCommandsChanged();
    }

    partial void OnIsSupportedChanged(bool value)
    {
        OnPropertyChanged(nameof(DisplayText));
        OnPropertyChanged(nameof(ForegroundBrushKey));
        NotifyCommandsChanged();
    }

    partial void OnIsMatchedChanged(bool value)
    {
        OnPropertyChanged(nameof(ForegroundBrushKey));
    }

    partial void OnUnsupportedTextChanged(string value)
    {
        OnPropertyChanged(nameof(DisplayText));
    }

    private bool CanCopyOrSave() => IsSupported && !string.IsNullOrEmpty(HashValue);

    private void NotifyCommandsChanged()
    {
        CopyCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
    }
}
