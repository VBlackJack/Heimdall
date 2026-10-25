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
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Heimdall.App.Services;
using Heimdall.Core.Hashing;
using Heimdall.Core.Localization;
using Heimdall.Core.Logging;

namespace Heimdall.App.ViewModels.Tools;

public sealed partial class HmacGeneratorViewModel : ObservableObject, IDisposable
{
    private readonly IHmacGeneratorService _service;
    private LocalizationManager? _localizer;
    private CancellationTokenSource? _computeCts;
    private byte[]? _cachedHmacBytes;
    private bool _disposed;

    [ObservableProperty] private string _inputText = string.Empty;
    [ObservableProperty] private string _keyPlainText = string.Empty;
    [ObservableProperty] private bool _isKeyVisible;
    [ObservableProperty] private HmacAlgorithmOption? _selectedAlgorithm;
    [ObservableProperty] private HmacOutputFormat _outputFormat = HmacOutputFormat.Hex;
    [ObservableProperty] private string _outputText = string.Empty;
    [ObservableProperty] private string _byteLengthText = string.Empty;
    [ObservableProperty] private bool _isEmptyStateVisible = true;
    [ObservableProperty] private string _verifyInput = string.Empty;
    [ObservableProperty] private string _verifyResultText = string.Empty;
    [ObservableProperty] private string _verifyForegroundBrushKey = "TextPrimaryBrush";

    public HmacGeneratorViewModel(IHmacGeneratorService? service = null)
    {
        _service = service ?? new HmacGeneratorService();
        Algorithms = [];
    }

    public ObservableCollection<HmacAlgorithmOption> Algorithms { get; }

    public bool IsHexFormat
    {
        get => OutputFormat == HmacOutputFormat.Hex;
        set
        {
            if (value)
            {
                OutputFormat = HmacOutputFormat.Hex;
            }
        }
    }

    public bool IsBase64Format
    {
        get => OutputFormat == HmacOutputFormat.Base64;
        set
        {
            if (value)
            {
                OutputFormat = HmacOutputFormat.Base64;
            }
        }
    }

    public string ToggleKeyIcon => IsKeyVisible ? "\uED1A" : "\uE7B3";

    public void Initialize(LocalizationManager? localizer)
    {
        if (!ReferenceEquals(_localizer, localizer))
        {
            if (_localizer is not null)
            {
                _localizer.LocaleChanged -= OnLocaleChanged;
            }

            _localizer = localizer;
            if (_localizer is not null)
            {
                _localizer.LocaleChanged += OnLocaleChanged;
            }
        }

        if (Algorithms.Count == 0)
        {
            foreach (var kind in HmacAlgorithmCatalog.SupportedKinds)
            {
                Algorithms.Add(new HmacAlgorithmOption(kind, HmacAlgorithmCatalog.DisplayName(kind)));
            }
        }

        if (SelectedAlgorithm is null && Algorithms.Count > 0)
        {
            SelectedAlgorithm = Algorithms[0];
        }

        RefreshLocalizedMessages();
    }

    public void UpdateInputText(string text)
    {
        InputText = text ?? string.Empty;
    }

    public void UpdateKeyText(string text)
    {
        KeyPlainText = text ?? string.Empty;
    }

    public void RequestRecompute()
    {
        if (_disposed)
        {
            return;
        }

        _computeCts?.Cancel();
        _computeCts?.Dispose();
        _computeCts = new CancellationTokenSource();
        _ = ComputeAsync(_computeCts.Token);
    }

    [RelayCommand]
    private void ToggleKeyVisibility()
    {
        IsKeyVisible = !IsKeyVisible;
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

        _computeCts?.Cancel();
        _computeCts?.Dispose();
        _computeCts = null;
        GC.SuppressFinalize(this);
    }

    partial void OnSelectedAlgorithmChanged(HmacAlgorithmOption? value)
    {
        RequestRecompute();
    }

    partial void OnOutputFormatChanged(HmacOutputFormat value)
    {
        OnPropertyChanged(nameof(IsHexFormat));
        OnPropertyChanged(nameof(IsBase64Format));
        RefreshFormattedOutput();
        UpdateVerifyResult();
    }

    partial void OnIsKeyVisibleChanged(bool value)
    {
        OnPropertyChanged(nameof(ToggleKeyIcon));
    }

    partial void OnVerifyInputChanged(string value)
    {
        UpdateVerifyResult();
    }

    private async Task ComputeAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(InputText) || string.IsNullOrEmpty(KeyPlainText) || SelectedAlgorithm is null)
        {
            _cachedHmacBytes = null;
            OutputText = string.Empty;
            ByteLengthText = string.Empty;
            IsEmptyStateVisible = true;
            UpdateVerifyResult();
            return;
        }

        try
        {
            var keyBytes = Encoding.UTF8.GetBytes(KeyPlainText);
            var dataBytes = Encoding.UTF8.GetBytes(InputText);
            var bytes = await _service.ComputeAsync(SelectedAlgorithm.Kind, keyBytes, dataBytes, ct).ConfigureAwait(true);
            if (ct.IsCancellationRequested)
            {
                return;
            }

            _cachedHmacBytes = bytes;
            IsEmptyStateVisible = false;
            RefreshFormattedOutput();
            ByteLengthText = string.Format(L("ToolHmacByteLengthFormat"), bytes.Length, bytes.Length * 8);
            UpdateVerifyResult();
        }
        catch (NotSupportedException)
        {
            _cachedHmacBytes = null;
            OutputText = L("ToolHmacErrorUnsupported");
            ByteLengthText = string.Empty;
            IsEmptyStateVisible = false;
            UpdateVerifyResult();
        }
        catch (OperationCanceledException)
        {
            // Swallow cancellation.
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"HmacGenerator computation failed: {ex.Message}");
            _cachedHmacBytes = null;
            OutputText = string.Empty;
            ByteLengthText = string.Empty;
            IsEmptyStateVisible = false;
            UpdateVerifyResult();
        }
    }

    private void RefreshFormattedOutput()
    {
        if (_cachedHmacBytes is null)
        {
            return;
        }

        OutputText = HmacComputer.Format(_cachedHmacBytes, OutputFormat);
    }

    private void UpdateVerifyResult()
    {
        if (string.IsNullOrWhiteSpace(VerifyInput) || _cachedHmacBytes is null)
        {
            VerifyResultText = string.Empty;
            VerifyForegroundBrushKey = "TextPrimaryBrush";
            return;
        }

        var outcome = HmacVerifier.Verify(_cachedHmacBytes, VerifyInput);
        if (outcome.Matched)
        {
            VerifyResultText = L("ToolHmacVerifyMatch");
            VerifyForegroundBrushKey = "SuccessTextBrush";
            return;
        }

        VerifyResultText = L("ToolHmacVerifyNoMatch");
        VerifyForegroundBrushKey = "ErrorTextBrush";
    }

    private void RefreshLocalizedMessages()
    {
        if (_cachedHmacBytes is not null)
        {
            ByteLengthText = string.Format(L("ToolHmacByteLengthFormat"), _cachedHmacBytes.Length, _cachedHmacBytes.Length * 8);
        }

        if (_cachedHmacBytes is null &&
            !string.IsNullOrEmpty(InputText) &&
            !string.IsNullOrEmpty(KeyPlainText) &&
            SelectedAlgorithm is not null &&
            !HmacAlgorithmCatalog.IsSupported(SelectedAlgorithm.Kind))
        {
            OutputText = L("ToolHmacErrorUnsupported");
        }

        UpdateVerifyResult();
    }

    private void OnLocaleChanged(string _)
    {
        RefreshLocalizedMessages();
    }

    private string L(string key) => _localizer?[key] ?? key;
}

public sealed record HmacAlgorithmOption(HashAlgorithmKind Kind, string DisplayName);
