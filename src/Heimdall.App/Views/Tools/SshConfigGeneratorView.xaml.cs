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

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;
using Heimdall.Core.Ssh;
using Microsoft.Extensions.DependencyInjection;

namespace Heimdall.App.Views.Tools;

/// <summary>
/// Generates OpenSSH config blocks from form inputs or from the entire Heimdall server inventory.
/// Output follows the standard ~/.ssh/config format with only non-default fields included.
/// </summary>
public partial class SshConfigGeneratorView : UserControl, IToolView
{
    private const int DefaultAliveInterval = 0;
    private const int DefaultAliveIntervalInput = 60;

    private LocalizationManager? _localizer;
    private ToolContext? _context;
    private bool _keyHandlersAttached;

    public SshConfigGeneratorView()
    {
        InitializeComponent();
    }

    public void Initialize(ToolContext? context, LocalizationManager? localizer)
    {
        if (_localizer is not null)
        {
            _localizer.LocaleChanged -= OnLocaleChanged;
        }

        _localizer = localizer;
        _context = context;
        if (_localizer is not null)
        {
            _localizer.LocaleChanged += OnLocaleChanged;
        }

        ApplyLocalization();
        TxtPort.Text = DefaultPorts.Ssh.ToString();
        TxtServerAliveInterval.Text = DefaultAliveIntervalInput.ToString();

        if (!_keyHandlersAttached)
        {
            _keyHandlersAttached = true;
            TxtHostAlias.KeyDown += (s, e) => { if (e.Key == System.Windows.Input.Key.Enter) OnGenerateClick(s, e); };
            TxtHostName.KeyDown += (s, e) => { if (e.Key == System.Windows.Input.Key.Enter) OnGenerateClick(s, e); };
            TxtUser.KeyDown += (s, e) => { if (e.Key == System.Windows.Input.Key.Enter) OnGenerateClick(s, e); };
            TxtPort.KeyDown += (s, e) => { if (e.Key == System.Windows.Input.Key.Enter) OnGenerateClick(s, e); };
            TxtIdentityFile.KeyDown += (s, e) => { if (e.Key == System.Windows.Input.Key.Enter) OnGenerateClick(s, e); };
            TxtProxyJump.KeyDown += (s, e) => { if (e.Key == System.Windows.Input.Key.Enter) OnGenerateClick(s, e); };
            TxtServerAliveInterval.KeyDown += (s, e) => { if (e.Key == System.Windows.Input.Key.Enter) OnGenerateClick(s, e); };
        }

        // Pre-fill from context if available
        if (!string.IsNullOrWhiteSpace(context?.TargetHost))
        {
            TxtHostName.Text = context.TargetHost;
            TxtHostAlias.Text = context.DisplayName ?? context.TargetHost;
        }

        if (!string.IsNullOrWhiteSpace(context?.Username))
        {
            TxtUser.Text = context.Username;
        }

        if (context?.TargetPort is > 0)
        {
            TxtPort.Text = context.TargetPort.Value.ToString();
        }

        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            TxtHostAlias.Focus();
            TxtHostAlias.SelectAll();
        });
    }

    private void OnLocaleChanged(string _)
    {
        Dispatcher.BeginInvoke(ApplyLocalization);
    }

    private void ApplyLocalization()
    {
        HeaderTitle.Text = L("ToolSshConfigTitle");
        LblHostAlias.Text = L("ToolSshConfigHostAlias");
        LblHostName.Text = L("ToolSshConfigHostName");
        LblUser.Text = L("ToolSshConfigUser");
        LblPort.Text = L("ToolSshConfigPort");
        LblIdentityFile.Text = L("ToolSshConfigIdentityFile");
        LblProxyJump.Text = L("ToolSshConfigProxyJump");
        LblForwardAgent.Text = L("ToolSshConfigForwardAgent");
        LblServerAliveInterval.Text = L("ToolSshConfigAliveInterval");
        BtnGenerate.Content = L("ToolSshConfigBtnGenerate");
        BtnGenerateAll.Content = L("ToolSshConfigBtnGenerateAll");
        BtnCopy.Content = L("ToolSshConfigBtnCopy");

        AutomationProperties.SetName(TxtHostAlias, L("ToolSshConfigHostAlias"));
        AutomationProperties.SetName(TxtHostName, L("ToolSshConfigHostName"));
        AutomationProperties.SetName(TxtUser, L("ToolSshConfigUser"));
        AutomationProperties.SetName(TxtPort, L("ToolSshConfigPort"));
        AutomationProperties.SetName(TxtIdentityFile, L("ToolSshConfigIdentityFile"));
        AutomationProperties.SetName(TxtProxyJump, L("ToolSshConfigProxyJump"));
        AutomationProperties.SetName(ChkForwardAgent, L("ToolSshConfigForwardAgent"));
        AutomationProperties.SetName(TxtServerAliveInterval, L("ToolSshConfigAliveInterval"));
        AutomationProperties.SetName(BtnGenerate, L("ToolSshConfigBtnGenerate"));
        AutomationProperties.SetName(BtnGenerateAll, L("ToolSshConfigBtnGenerateAll"));
        AutomationProperties.SetName(BtnCopy, L("ToolSshConfigBtnCopy"));
        AutomationProperties.SetName(TxtOutput, L("ToolSshConfigOutput"));

        BtnHelp.ToolTip = L("ToolHelpTooltip");
        AutomationProperties.SetName(BtnHelp, L("ToolHelpTooltip"));
        AutomationProperties.SetName(BtnCloseHelp, L("BtnClose"));
        TxtEmptyState.Text = L("ToolSshConfigEmptyState");
    }

    private void OnGenerateClick(object sender, RoutedEventArgs e)
    {
        var alias = TxtHostAlias.Text.Trim();
        var hostname = TxtHostName.Text.Trim();

        if (string.IsNullOrWhiteSpace(hostname))
        {
            TxtOutput.Text = L("ToolSshConfigErrorHostRequired");
            EmptyStatePanel.Visibility = Visibility.Collapsed;
            OutputPanel.Visibility = Visibility.Visible;
            return;
        }

        if (string.IsNullOrWhiteSpace(alias))
        {
            alias = hostname;
        }

        var block = SshConfigExporter.ExportSingleHost(new SshConfigHostEntry(
            Alias: alias,
            HostName: hostname,
            User: TxtUser.Text.Trim(),
            Port: ParseInt(TxtPort.Text, DefaultPorts.Ssh),
            IdentityFile: TxtIdentityFile.Text.Trim(),
            ProxyJump: TxtProxyJump.Text.Trim(),
            ForwardAgent: ChkForwardAgent.IsChecked == true,
            ServerAliveInterval: ParseInt(TxtServerAliveInterval.Text, DefaultAliveInterval)));

        TxtOutput.Text = block;
        EmptyStatePanel.Visibility = Visibility.Collapsed;
        OutputPanel.Visibility = Visibility.Visible;
    }

    private async void OnGenerateAllClick(object sender, RoutedEventArgs e)
    {
        var configManager = (Application.Current as App)?.Services?.GetService<IConfigManager>();
        if (configManager is null)
        {
            TxtOutput.Text = L("ToolSshConfigGenerateAllHint");
            EmptyStatePanel.Visibility = Visibility.Collapsed;
            OutputPanel.Visibility = Visibility.Visible;
            return;
        }

        BtnGenerateAll.IsEnabled = false;
        try
        {
            var servers = await configManager.LoadServersAsync().ConfigureAwait(true);
            var settings = await configManager.LoadSettingsAsync().ConfigureAwait(true);
            var aliveInterval = ParseInt(TxtServerAliveInterval.Text, DefaultAliveIntervalInput);
            var exported = SshConfigExporter.ExportInventory(servers, settings.SshGateways, aliveInterval);

            TxtOutput.Text = string.IsNullOrWhiteSpace(exported)
                ? L("ToolSshConfigGenerateAllEmpty")
                : exported;
            EmptyStatePanel.Visibility = Visibility.Collapsed;
            OutputPanel.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            Heimdall.Core.Logging.FileLogger.Warn($"SSH config inventory export failed: {ex.Message}");
            TxtOutput.Text = L("ToolSshConfigGenerateAllHint");
            EmptyStatePanel.Visibility = Visibility.Collapsed;
            OutputPanel.Visibility = Visibility.Visible;
        }
        finally
        {
            BtnGenerateAll.IsEnabled = true;
        }
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(TxtOutput.Text))
        {
            try { Clipboard.SetText(TxtOutput.Text); }
            catch (System.Runtime.InteropServices.ExternalException) { return; }
            CopyFeedbackHelper.ShowCopyFeedback(sender as Button);
        }
    }

    private static int ParseInt(string text, int fallback)
    {
        return int.TryParse(text.Trim(), out var value) ? value : fallback;
    }

    private void OnHelpClick(object sender, RoutedEventArgs e)
    {
        if (HelpPanel.Visibility == Visibility.Visible)
        {
            HelpPanel.Visibility = Visibility.Collapsed;
            return;
        }
        TxtHelpContent.Text = L("ToolHelpSSHCONFIG").Replace("\\n", "\n");
        HelpPanel.Visibility = Visibility.Visible;
    }

    private void OnCloseHelpClick(object sender, RoutedEventArgs e)
    {
        HelpPanel.Visibility = Visibility.Collapsed;
    }

    private string L(string key) => _localizer?[key] ?? key;

    public void Dispose()
    {
        if (_localizer is not null)
        {
            _localizer.LocaleChanged -= OnLocaleChanged;
        }

        GC.SuppressFinalize(this);
    }
}
