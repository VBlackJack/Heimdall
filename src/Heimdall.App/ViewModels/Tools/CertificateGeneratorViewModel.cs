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
using System.Security.Cryptography;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Heimdall.App.Services;
using Heimdall.Core.Certificates;
using Heimdall.Core.Localization;

namespace Heimdall.App.ViewModels.Tools;

/// <param name="Content">The text or bytes to save.</param>
/// <param name="IsBinary">True when <paramref name="Content"/> is a byte array.</param>
/// <param name="Filter">The file dialog filter.</param>
/// <param name="DefaultName">The suggested file name.</param>
/// <param name="ContainsPrivateKey">True when the file holds a private key, which is then written with a restricted ACL.</param>
public sealed record SaveFileRequest(
    object Content,
    bool IsBinary,
    string Filter,
    string DefaultName,
    bool ContainsPrivateKey = false);

public sealed record PfxPasswordRequest(string Title, string Prompt, Action<string?> ResultCallback);

public sealed partial class CertificateGeneratorViewModel : ObservableObject, IDisposable
{
    /// <summary>The tail of every PEM private key header (plain, RSA, EC, encrypted).</summary>
    private const string PrivateKeyPemMarker = "PRIVATE KEY-----";

    public const int DefaultValidityDays = 365;
    public const int CaValidityDays = 3650;
    public const string MaskedPlaceholder = "********";

    private readonly ICertificateGeneratorService _service;
    private LocalizationManager? _localizer;
    private Action<string>? _localeChangedHandler;
    private SelfSignedCertificateResult? _selfSignedResult;
    private CaLeafCertificateResult? _caLeafResult;
    private CertificateValidationCode _lastValidationCode = CertificateValidationCode.Ok;
    private CertificateMessageKind _lastMessageKind = CertificateMessageKind.None;
    private string? _lastMessageDetail;
    private bool _disposed;

    [ObservableProperty] private string _cn = string.Empty;
    [ObservableProperty] private string _org = string.Empty;
    [ObservableProperty] private string _country = string.Empty;
    [ObservableProperty] private int _keySizeIndex;
    [ObservableProperty] private string _validityDaysText = DefaultValidityDays.ToString(CultureInfo.InvariantCulture);
    [ObservableProperty] private string _sanRaw = string.Empty;
    [ObservableProperty] private CertificateMode _mode = CertificateMode.SelfSigned;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanGenerate))] private bool _isGenerating;
    [ObservableProperty] private bool _isKeyVisible;
    [ObservableProperty] private bool _isLeafKeyVisible;
    [ObservableProperty] private bool _isHelpVisible;
    [ObservableProperty] private string? _validationMessage;

    public CertificateGeneratorViewModel(ICertificateGeneratorService? service = null)
    {
        _service = service ?? new CertificateGeneratorService();
    }

    public bool IsSelfSigned
    {
        get => Mode == CertificateMode.SelfSigned;
        set
        {
            if (value)
            {
                Mode = CertificateMode.SelfSigned;
            }
        }
    }

    public bool IsCaLeafMode
    {
        get => Mode == CertificateMode.CaLeaf;
        set
        {
            if (value)
            {
                Mode = CertificateMode.CaLeaf;
            }
        }
    }

    public bool CanGenerate => !IsGenerating;

    public bool HasResult => _selfSignedResult is not null || _caLeafResult is not null;

    public string FingerprintText => _selfSignedResult?.Fingerprint ?? _caLeafResult?.Fingerprint ?? string.Empty;

    public string CurrentCertPem => Mode == CertificateMode.CaLeaf
        ? _caLeafResult?.CaCertPem ?? string.Empty
        : _selfSignedResult?.CertPem ?? string.Empty;

    public string CurrentKeyPem => Mode == CertificateMode.CaLeaf
        ? _caLeafResult?.CaKeyPem ?? string.Empty
        : _selfSignedResult?.KeyPem ?? string.Empty;

    public string LeafCertPem => _caLeafResult?.LeafCertPem ?? string.Empty;

    public string LeafKeyPem => _caLeafResult?.LeafKeyPem ?? string.Empty;

    public string DisplayedKeyText => IsKeyVisible ? CurrentKeyPem : MaskedPlaceholder;

    public string DisplayedLeafKeyText => IsLeafKeyVisible ? LeafKeyPem : MaskedPlaceholder;

    public string CertLabelText => L(Mode == CertificateMode.CaLeaf ? "ToolCertGenCaCertPem" : "ToolCertGenCertPem");

    public string KeyLabelText => L(Mode == CertificateMode.CaLeaf ? "ToolCertGenCaKeyPem" : "ToolCertGenKeyPem");

    public string BtnShowKeyText => L(IsKeyVisible ? "ToolCertGenBtnHide" : "ToolCertGenBtnShow");

    public string BtnShowLeafKeyText => L(IsLeafKeyVisible ? "ToolCertGenBtnHide" : "ToolCertGenBtnShow");

    public bool IsFingerprintPanelVisible => HasResult;

    public bool IsCertPanelVisible => HasResult;

    public bool IsKeyPanelVisible => HasResult;

    public bool IsLeafPanelVisible => HasResult && Mode == CertificateMode.CaLeaf;

    public bool IsExportPanelVisible => HasResult;

    public string HelpContentText => L("ToolHelpCERTGEN").Replace("\\n", "\n", StringComparison.Ordinal);

    public event EventHandler<string>? CopyTextRequested;
    public event EventHandler<SaveFileRequest>? SaveFileRequested;
    public event EventHandler<PfxPasswordRequest>? PfxPasswordRequested;
    public event EventHandler<string>? ValidationFocusRequested;

    public void Initialize(LocalizationManager? localizer) => UpdateLocalizer(localizer);

    public void UpdateLocalizer(LocalizationManager? localizer)
    {
        if (ReferenceEquals(_localizer, localizer))
        {
            return;
        }

        if (_localizer is not null && _localeChangedHandler is not null)
        {
            _localizer.LocaleChanged -= _localeChangedHandler;
        }

        _localizer = localizer;
        if (_localizer is not null)
        {
            _localeChangedHandler ??= _ => RefreshLocalizedMessages();
            _localizer.LocaleChanged += _localeChangedHandler;
        }

        RefreshLocalizedMessages();
    }

    [RelayCommand(CanExecute = nameof(CanGenerate))]
    private async Task GenerateAsync(CancellationToken cancellationToken)
    {
        if (_disposed || IsGenerating)
        {
            return;
        }

        ClearValidation();

        var cn = Cn.Trim();
        if (string.IsNullOrWhiteSpace(cn))
        {
            SetValidationCode(CertificateValidationCode.CnRequired);
            ValidationFocusRequested?.Invoke(this, "Cn");
            return;
        }

        if (!int.TryParse(ValidityDaysText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var validityDays)
            || validityDays < 1)
        {
            SetValidationCode(CertificateValidationCode.InvalidValidity);
            ValidationFocusRequested?.Invoke(this, "ValidityDays");
            return;
        }

        var options = new CertificateOptions(
            cn,
            Org.Trim(),
            Country.Trim(),
            KeySizeIndex == 0 ? CertificateGenerator.Rsa2048KeySize : CertificateGenerator.Rsa4096KeySize,
            validityDays,
            SanParser.Parse(SanRaw));

        var validation = options.Validate();
        if (validation.Code != CertificateValidationCode.Ok)
        {
            SetValidationCode(validation.Code);
            ValidationFocusRequested?.Invoke(
                this,
                validation.Code == CertificateValidationCode.CnRequired ? "Cn" : "ValidityDays");
            return;
        }

        IsGenerating = true;
        _lastMessageKind = CertificateMessageKind.Generating;
        _lastMessageDetail = null;
        ValidationMessage = L("ToolCertGenGenerating");

        try
        {
            if (Mode == CertificateMode.CaLeaf)
            {
                _caLeafResult = await _service.GenerateCaLeafPairAsync(options, CaValidityDays, cancellationToken).ConfigureAwait(true);
                _selfSignedResult = null;
            }
            else
            {
                _selfSignedResult = await _service.GenerateSelfSignedAsync(options, cancellationToken).ConfigureAwait(true);
                _caLeafResult = null;
            }

            IsKeyVisible = false;
            IsLeafKeyVisible = false;
            ClearValidation();
            RaiseResultPropertiesChanged();
        }
        catch (CryptographicException ex)
        {
            _lastMessageKind = CertificateMessageKind.GenerationError;
            _lastMessageDetail = ex.Message;
            ValidationMessage = string.Format(CultureInfo.InvariantCulture, L("ToolCertGenErrorGeneration"), ex.Message);
        }
        catch (Exception ex)
        {
            _lastMessageKind = CertificateMessageKind.GenerationError;
            _lastMessageDetail = ex.Message;
            ValidationMessage = string.Format(CultureInfo.InvariantCulture, L("ToolCertGenErrorGeneration"), ex.Message);
        }
        finally
        {
            IsGenerating = false;
            GenerateCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand]
    private void CopyFingerprint()
    {
        if (!string.IsNullOrEmpty(FingerprintText))
        {
            CopyTextRequested?.Invoke(this, FingerprintText);
        }
    }

    [RelayCommand]
    private void CopyCert()
    {
        if (!string.IsNullOrEmpty(CurrentCertPem))
        {
            CopyTextRequested?.Invoke(this, CurrentCertPem);
        }
    }

    [RelayCommand]
    private void CopyKey()
    {
        if (!string.IsNullOrEmpty(CurrentKeyPem))
        {
            CopyTextRequested?.Invoke(this, CurrentKeyPem);
        }
    }

    [RelayCommand]
    private void CopyLeafCert()
    {
        if (!string.IsNullOrEmpty(LeafCertPem))
        {
            CopyTextRequested?.Invoke(this, LeafCertPem);
        }
    }

    [RelayCommand]
    private void CopyLeafKey()
    {
        if (!string.IsNullOrEmpty(LeafKeyPem))
        {
            CopyTextRequested?.Invoke(this, LeafKeyPem);
        }
    }

    /// <summary>
    /// True when <paramref name="text"/> is private key material, so a view can route its copy
    /// through the secret clipboard path. The copy event carries only text.
    /// </summary>
    public bool IsPrivateKeyMaterial(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        return string.Equals(text, CurrentKeyPem, StringComparison.Ordinal)
            || string.Equals(text, LeafKeyPem, StringComparison.Ordinal)
            || ContainsPrivateKeyMarker(text);
    }

    /// <summary>True when <paramref name="text"/> carries a PEM private key header.</summary>
    internal static bool ContainsPrivateKeyMarker(string text)
        => text.Contains(PrivateKeyPemMarker, StringComparison.Ordinal);

    /// <summary>Shows the export error status for a file that could not be written.</summary>
    public void ReportSaveFailure(string detail)
    {
        _lastMessageKind = CertificateMessageKind.ExportError;
        _lastMessageDetail = detail;
        ValidationMessage = string.Format(CultureInfo.InvariantCulture, L("ToolCertGenErrorExport"), detail);
    }

    [RelayCommand]
    private void ToggleKey() => IsKeyVisible = !IsKeyVisible;

    [RelayCommand]
    private void ToggleLeafKey() => IsLeafKeyVisible = !IsLeafKeyVisible;

    [RelayCommand]
    private void SavePem()
    {
        var content = Mode == CertificateMode.CaLeaf ? LeafCertPem : CurrentCertPem;
        if (string.IsNullOrEmpty(content))
        {
            return;
        }

        SaveFileRequested?.Invoke(
            this,
            new SaveFileRequest(
                content,
                false,
                L("ToolCertGenPemFilter"),
                "certificate.pem"));
    }

    [RelayCommand]
    private void SavePfx()
    {
        if (_selfSignedResult is null && _caLeafResult is null)
        {
            return;
        }

        PfxPasswordRequested?.Invoke(
            this,
            new PfxPasswordRequest(
                L("ToolCertGenPfxPasswordTitle"),
                L("ToolCertGenPfxPasswordPrompt"),
                password =>
                {
                    if (password is null)
                    {
                        return;
                    }

                    try
                    {
                        var bytes = _selfSignedResult is not null
                            ? _service.BuildPfx(_selfSignedResult, password)
                            : _service.BuildPfx(_caLeafResult!, password);

                        SaveFileRequested?.Invoke(
                            this,
                            new SaveFileRequest(
                                bytes,
                                true,
                                L("ToolCertGenPfxFilter"),
                                "certificate.pfx",
                                ContainsPrivateKey: true));
                    }
                    catch (CryptographicException ex)
                    {
                        ReportSaveFailure(ex.Message);
                    }
                }));
    }

    [RelayCommand]
    private void ToggleHelp() => IsHelpVisible = !IsHelpVisible;

    partial void OnModeChanged(CertificateMode value)
    {
        OnPropertyChanged(nameof(IsSelfSigned));
        OnPropertyChanged(nameof(IsCaLeafMode));
        RaiseResultPropertiesChanged();
    }

    partial void OnIsKeyVisibleChanged(bool value)
    {
        OnPropertyChanged(nameof(DisplayedKeyText));
        OnPropertyChanged(nameof(BtnShowKeyText));
    }

    partial void OnIsLeafKeyVisibleChanged(bool value)
    {
        OnPropertyChanged(nameof(DisplayedLeafKeyText));
        OnPropertyChanged(nameof(BtnShowLeafKeyText));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_localizer is not null && _localeChangedHandler is not null)
        {
            _localizer.LocaleChanged -= _localeChangedHandler;
        }

        _localizer = null;
        _selfSignedResult = null;
        _caLeafResult = null;
        GC.SuppressFinalize(this);
    }

    private void RefreshLocalizedMessages()
    {
        OnPropertyChanged(nameof(HelpContentText));
        OnPropertyChanged(nameof(CertLabelText));
        OnPropertyChanged(nameof(KeyLabelText));
        OnPropertyChanged(nameof(BtnShowKeyText));
        OnPropertyChanged(nameof(BtnShowLeafKeyText));

        ValidationMessage = _lastMessageKind switch
        {
            CertificateMessageKind.None => null,
            CertificateMessageKind.ValidationCode => L(ErrorKeyFor(_lastValidationCode)),
            CertificateMessageKind.Generating => L("ToolCertGenGenerating"),
            CertificateMessageKind.GenerationError => string.Format(
                CultureInfo.InvariantCulture,
                L("ToolCertGenErrorGeneration"),
                _lastMessageDetail ?? string.Empty),
            CertificateMessageKind.ExportError => string.Format(
                CultureInfo.InvariantCulture,
                L("ToolCertGenErrorExport"),
                _lastMessageDetail ?? string.Empty),
            _ => ValidationMessage,
        };
    }

    private void SetValidationCode(CertificateValidationCode code)
    {
        _lastValidationCode = code;
        _lastMessageKind = CertificateMessageKind.ValidationCode;
        _lastMessageDetail = null;
        ValidationMessage = L(ErrorKeyFor(code));
    }

    private void ClearValidation()
    {
        _lastValidationCode = CertificateValidationCode.Ok;
        _lastMessageKind = CertificateMessageKind.None;
        _lastMessageDetail = null;
        ValidationMessage = null;
    }

    private void RaiseResultPropertiesChanged()
    {
        OnPropertyChanged(nameof(HasResult));
        OnPropertyChanged(nameof(FingerprintText));
        OnPropertyChanged(nameof(CurrentCertPem));
        OnPropertyChanged(nameof(CurrentKeyPem));
        OnPropertyChanged(nameof(LeafCertPem));
        OnPropertyChanged(nameof(LeafKeyPem));
        OnPropertyChanged(nameof(DisplayedKeyText));
        OnPropertyChanged(nameof(DisplayedLeafKeyText));
        OnPropertyChanged(nameof(CertLabelText));
        OnPropertyChanged(nameof(KeyLabelText));
        OnPropertyChanged(nameof(IsFingerprintPanelVisible));
        OnPropertyChanged(nameof(IsCertPanelVisible));
        OnPropertyChanged(nameof(IsKeyPanelVisible));
        OnPropertyChanged(nameof(IsLeafPanelVisible));
        OnPropertyChanged(nameof(IsExportPanelVisible));
    }

    private static string ErrorKeyFor(CertificateValidationCode code) => code switch
    {
        CertificateValidationCode.CnRequired => "ToolCertGenErrorCnRequired",
        CertificateValidationCode.InvalidValidity => "ToolCertGenErrorInvalidValidity",
        _ => string.Empty,
    };

    private string L(string key) => _localizer?[key] ?? key;

    private enum CertificateMessageKind
    {
        None,
        ValidationCode,
        Generating,
        GenerationError,
        ExportError,
    }
}
