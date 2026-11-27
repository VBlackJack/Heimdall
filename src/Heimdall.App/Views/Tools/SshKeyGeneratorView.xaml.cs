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
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Heimdall.App.Localization;
using Heimdall.App.Services;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;
using Heimdall.Core.Ssh;
using Heimdall.Ssh;
using Microsoft.Extensions.DependencyInjection;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace Heimdall.App.Views.Tools;

/// <summary>
/// SSH key pair generator supporting Ed25519, RSA (2048/4096), and ECDSA (P-256/P-384/P-521)
/// with OpenSSH and PKCS#8 PEM private key export.
/// </summary>
public partial class SshKeyGeneratorView : UserControl, IToolView
{
    private const string MaskedPlaceholder = "********";
    private const string PublicKeyFileExtension = ".pub";
    private const string PemPrivateKeyFileExtension = ".pem";

    private string PublicKeyFileFilter => L("ToolSshKeyGenPubFilter");
    private string PemPrivateKeyFileFilter => L("ToolSshKeyGenPrivFilter");
    private string OpenSshPrivateKeyFileFilter => L("ToolSshKeyGenPrivOpenSshFilter");

    private readonly TimedSecretClipboard _secretClipboard = TimedSecretClipboard.CreateDefault();
    private LocalizationManager? _localizer;
    private string _privateKey = string.Empty;
    private string _publicKeyOpenSsh = string.Empty;
    private string _fingerprint = string.Empty;
    private string _defaultFileBaseName = "id_ed25519";
    private SshPrivateKeyFormat _lastGeneratedFormat = SshPrivateKeyFormat.OpenSsh;
    private bool _privateKeyVisible;
    private bool _isGenerating;

    public SshKeyGeneratorView()
    {
        InitializeComponent();
        CommentInput.KeyDown += (s, e) => { if (e.Key == System.Windows.Input.Key.Enter) OnGenerateClick(s, e); };
    }

    /// <summary>
    /// Initializes the view with optional context and localizer.
    /// </summary>
    public void Initialize(ToolContext? context, LocalizationManager? localizer)
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

        ApplyLocalization();
        ClearValidation();

        // Default comment: user@hostname
        CommentInput.Text = $"{Environment.UserName}@{Environment.MachineName}";

        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            CommentInput.Focus();
            CommentInput.SelectAll();
        });
    }

    private void OnLocaleChanged(string _)
    {
        Dispatcher.BeginInvoke(ApplyLocalization);
    }

    private void ApplyLocalization()
    {
        HeaderTitle.Text = L("ToolSshKeyGenTitle");
        AlgorithmLabel.Text = L("ToolSshKeyGenAlgorithm");
        FormatLabel.Text = L("ToolSshKeyGenFormat");
        CommentLabel.Text = L("ToolSshKeyGenComment");
        PassphraseLabel.Text = L("ToolSshKeyGenPassphrase");
        PassphraseHint.Text = L("ToolSshKeyGenPassphraseHint");
        BtnGenerate.Content = L("ToolSshKeyGenBtnGenerate");
        FingerprintLabel.Text = L("ToolSshKeyGenFingerprint");
        BtnCopyFingerprint.Content = L("ToolSshKeyGenBtnCopy");
        PublicKeyLabel.Text = L("ToolSshKeyGenPublicKey");
        BtnCopyPublicKey.Content = L("ToolSshKeyGenBtnCopy");
        BtnSavePublicKey.Content = L("ToolSshKeyGenBtnSave");
        BtnDeployPublicKey.Content = L("ToolSshKeyGenBtnDeploy");
        PrivateKeyLabel.Text = L("ToolSshKeyGenPrivateKey");
        BtnCopyPrivateKey.Content = L("ToolSshKeyGenBtnCopy");
        BtnShowPrivateKey.Content = _privateKeyVisible
            ? L("ToolSshKeyGenBtnHide")
            : L("ToolSshKeyGenBtnShow");
        BtnSavePrivateKey.Content = L("ToolSshKeyGenBtnSave");

        Ed25519Item.Content = L("ToolSshKeyGenEd25519");
        Rsa4096Item.Content = L("ToolSshKeyGenRsa4096");
        Rsa2048Item.Content = L("ToolSshKeyGenRsa2048");
        EcdsaP256Item.Content = L("ToolSshKeyGenEcdsaP256");
        EcdsaP384Item.Content = L("ToolSshKeyGenEcdsaP384");
        EcdsaP521Item.Content = L("ToolSshKeyGenEcdsaP521");
        FormatOpenSshItem.Content = L("ToolSshKeyGenFormatOpenSsh");
        FormatPkcs8Item.Content = L("ToolSshKeyGenFormatPkcs8");
        Ed25519Notice.Text = L("ToolSshKeyGenEd25519Notice");
        Ed25519Item.ToolTip = L("ToolSshKeyGenEd25519Tooltip");

        // Accessibility
        System.Windows.Automation.AutomationProperties.SetName(BtnGenerate, L("ToolSshKeyGenBtnGenerate"));
        System.Windows.Automation.AutomationProperties.SetName(BtnCopyFingerprint, L("ToolSshKeyGenBtnCopy"));
        System.Windows.Automation.AutomationProperties.SetName(BtnCopyPublicKey, L("ToolSshKeyGenBtnCopy"));
        System.Windows.Automation.AutomationProperties.SetName(BtnCopyPrivateKey, L("ToolSshKeyGenBtnCopy"));
        System.Windows.Automation.AutomationProperties.SetName(BtnSavePublicKey, L("ToolSshKeyGenBtnSave"));
        System.Windows.Automation.AutomationProperties.SetName(BtnDeployPublicKey, L("ToolSshKeyGenBtnDeploy"));
        System.Windows.Automation.AutomationProperties.SetName(BtnSavePrivateKey, L("ToolSshKeyGenBtnSave"));
        System.Windows.Automation.AutomationProperties.SetName(BtnShowPrivateKey, L("ToolSshKeyGenBtnShow"));
        System.Windows.Automation.AutomationProperties.SetName(AlgorithmCombo, L("ToolSshKeyGenAlgorithm"));
        System.Windows.Automation.AutomationProperties.SetName(FormatCombo, L("ToolSshKeyGenFormat"));
        System.Windows.Automation.AutomationProperties.SetName(CommentInput, L("ToolSshKeyGenComment"));
        System.Windows.Automation.AutomationProperties.SetName(PassphraseInput, L("ToolSshKeyGenPassphrase"));
        System.Windows.Automation.AutomationProperties.SetName(FingerprintOutput, L("ToolSshKeyGenFingerprint"));
        System.Windows.Automation.AutomationProperties.SetName(PublicKeyOutput, L("ToolSshKeyGenPublicKey"));
        System.Windows.Automation.AutomationProperties.SetName(PrivateKeyOutput, L("ToolSshKeyGenPrivateKey"));

        BtnCopyFingerprint.ToolTip = L("ToolBtnCopyToClipboard");
        BtnCopyPublicKey.ToolTip = L("ToolBtnCopyToClipboard");
        BtnCopyPrivateKey.ToolTip = L("ToolBtnCopyToClipboard");

        CommentInput.Tag = L("ToolSshKeyGenCommentPlaceholder");

        BtnHelp.ToolTip = L("ToolHelpTooltip");
        System.Windows.Automation.AutomationProperties.SetName(BtnHelp, L("ToolHelpTooltip"));
        System.Windows.Automation.AutomationProperties.SetName(BtnCloseHelp, L("BtnClose"));
    }

    private void OnHelpClick(object sender, RoutedEventArgs e)
    {
        if (HelpPanel.Visibility == Visibility.Visible)
        {
            HelpPanel.Visibility = Visibility.Collapsed;
            return;
        }
        TxtHelpContent.Text = L("ToolHelpSSHKEY").Replace("\\n", "\n");
        HelpPanel.Visibility = Visibility.Visible;
    }

    private void OnCloseHelpClick(object sender, RoutedEventArgs e)
    {
        HelpPanel.Visibility = Visibility.Collapsed;
    }

    private async void OnGenerateClick(object sender, RoutedEventArgs e)
    {
        if (_isGenerating) return;
        _isGenerating = true;
        BtnGenerate.IsEnabled = false;
        ShowValidation(L("ToolSshKeyGenGenerating"));

        try
        {
            var algorithm = GetSelectedAlgorithm();
            var format = GetSelectedFormat();
            var comment = CommentInput.Text.Trim();
            var passphrase = PassphraseInput.Password;

            var generated = await Task.Run(() =>
                SshKeyPairGenerator.Generate(algorithm, comment, passphrase, format));

            _publicKeyOpenSsh = generated.PublicKeyOpenSsh;
            _privateKey = generated.PrivateKey;
            _fingerprint = generated.Fingerprint;
            _defaultFileBaseName = generated.DefaultFileBaseName;
            _lastGeneratedFormat = generated.Format;

            ShowGeneratedKeys();
        }
        catch (Exception ex)
        {
            Heimdall.Core.Logging.FileLogger.Warn($"SSH key generation failed: {ex.Message}");
            ShowValidation(string.Format(CultureInfo.CurrentCulture, L("ToolSshKeyGenGenerateFailed"), ex.Message));
        }
        finally
        {
            _isGenerating = false;
            BtnGenerate.IsEnabled = true;
        }
    }

    private SshKeyAlgorithm GetSelectedAlgorithm()
    {
        var tag = (AlgorithmCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        return tag switch
        {
            "RSA2048" => SshKeyAlgorithm.Rsa2048,
            "RSA4096" => SshKeyAlgorithm.Rsa4096,
            "ECDSA256" => SshKeyAlgorithm.EcdsaP256,
            "ECDSA384" => SshKeyAlgorithm.EcdsaP384,
            "ECDSA521" => SshKeyAlgorithm.EcdsaP521,
            _ => SshKeyAlgorithm.Ed25519
        };
    }

    private SshPrivateKeyFormat GetSelectedFormat()
    {
        var tag = (FormatCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        return string.Equals(tag, "Pkcs8Pem", StringComparison.Ordinal)
            ? SshPrivateKeyFormat.Pkcs8Pem
            : SshPrivateKeyFormat.OpenSsh;
    }

    private void OnCopyPublicKeyClick(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_publicKeyOpenSsh))
        {
            try { Clipboard.SetText(_publicKeyOpenSsh); }
            catch (System.Runtime.InteropServices.ExternalException) { return; }
            CopyFeedbackHelper.ShowCopyFeedback(sender as Button);
        }
    }

    private void OnCopyPrivateKeyClick(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_privateKey))
        {
            if (!_secretClipboard.TryCopy(_privateKey)) { return; }
            CopyFeedbackHelper.ShowCopyFeedback(sender as Button);
        }
    }

    private void OnCopyFingerprintClick(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_fingerprint))
        {
            try { Clipboard.SetText(_fingerprint); }
            catch (System.Runtime.InteropServices.ExternalException) { return; }
            CopyFeedbackHelper.ShowCopyFeedback(sender as Button);
        }
    }

    private void OnTogglePrivateKeyClick(object sender, RoutedEventArgs e)
    {
        _privateKeyVisible = !_privateKeyVisible;
        PrivateKeyOutput.Text = _privateKeyVisible ? _privateKey : MaskedPlaceholder;
        BtnShowPrivateKey.Content = _privateKeyVisible
            ? L("ToolSshKeyGenBtnHide")
            : L("ToolSshKeyGenBtnShow");
    }

    private void OnSavePublicKeyClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_publicKeyOpenSsh)) return;

        var dialog = new SaveFileDialog
        {
            FileName = _defaultFileBaseName + PublicKeyFileExtension,
            Filter = PublicKeyFileFilter,
            DefaultExt = PublicKeyFileExtension
        };

        if (dialog.ShowDialog() == true)
        {
            TrySaveKey(() => Heimdall.Core.Security.SshKeyFileWriter.WritePublicKey(dialog.FileName, _publicKeyOpenSsh));
        }
    }

    private void OnSavePrivateKeyClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_privateKey)) return;

        var isOpenSsh = _lastGeneratedFormat == SshPrivateKeyFormat.OpenSsh;
        var dialog = new SaveFileDialog
        {
            FileName = isOpenSsh ? _defaultFileBaseName : _defaultFileBaseName + PemPrivateKeyFileExtension,
            Filter = isOpenSsh ? OpenSshPrivateKeyFileFilter : PemPrivateKeyFileFilter,
            DefaultExt = isOpenSsh ? string.Empty : PemPrivateKeyFileExtension
        };

        if (dialog.ShowDialog() == true)
        {
            TrySaveKey(() => Heimdall.Core.Security.SshKeyFileWriter.WritePrivateKey(dialog.FileName, _privateKey));
        }
    }

    private async void OnDeployPublicKeyClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_publicKeyOpenSsh))
        {
            return;
        }

        var services = (Application.Current as App)?.Services;
        if (_localizer is null)
        {
            return;
        }

        BtnDeployPublicKey.IsEnabled = false;
        try
        {
            await SshPublicKeyDeployUiHelper.DeployInteractivelyAsync(
                    services,
                    _publicKeyOpenSsh,
                    _localizer,
                    ShowValidation)
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Heimdall.Core.Logging.FileLogger.Warn($"SSH public key deployment failed: {ex.Message}");
            ShowValidation(string.Format(
                CultureInfo.CurrentCulture,
                L("ToolSshKeyGenDeployFailed"),
                ex.Message));
        }
        finally
        {
            BtnDeployPublicKey.IsEnabled = true;
        }
    }

    /// <summary>
    /// Runs a key save and shows the tool's validation message when the file cannot be written,
    /// instead of letting the error escape the click handler.
    /// </summary>
    private void TrySaveKey(Action save)
    {
        try
        {
            save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Heimdall.Core.Logging.FileLogger.Warn($"Saving an SSH key failed: {ex.Message}");
            ShowValidation(L("ToolSshKeyGenSaveFailed"));
        }
    }

    private void ShowGeneratedKeys()
    {
        ClearValidation();
        PublicKeyOutput.Text = _publicKeyOpenSsh;
        FingerprintOutput.Text = _fingerprint;

        _privateKeyVisible = false;
        PrivateKeyOutput.Text = MaskedPlaceholder;
        BtnShowPrivateKey.Content = L("ToolSshKeyGenBtnShow");

        FingerprintPanel.Visibility = Visibility.Visible;
        PublicKeyPanel.Visibility = Visibility.Visible;
        PrivateKeyPanel.Visibility = Visibility.Visible;
    }

    private void ClearValidation()
    {
        ValidationText.Text = string.Empty;
        ValidationText.Visibility = Visibility.Collapsed;
    }

    private void ShowValidation(string message)
    {
        ValidationText.Text = message;
        ValidationText.Visibility = string.IsNullOrWhiteSpace(message)
            ? Visibility.Collapsed
            : Visibility.Visible;
        ValidationText.BringIntoView();
    }

    private string L(string key) => _localizer?[key] ?? key;

    public void Dispose()
    {
        if (_localizer is not null)
        {
            _localizer.LocaleChanged -= OnLocaleChanged;
        }

        // A copied private key must not outlive the tool, and sensitive data leaves memory
        _secretClipboard.Dispose();
        _privateKey = string.Empty;
        GC.SuppressFinalize(this);
    }
}
