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

using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;
using Microsoft.Web.WebView2.Core;

namespace Heimdall.App.Views.Tools;

/// <summary>
/// Embedded draw.io diagram editor hosted via WebView2 in embed mode.
/// Uses an iframe wrapper (heimdall-host.html) because draw.io's embed
/// protocol requires (window.opener || window.parent) != window.
/// draw.io's own top toolbar is disabled in the iframe host - Heimdall provides
/// the command surface for reliable actions such as undo/redo and zoom.
/// </summary>
public partial class DiagramEditorView : UserControl, IToolView
{
    private LocalizationManager? _localizer;
    private bool _disposed;
    private string? _currentFilePath;
    private string? _lastSavedXml;
    private const string VirtualHost = "heimdall-drawio.local";

    public DiagramEditorView()
    {
        InitializeComponent();
    }

    /// <inheritdoc />
    public void Initialize(ToolContext? context, LocalizationManager? localizer)
    {
        _localizer = localizer;
        ApplyLocalization();

        if (!string.IsNullOrWhiteSpace(context?.Argument))
        {
            _currentFilePath = context.Argument;
        }

        _ = InitializeWebViewAsync();
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            if (!Services.WebView2Helper.IsAvailable)
            {
                ShowFallback(L("ErrorWebView2NotFound"));
                return;
            }

            // Draw.io assets are excluded from Debug builds (~48 MB / 2258 files,
            // see Heimdall.App.csproj). Show a friendly message instead of crashing.
            var assetsPath = Path.Combine(AppContext.BaseDirectory, "Assets", "drawio");
            if (!Directory.Exists(assetsPath))
            {
                Core.Logging.FileLogger.Warn(
                    "[DiagramEditor] Draw.io assets not found at " + assetsPath +
                    " - expected in Release builds only.");
                ShowFallback(L("DiagramEditorDebugOnly"));
                return;
            }

            var env = await Services.WebView2Helper.CreateEnvironmentAsync("DrawIO");
            await DiagramWebView.EnsureCoreWebView2Async(env);

            var core = DiagramWebView.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;

            // Virtual host mapping for local draw.io files
            core.SetVirtualHostNameToFolderMapping(
                VirtualHost, assetsPath,
                CoreWebView2HostResourceAccessKind.Allow);

            core.WebMessageReceived += OnWebMessageReceived;

            // Block navigation away from draw.io
            core.NavigationStarting += (_, navArgs) =>
            {
                if (navArgs.Uri is not null
                    && !navArgs.Uri.StartsWith($"https://{VirtualHost}", StringComparison.OrdinalIgnoreCase)
                    && !navArgs.Uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase)
                    && !navArgs.Uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    navArgs.Cancel = true;
                }
            };

            // Load the host page (iframe wrapper for draw.io embed protocol)
            core.Navigate($"https://{VirtualHost}/heimdall-host.html");
        }
        catch (Exception ex)
        {
            ShowFallback(ex.Message);
        }
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        var message = e.TryGetWebMessageAsString();
        if (string.IsNullOrEmpty(message)) return;

        if (message == "ready:")
        {
            SetToolbarEnabled(true);

            if (_currentFilePath is not null && File.Exists(_currentFilePath))
            {
                var xml = File.ReadAllText(_currentFilePath);
                _lastSavedXml = xml;
                PostWebMessage($"load:{xml}");
            }

            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
            {
                DiagramWebView.Focus();
            });
            return;
        }

        if (message.StartsWith("open-link:", StringComparison.Ordinal))
        {
            OpenExternalLink(message["open-link:".Length..]);
            return;
        }

        if (message.StartsWith("save:", StringComparison.Ordinal))
        {
            _lastSavedXml = message["save:".Length..];
            return;
        }

        if (message.StartsWith("export:", StringComparison.Ordinal))
        {
            HandleExportData(message["export:".Length..]);
            return;
        }
    }

    private void HandleExportData(string data)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog();

        if (data.StartsWith("data:image/png", StringComparison.OrdinalIgnoreCase))
        {
            dialog.Filter = "PNG (*.png)|*.png";
            dialog.FileName = "diagram.png";

            if (dialog.ShowDialog() != true) return;

            var base64Start = data.IndexOf(",", StringComparison.Ordinal);
            if (base64Start < 0) return;

            var base64Data = data[(base64Start + 1)..];
            var bytes = Convert.FromBase64String(base64Data);
            File.WriteAllBytes(dialog.FileName, bytes);
        }
        else
        {
            dialog.Filter = "SVG (*.svg)|*.svg";
            dialog.FileName = "diagram.svg";

            if (dialog.ShowDialog() != true) return;

            File.WriteAllText(dialog.FileName, data, Encoding.UTF8);
        }
    }

    private void OnNewClick(object sender, RoutedEventArgs e)
    {
        _currentFilePath = null;
        _lastSavedXml = null;
        PostWebMessage("load:");
    }

    private void OnOpenClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Draw.io (*.drawio)|*.drawio|XML (*.xml)|*.xml|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog() == true)
        {
            _currentFilePath = dialog.FileName;
            var xml = File.ReadAllText(dialog.FileName);
            _lastSavedXml = xml;
            PostWebMessage($"load:{xml}");
        }
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_lastSavedXml)) return;

        if (_currentFilePath is null)
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "Draw.io (*.drawio)|*.drawio",
                FileName = "diagram.drawio"
            };
            if (dialog.ShowDialog() != true) return;
            _currentFilePath = dialog.FileName;
        }

        File.WriteAllText(_currentFilePath, _lastSavedXml, Encoding.UTF8);
    }

    private void OnExportPngClick(object sender, RoutedEventArgs e)
    {
        PostWebMessage("export-png:");
    }

    private void OnInsertLineClick(object sender, RoutedEventArgs e)
    {
        PostEditorCommand("insertEdge");
    }

    private void OnFormatClick(object sender, RoutedEventArgs e)
    {
        PostEditorCommand("format");
    }

    private void OnUndoClick(object sender, RoutedEventArgs e)
    {
        PostEditorCommand("undo");
    }

    private void OnRedoClick(object sender, RoutedEventArgs e)
    {
        PostEditorCommand("redo");
    }

    private void OnZoomOutClick(object sender, RoutedEventArgs e)
    {
        PostEditorCommand("zoomOut");
    }

    private void OnActualSizeClick(object sender, RoutedEventArgs e)
    {
        PostEditorCommand("resetView");
    }

    private void OnZoomInClick(object sender, RoutedEventArgs e)
    {
        PostEditorCommand("zoomIn");
    }

    private void OnDuplicateClick(object sender, RoutedEventArgs e)
    {
        PostEditorCommand("duplicate");
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        PostEditorCommand("delete");
    }

    private void OnHelpClick(object sender, RoutedEventArgs e)
    {
        if (HelpPanel.Visibility == Visibility.Visible)
        {
            HelpPanel.Visibility = Visibility.Collapsed;
            return;
        }
        TxtHelpContent.Text = L("ToolHelpDIAGRAM").Replace("\\n", "\n");
        HelpPanel.Visibility = Visibility.Visible;
    }

    private void OnCloseHelpClick(object sender, RoutedEventArgs e)
    {
        HelpPanel.Visibility = Visibility.Collapsed;
    }

    private void PostWebMessage(string message)
    {
        if (DiagramWebView.CoreWebView2 is not null)
        {
            DiagramWebView.CoreWebView2.PostWebMessageAsString(message);
        }
    }

    private void PostEditorCommand(string action)
    {
        PostWebMessage($"command:{action}");
    }

    private static void OpenExternalLink(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (!document.RootElement.TryGetProperty("href", out var hrefProperty))
                return;

            var href = hrefProperty.GetString();
            if (string.IsNullOrWhiteSpace(href)
                || !Uri.TryCreate(href, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return;
            }

            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch
        {
            // Ignore malformed link payloads
        }
    }

    private void ApplyLocalization()
    {
        HeaderTitle.Text = L("ToolDiagramTitle");
        BtnNew.Content = L("ToolDiagramBtnNew");
        BtnOpen.Content = L("ToolDiagramBtnOpen");
        BtnSave.Content = L("ToolDiagramBtnSave");
        BtnExportPng.Content = L("ToolDiagramBtnExportPng");
        BtnInsertLine.Content = L("ToolDiagramBtnInsertLine");
        BtnFormat.Content = L("ToolDiagramBtnFormat");
        BtnUndo.Content = L("BtnUndo");
        BtnRedo.Content = L("ToolDiagramBtnRedo");
        BtnZoomOut.Content = L("ToolDiagramBtnZoomOut");
        BtnActualSize.Content = L("ToolDiagramBtnActualSize");
        BtnZoomIn.Content = L("ToolDiagramBtnZoomIn");
        BtnDuplicate.Content = L("BtnDuplicate");
        BtnDelete.Content = L("BtnDelete");

        BtnHelp.ToolTip = L("ToolHelpTooltip");
        System.Windows.Automation.AutomationProperties.SetName(BtnHelp, L("ToolHelpTooltip"));
        System.Windows.Automation.AutomationProperties.SetName(BtnCloseHelp, L("BtnClose"));
        System.Windows.Automation.AutomationProperties.SetName(BtnNew, L("ToolDiagramBtnNew"));
        System.Windows.Automation.AutomationProperties.SetName(BtnOpen, L("ToolDiagramBtnOpen"));
        System.Windows.Automation.AutomationProperties.SetName(BtnSave, L("ToolDiagramBtnSave"));
        System.Windows.Automation.AutomationProperties.SetName(BtnExportPng, L("ToolDiagramBtnExportPng"));
        System.Windows.Automation.AutomationProperties.SetName(BtnInsertLine, L("ToolDiagramBtnInsertLine"));
        System.Windows.Automation.AutomationProperties.SetName(BtnFormat, L("ToolDiagramBtnFormat"));
        System.Windows.Automation.AutomationProperties.SetName(BtnUndo, L("BtnUndo"));
        System.Windows.Automation.AutomationProperties.SetName(BtnRedo, L("ToolDiagramBtnRedo"));
        System.Windows.Automation.AutomationProperties.SetName(BtnZoomOut, L("ToolDiagramBtnZoomOut"));
        System.Windows.Automation.AutomationProperties.SetName(BtnActualSize, L("ToolDiagramBtnActualSize"));
        System.Windows.Automation.AutomationProperties.SetName(BtnZoomIn, L("ToolDiagramBtnZoomIn"));
        System.Windows.Automation.AutomationProperties.SetName(BtnDuplicate, L("BtnDuplicate"));
        System.Windows.Automation.AutomationProperties.SetName(BtnDelete, L("BtnDelete"));

        BtnNew.ToolTip = L("ToolDiagramBtnNew");
        BtnOpen.ToolTip = L("ToolDiagramBtnOpen");
        BtnSave.ToolTip = L("ToolDiagramBtnSave");
        BtnExportPng.ToolTip = L("ToolDiagramBtnExportPng");
        BtnInsertLine.ToolTip = L("ToolDiagramBtnInsertLine");
        BtnFormat.ToolTip = L("ToolDiagramBtnFormat");
        BtnUndo.ToolTip = L("BtnUndo");
        BtnRedo.ToolTip = L("ToolDiagramBtnRedo");
        BtnZoomOut.ToolTip = L("ToolDiagramBtnZoomOut");
        BtnActualSize.ToolTip = L("ToolDiagramBtnActualSize");
        BtnZoomIn.ToolTip = L("ToolDiagramBtnZoomIn");
        BtnDuplicate.ToolTip = L("BtnDuplicate");
        BtnDelete.ToolTip = L("BtnDelete");

        BtnNew.TabIndex = 0;
        BtnOpen.TabIndex = 1;
        BtnSave.TabIndex = 2;
        BtnExportPng.TabIndex = 3;
        BtnInsertLine.TabIndex = 4;
        BtnFormat.TabIndex = 5;
        BtnUndo.TabIndex = 6;
        BtnRedo.TabIndex = 7;
        BtnZoomOut.TabIndex = 8;
        BtnActualSize.TabIndex = 9;
        BtnZoomIn.TabIndex = 10;
        BtnDuplicate.TabIndex = 11;
        BtnDelete.TabIndex = 12;
        BtnHelp.TabIndex = 13;
        BtnCloseHelp.TabIndex = 14;

        SetToolbarEnabled(false);
    }

    private void SetToolbarEnabled(bool enabled)
    {
        BtnNew.IsEnabled = enabled;
        BtnOpen.IsEnabled = enabled;
        BtnSave.IsEnabled = enabled;
        BtnExportPng.IsEnabled = enabled;
        BtnInsertLine.IsEnabled = enabled;
        BtnFormat.IsEnabled = enabled;
        BtnUndo.IsEnabled = enabled;
        BtnRedo.IsEnabled = enabled;
        BtnZoomOut.IsEnabled = enabled;
        BtnActualSize.IsEnabled = enabled;
        BtnZoomIn.IsEnabled = enabled;
        BtnDuplicate.IsEnabled = enabled;
        BtnDelete.IsEnabled = enabled;
    }

    private void ShowFallback(string message)
    {
        DiagramWebView.Visibility = Visibility.Collapsed;
        FallbackPanel.Visibility = Visibility.Visible;
        FallbackTitle.Text = L("ToolDiagramTitle");
        FallbackMessage.Text = message;
    }

    private string L(string key) => _localizer?[key] ?? key;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (DiagramWebView.CoreWebView2 is not null)
            {
                DiagramWebView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
            }
            DiagramWebView.Dispose();
        }
        catch
        {
            // Disposal may fail if WebView2 was never initialized
        }

        GC.SuppressFinalize(this);
    }
}
