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

using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Heimdall.App.ViewModels.Tools;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;
using Heimdall.Core.Security;
using Microsoft.Win32;

namespace Heimdall.App.Views.Tools;

/// <summary>
/// Parses SSH keys (public and private, OpenSSH and PEM formats) and audits
/// their security strength. Displays algorithm, key size, fingerprint, format,
/// encryption status, and actionable security recommendations.
/// </summary>
public partial class SshKeyAuditView : UserControl, IToolView
{
    private const int DebounceDelayMs = 200;
    private const string KeyFileFilterKey = "ToolSshAuditKeyFileFilter";

    private LocalizationManager? _localizer;
    private readonly SshKeyAuditViewModel _vm;
    private DispatcherTimer? _debounceTimer;

    private sealed class FindingDisplayItem
    {
        public string Icon { get; init; } = string.Empty;
        public string Text { get; init; } = string.Empty;
        public Brush IconBrush { get; init; } = Brushes.Transparent;
    }

    public SshKeyAuditView()
    {
        InitializeComponent();
        _vm = new SshKeyAuditViewModel();
        DataContext = _vm;
        _vm.PropertyChanged += OnVmPropertyChanged;
        InitializeDebounceTimer();
    }

    /// <inheritdoc/>
    public void Initialize(ToolContext? context, LocalizationManager? localizer)
    {
        _localizer = localizer;
        _vm.Initialize(localizer);
        ApplyLocalization();

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            KeyInput.Focus();
            KeyInput.SelectAll();
        });
    }

    private void ApplyLocalization()
    {
        HeaderTitle.Text = L("ToolSshAuditTitle");
        InputLabel.Text = L("ToolSshAuditInput");
        BtnBrowse.Content = L("ToolSshAuditBtnBrowse");
        EmptyState.Text = L("ToolSshAuditEmptyState");
        ParseError.Text = L("ToolSshAuditParseError");
        BtnCopyFingerprint.Content = L("ToolSshAuditBtnCopy");
        FindingsTitle.Text = L("ToolSshAuditRating");

        DetailAlgorithmLabel.Text = L("ToolSshAuditAlgorithm");
        DetailKeySizeLabel.Text = L("ToolSshAuditKeySize");
        DetailFingerprintLabel.Text = L("ToolSshAuditFingerprint");
        DetailFormatLabel.Text = L("ToolSshAuditFormat");
        DetailTypeLabel.Text = L("ToolSshAuditType");
        DetailEncryptedLabel.Text = L("ToolSshAuditEncrypted");

        System.Windows.Automation.AutomationProperties.SetName(BtnHelp, L("ToolHelpTooltip"));
        System.Windows.Automation.AutomationProperties.SetName(BtnBrowse, L("ToolSshAuditBtnBrowse"));
        System.Windows.Automation.AutomationProperties.SetName(BtnCopyFingerprint, L("ToolSshAuditBtnCopy"));
        System.Windows.Automation.AutomationProperties.SetName(KeyInput, L("ToolSshAuditInput"));
        System.Windows.Automation.AutomationProperties.SetName(DetailFingerprintValue, L("ToolSshAuditFingerprint"));

        BtnHelp.ToolTip = L("ToolHelpTooltip");
        BtnCopyFingerprint.ToolTip = L("ToolBtnCopyToClipboard");
        System.Windows.Automation.AutomationProperties.SetName(BtnCloseHelp, L("BtnClose"));

        KeyInput.Tag = L("ToolWatermarkSshKey");
    }

    private void InitializeDebounceTimer()
    {
        _debounceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(DebounceDelayMs)
        };
        _debounceTimer.Tick += (_, _) =>
        {
            _debounceTimer.Stop();
            _vm.RunAuditCommand.Execute(null);
        };
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SshKeyAuditViewModel.Rating):
                UpdateBadgeBrushes();
                break;
            case nameof(SshKeyAuditViewModel.Findings):
                ProjectFindings();
                break;
            case nameof(SshKeyAuditViewModel.ShowResults):
                if (_vm.ShowResults)
                {
                    UpdateResultDisplay();
                }
                break;
        }
    }

    private void UpdateBadgeBrushes()
    {
        AlgorithmBadge.Background = GetAlgorithmBrush(_vm.Algorithm);
        RatingBadge.Background = GetRatingBrush(_vm.Rating);
        RatingBadgeText.Text = GetRatingDisplayText(_vm.Rating);
    }

    private void UpdateResultDisplay()
    {
        AlgorithmBadgeText.Text = _vm.Algorithm;
        DetailKeySizeValue.Text = string.Format(L("ToolSshAuditBitsLabel"), _vm.KeySize);
        DetailTypeValue.Text = _vm.IsPrivateKey ? L("ToolSshAuditPrivate") : L("ToolSshAuditPublic");
        DetailEncryptedValue.Text = _vm.IsEncrypted ? L("ToolSshAuditYes") : L("ToolSshAuditNo");
    }

    private void ProjectFindings()
    {
        FindingsList.ItemsSource = _vm.Findings.Select(f => new FindingDisplayItem
        {
            Icon = f.Severity switch
            {
                FindingSeverity.Pass => "\u2713",
                FindingSeverity.Warning => "\u26A0",
                FindingSeverity.Fail => "\u2717",
                _ => string.Empty
            },
            Text = f.Text,
            IconBrush = f.Severity switch
            {
                FindingSeverity.Pass => (Brush)FindResource("SuccessTextBrush"),
                FindingSeverity.Warning => (Brush)FindResource("WarningTextBrush"),
                FindingSeverity.Fail => (Brush)FindResource("ErrorTextBrush"),
                _ => (Brush)FindResource("TextSecondaryBrush")
            }
        }).ToList();
    }

    private void OnKeyInputTextChanged(object sender, TextChangedEventArgs e)
    {
        _debounceTimer?.Stop();
        _debounceTimer?.Start();
    }

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = L(KeyFileFilterKey)
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }

        // The view model reports an oversized or unreadable file through
        // FileErrorMessage; the view used to drop those cases without a word.
        _vm.LoadKeyFile(dialog.FileName);
    }

    private void OnHelpClick(object sender, RoutedEventArgs e)
    {
        if (HelpPanel.Visibility == Visibility.Visible)
        {
            HelpPanel.Visibility = Visibility.Collapsed;
            return;
        }

        TxtHelpContent.Text = L("ToolHelpSSHAUDIT").Replace("\\n", "\n");
        HelpPanel.Visibility = Visibility.Visible;
    }

    private void OnCloseHelpClick(object sender, RoutedEventArgs e)
    {
        HelpPanel.Visibility = Visibility.Collapsed;
    }

    private void OnCopyFingerprintClick(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_vm.Fingerprint))
        {
            try
            {
                Clipboard.SetText(_vm.Fingerprint);
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                return;
            }

            CopyFeedbackHelper.ShowCopyFeedback(sender as Button);
        }
    }

    private Brush GetAlgorithmBrush(string algorithm) => algorithm switch
    {
        "Ed25519" => (Brush)FindResource("SuccessTextBrush"),
        "RSA" => (Brush)FindResource("InfoBrush"),
        "ECDSA" => (Brush)FindResource("AccentBrush"),
        "DSA" => (Brush)FindResource("ErrorTextBrush"),
        _ => (Brush)FindResource("TextSecondaryBrush")
    };

    private Brush GetRatingBrush(SecurityRating rating) => rating switch
    {
        SecurityRating.Strong => (Brush)FindResource("SuccessTextBrush"),
        SecurityRating.Acceptable => (Brush)FindResource("AccentBrush"),
        SecurityRating.Weak => (Brush)FindResource("WarningTextBrush"),
        SecurityRating.Deprecated => (Brush)FindResource("ErrorTextBrush"),
        _ => (Brush)FindResource("AccentBrush")
    };

    private string GetRatingDisplayText(SecurityRating rating) => rating switch
    {
        SecurityRating.Strong => L("ToolSshAuditStrong"),
        SecurityRating.Acceptable => L("ToolSshAuditAcceptable"),
        SecurityRating.Weak => L("ToolSshAuditWeak"),
        SecurityRating.Deprecated => L("ToolSshAuditDeprecated"),
        _ => string.Empty
    };

    private string L(string key) => _localizer?[key] ?? key;

    /// <inheritdoc/>
    public void Dispose()
    {
        _vm.PropertyChanged -= OnVmPropertyChanged;

        if (_debounceTimer is not null)
        {
            _debounceTimer.Stop();
            _debounceTimer = null;
        }

        GC.SuppressFinalize(this);
    }
}
