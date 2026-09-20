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
using Heimdall.App.Services;
using Heimdall.Core.Discovery;
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
/// <remarks>
/// The editor runs entirely offline: the iframe is loaded with offline=1 and
/// stealth=1, and index.html carries a Content-Security-Policy that keeps every
/// request on the local virtual host. Document state lives in
/// <see cref="DiagramDocumentState"/> so that saving, Save As and the
/// unsaved-changes prompt all read the same record.
/// </remarks>
public partial class DiagramEditorView : UserControl, IToolView
{
    private const string VirtualHost = "heimdall-drawio.local";
    private const string HostPagePath = "heimdall-host.html";
    private const string DarkTheme = "dark";
    private const string LightTheme = "light";

    /// <summary>Extension of a draw.io document, used for the default file name.</summary>
    private const string DiagramExtension = ".drawio";

    private const string PngExtension = ".png";
    private const string SvgExtension = ".svg";

    /// <summary>Luminance below which the current Heimdall background counts as dark.</summary>
    private const byte DarkBackgroundThreshold = 128;

    private readonly DiagramDocumentState _document = new();
    private readonly DiagramDraftStore _drafts = new();
    private readonly DiagramRecentFiles _recentFiles = new();
    private LocalizationManager? _localizer;
    private Action<SessionLaunchRequest>? _openSession;
    private WebViewDocumentPolicy? _hostPolicy;
    private bool _disposed;
    private bool _editorReady;

    /// <summary>Set while a save is waiting for the editor to report its content.</summary>
    private bool _savePending;

    /// <summary>Set when the pending save must ask for a new location (Save As).</summary>
    private bool _savePendingChoosesLocation;

    public DiagramEditorView()
    {
        InitializeComponent();
    }

    /// <inheritdoc />
    public void Initialize(ToolContext? context, LocalizationManager? localizer)
    {
        _localizer = localizer;
        _openSession = ToolContextMenuHelper.GetOpenSessionAction(context);
        ApplyLocalization();

        // A caller can hand over either a file on disk or an unsaved document body.
        // Unsaved content deliberately carries no path, so the first save asks for one.
        if (!string.IsNullOrEmpty(context?.DocumentContent))
        {
            _document.OpenUnsaved(context.DocumentContent);
        }
        else if (!string.IsNullOrWhiteSpace(context?.Argument))
        {
            TryOpenFile(context.Argument);
        }

        OfferDraftRecovery();
        UpdateDocumentIndicator();

        _ = InitializeWebViewAsync();
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            if (!WebView2Helper.IsAvailable)
            {
                ShowFallback(L("ErrorWebView2NotFound"));
                return;
            }

            // Draw.io assets are excluded from Debug builds (~44 MB / 2300 files,
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

            var hostUri = BuildHostUri();
            _hostPolicy = new WebViewDocumentPolicy(hostUri);

            var env = await WebView2Helper.CreateEnvironmentAsync("DrawIO");
            await DiagramWebView.EnsureCoreWebView2Async(env);

            var core = DiagramWebView.CoreWebView2;
            ConfigureWebViewSettings(core);

            // Virtual host mapping for local draw.io files
            core.SetVirtualHostNameToFolderMapping(
                VirtualHost, assetsPath,
                WebViewAssetAccess.Diagram);

            core.WebMessageReceived -= OnWebMessageReceived;
            core.WebMessageReceived += OnWebMessageReceived;
            core.NavigationStarting -= OnNavigationStarting;
            core.NavigationStarting += OnNavigationStarting;
            core.FrameNavigationStarting -= OnFrameNavigationStarting;
            core.FrameNavigationStarting += OnFrameNavigationStarting;
            core.NewWindowRequested -= OnNewWindowRequested;
            core.NewWindowRequested += OnNewWindowRequested;

            core.Navigate(hostUri);
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"[DiagramEditor] WebView2 initialization failed: {ex.Message}");
            ShowFallback(ex.Message);
        }
    }

    /// <summary>
    /// Locks down the WebView2 surface hosting the editor.
    /// </summary>
    /// <remarks>
    /// Browser accelerator keys matter most: F5 and Ctrl+R reload the host page
    /// and silently drop whatever the editor holds. The SSH and VNC surfaces
    /// already turned them off; this one did not.
    /// </remarks>
    private static void ConfigureWebViewSettings(CoreWebView2 core)
    {
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.AreDefaultScriptDialogsEnabled = false;
    }

    /// <summary>
    /// Builds the host page URL, carrying the locale and theme so the embedded
    /// editor follows Heimdall rather than the operating system.
    /// </summary>
    private string BuildHostUri()
    {
        var locale = _localizer?.CurrentLocale ?? "en";
        var language = locale.Split('-')[0].ToLowerInvariant();
        var theme = IsDarkTheme() ? DarkTheme : LightTheme;

        return $"https://{VirtualHost}/{HostPagePath}?lang={Uri.EscapeDataString(language)}"
            + $"&theme={Uri.EscapeDataString(theme)}";
    }

    private static bool IsDarkTheme()
    {
        return Application.Current?.TryFindResource("BackgroundBrush")
            is System.Windows.Media.SolidColorBrush background
            && background.Color.R < DarkBackgroundThreshold;
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs args)
    {
        // The host page is the only document this WebView may show.
        if (_hostPolicy?.IsTrustedDocument(args.Uri) != true)
        {
            args.Cancel = true;
        }
    }

    private void OnFrameNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs args)
    {
        // The iframe may only load draw.io itself, from the same virtual host.
        if (_hostPolicy?.IsTrustedOrigin(args.Uri) != true)
        {
            args.Cancel = true;
        }
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs args)
    {
        args.Handled = true;

        if (args.IsUserInitiated)
        {
            OpenExternalLink(args.Uri);
        }
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        // Only the host page may drive this view.
        var activeDocument = DiagramWebView.CoreWebView2?.Source;
        if (_hostPolicy?.ShouldAcceptMessage(e.Source, activeDocument) != true)
        {
            Core.Logging.FileLogger.Debug(
                "[DiagramEditor] Ignored a WebMessage from an untrusted document.");
            return;
        }

        var message = e.TryGetWebMessageAsString();
        if (string.IsNullOrEmpty(message)) return;

        if (message == "ready:")
        {
            // The host page is alive; the editor itself is not ready yet. Its
            // context menu carries Heimdall's own entries, whose labels live in
            // the locale catalogue rather than in draw.io's.
            PostWebMessage("labels:" + JsonSerializer.Serialize(
                new Dictionary<string, string>
                {
                    ["openSession"] = L("ToolDiagramCtxOpenSession")
                }));
            return;
        }

        if (message == "editor-ready:")
        {
            _editorReady = true;
            SetToolbarEnabled(true);

            if (_document.EditorXml is not null)
            {
                PostWebMessage($"load:{_document.EditorXml}");
            }

            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
            {
                DiagramWebView.Focus();
            });
            return;
        }

        if (message.StartsWith("open-link:", StringComparison.Ordinal))
        {
            HandleLinkPayload(message["open-link:".Length..]);
            return;
        }

        if (message.StartsWith("save-request:", StringComparison.Ordinal))
        {
            HandleSaveRequest(message["save-request:".Length..]);
            return;
        }

        if (message.StartsWith("save:", StringComparison.Ordinal))
        {
            // Autosave: track the content and keep a draft, but do not touch the
            // document's own file, which only an explicit save may write.
            var xml = message["save:".Length..];
            _document.EditorReported(xml);
            if (_document.IsDirty)
            {
                _drafts.TryWrite(_document.FilePath, xml);
            }
            UpdateDocumentIndicator();
            return;
        }

        if (message.StartsWith("export-xml:", StringComparison.Ordinal))
        {
            _document.EditorReported(message["export-xml:".Length..]);
            UpdateDocumentIndicator();

            if (_savePending)
            {
                _savePending = false;
                PerformSave(_savePendingChoosesLocation);
            }
            return;
        }

        if (message.StartsWith("export:", StringComparison.Ordinal))
        {
            HandleExportData(message["export:".Length..]);
            return;
        }
    }

    /// <summary>
    /// Ctrl+S inside the editor: the user asked for a write to disk, so the
    /// document reaches it.
    /// </summary>
    /// <remarks>
    /// The host page used to report this exactly like autosave, which only ever
    /// updated a field, so Ctrl+S wrote nothing and said nothing.
    /// </remarks>
    private void HandleSaveRequest(string xml)
    {
        _document.EditorReported(xml);
        UpdateDocumentIndicator();
        PerformSave(chooseNewLocation: false);
    }

    private void HandleExportData(string data)
    {
        // The editor answers with a data URI for SVG as well as for PNG, so the
        // payload is decoded rather than written through as text.
        var payload = DiagramExportPayload.Parse(data);
        if (payload is null)
        {
            Core.Logging.FileLogger.Warn(
                "[DiagramEditor] The editor returned an export payload that could not be read.");
            ShowInlineError(L("ToolDiagramErrorExportEmpty"));
            return;
        }

        var isPng = payload.Kind == DiagramExportKind.Png;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = isPng ? L("FileDialogPngFilter") : L("FileDialogSvgFilter"),
            FileName = DefaultFileName(isPng ? PngExtension : SvgExtension)
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            File.WriteAllBytes(dialog.FileName, payload.Bytes);
            HideInlineError();
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"[DiagramEditor] Export failed: {ex.Message}");
            ShowInlineError(string.Format(L("ToolDiagramErrorExportFailed"), ex.Message));
        }
    }

    /// <summary>
    /// Builds the name proposed in a save dialog: the current document's name when
    /// it has one, otherwise the localized default.
    /// </summary>
    private string DefaultFileName(string extension)
    {
        var stem = _document.FilePath is { } path
            ? Path.GetFileNameWithoutExtension(path)
            : L("ToolDiagramDefaultFileName");

        return stem + extension;
    }

    /// <summary>
    /// Opens the file menu. Everything that acts on the document as a whole lives
    /// here rather than on the toolbar, which already scrolled at French width.
    /// </summary>
    private void OnFileMenuClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = BtnFile,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            StaysOpen = false
        };

        AddMenuItem(menu, L("ToolDiagramBtnNew"), (_, _) => NewDocument());
        AddMenuItem(menu, L("ToolDiagramBtnOpen"), (_, _) => OpenDocument());
        menu.Items.Add(BuildRecentMenu());
        menu.Items.Add(new Separator());
        AddMenuItem(menu, L("ToolDiagramBtnSave"), (_, _) => PerformSave(chooseNewLocation: false));
        AddMenuItem(menu, L("ToolDiagramBtnSaveAs"), (_, _) => PerformSave(chooseNewLocation: true));
        menu.Items.Add(new Separator());
        AddMenuItem(menu, L("ToolDiagramBtnMergeScan"), (_, _) => MergeScanIntoDocument());
        menu.Items.Add(BuildTemplateMenu());
        menu.Items.Add(new Separator());
        AddMenuItem(menu, L("ToolDiagramBtnExportPng"), (_, _) => PostWebMessage("export-png:"));
        AddMenuItem(menu, L("ToolDiagramBtnExportSvg"), (_, _) => PostWebMessage("export-svg:"));
        AddMenuItem(menu, L("ToolDiagramBtnPrint"), (_, _) => PostEditorCommand("print"));

        menu.IsOpen = true;
    }

    private static void AddMenuItem(ItemsControl menu, string header, RoutedEventHandler onClick)
    {
        var item = new MenuItem { Header = header };
        item.Click += onClick;
        menu.Items.Add(item);
    }

    private MenuItem BuildRecentMenu()
    {
        var recent = new MenuItem { Header = L("ToolDiagramBtnRecent") };
        var paths = _recentFiles.Read();

        if (paths.Count == 0)
        {
            recent.IsEnabled = false;
            return recent;
        }

        foreach (var path in paths)
        {
            var entry = new MenuItem { Header = Path.GetFileName(path), ToolTip = path };
            var captured = path;
            entry.Click += (_, _) => OpenDocument(captured);
            recent.Items.Add(entry);
        }

        return recent;
    }

    private MenuItem BuildTemplateMenu()
    {
        var templates = new MenuItem { Header = L("ToolDiagramBtnTemplates") };
        var available = DiagramTemplates.Available();

        if (available.Count == 0)
        {
            templates.IsEnabled = false;
            return templates;
        }

        foreach (var template in available)
        {
            var entry = new MenuItem { Header = L(template.NameKey) };
            var captured = template;
            entry.Click += (_, _) => ApplyTemplate(captured);
            templates.Items.Add(entry);
        }

        return templates;
    }

    private void ApplyTemplate(DiagramTemplate template)
    {
        if (!ConfirmDiscardUnsavedChanges()) return;

        var xml = template.TryRead();
        if (xml is null)
        {
            ShowInlineError(string.Format(L("ToolDiagramErrorOpenFailed"), template.FileName));
            return;
        }

        // A template is a starting point, not a document: it has no path, so the
        // first save asks where the user's own copy belongs.
        _document.OpenUnsaved(xml);
        UpdateDocumentIndicator();
        HideInlineError();
        PostWebMessage($"load:{xml}");
    }

    /// <summary>
    /// Merges a freshly exported scan into the diagram on screen, keeping the
    /// layout and every shape the user added.
    /// </summary>
    private void MergeScanIntoDocument()
    {
        if (_document.EditorXml is not { } current)
        {
            ShowInlineError(L("ToolDiagramErrorEditorNotReady"));
            return;
        }

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = L("FileDialogDrawioFilter")
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            var incoming = File.ReadAllText(dialog.FileName);
            var merged = DiagramScanMerge.Merge(current, incoming, out var summary);

            _document.EditorReported(merged);
            UpdateDocumentIndicator();
            PostWebMessage($"load:{merged}");
            ShowInlineStatus(DiagramScanMerge.Describe(summary, L));
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"[DiagramEditor] Merge failed: {ex.Message}");
            ShowInlineError(string.Format(L("ToolDiagramErrorMergeFailed"), ex.Message));
        }
    }

    /// <summary>
    /// Offers the draft a previous run left behind, which only exists when that
    /// run ended without saving.
    /// </summary>
    private void OfferDraftRecovery()
    {
        var draft = _drafts.TryRead(_document.FilePath);
        if (draft is null || string.Equals(draft, _document.EditorXml, StringComparison.Ordinal))
        {
            return;
        }

        var restore = Dialogs.MessageDialog.ShowConfirm(
            Window.GetWindow(this),
            L("ToolDiagramTitle"),
            L("ToolDiagramConfirmRestoreDraft"),
            "warning",
            L("ToolDiagramBtnRestoreDraft"),
            L("BtnDiscard"));

        if (restore)
        {
            _document.EditorReported(draft);
        }
        else
        {
            _drafts.Discard(_document.FilePath);
        }
    }

    private void NewDocument()
    {
        if (!ConfirmDiscardUnsavedChanges()) return;

        _drafts.Discard(_document.FilePath);
        _document.Reset();
        UpdateDocumentIndicator();
        HideInlineError();
        PostWebMessage("load:");
    }

    private void OpenDocument(string? path = null)
    {
        if (!ConfirmDiscardUnsavedChanges()) return;

        if (path is null)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = L("FileDialogDrawioFilter")
            };

            if (dialog.ShowDialog() != true) return;
            path = dialog.FileName;
        }

        if (!TryOpenFile(path)) return;

        // A draft left by a run that did not finish belongs to this document, and
        // this is where the user meets it. Offering it only when the tool starts
        // with a document never fired: the tool starts empty, and the file arrives
        // through here.
        OfferDraftRecovery();

        UpdateDocumentIndicator();
        PostWebMessage($"load:{_document.EditorXml}");
    }

    /// <summary>Reads <paramref name="path"/> into the document, reporting failure inline.</summary>
    private bool TryOpenFile(string path)
    {
        try
        {
            _document.OpenFromDisk(path, File.ReadAllText(path));
            _recentFiles.Remember(path);
            HideInlineError();
            return true;
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"[DiagramEditor] Open failed for '{path}': {ex.Message}");
            ShowInlineError(string.Format(L("ToolDiagramErrorOpenFailed"), ex.Message));
            return false;
        }
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        PerformSave(chooseNewLocation: false);
    }

    /// <summary>
    /// Runs one step of a save. When the editor has not reported any content yet,
    /// the content is requested and the save resumes on the reply.
    /// </summary>
    /// <returns>True when the document reached the disk.</returns>
    private bool PerformSave(bool chooseNewLocation)
    {
        switch (_document.NextSaveStep(chooseNewLocation))
        {
            case DiagramSaveStep.RequestContent:
                if (!_editorReady)
                {
                    ShowInlineError(L("ToolDiagramErrorEditorNotReady"));
                    return false;
                }

                _savePending = true;
                _savePendingChoosesLocation = chooseNewLocation;
                PostWebMessage("export-xml:");
                return false;

            case DiagramSaveStep.ChooseLocation:
                var dialog = new Microsoft.Win32.SaveFileDialog
                {
                    Filter = L("FileDialogDrawioSaveFilter"),
                    FileName = DefaultFileName(DiagramExtension)
                };

                if (dialog.ShowDialog() != true) return false;
                return WriteDocument(dialog.FileName);

            default:
                return WriteDocument(_document.FilePath!);
        }
    }

    private bool WriteDocument(string path)
    {
        var xml = _document.EditorXml;
        if (xml is null) return false;

        try
        {
            File.WriteAllText(path, xml, Encoding.UTF8);
            _document.MarkWritten(path, xml);
            _drafts.Discard(path);
            _drafts.Discard(null);
            _recentFiles.Remember(path);
            UpdateDocumentIndicator();
            HideInlineError();
            return true;
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"[DiagramEditor] Save failed for '{path}': {ex.Message}");
            ShowInlineError(string.Format(L("ToolDiagramErrorSaveFailed"), ex.Message));
            return false;
        }
    }

    /// <summary>
    /// Asks the user what to do with unsaved work before it is replaced or closed.
    /// </summary>
    /// <returns>True when the caller may proceed.</returns>
    private bool ConfirmDiscardUnsavedChanges()
    {
        if (!_document.IsDirty) return true;

        var decision = Dialogs.MessageDialog.ShowThreeWay(
            Window.GetWindow(this),
            L("ToolDiagramTitle"),
            L("ToolDiagramUnsavedMessage"),
            "warning",
            L("BtnSave"),
            L("BtnDiscard"),
            L("BtnCancel"));

        return decision switch
        {
            true => PerformSave(chooseNewLocation: false),
            false => true,
            _ => false
        };
    }

    /// <inheritdoc />
    public ToolCloseRefusal CloseRefusal { get; private set; } = ToolCloseRefusal.Busy;

    /// <inheritdoc />
    public bool CanClose()
    {
        bool allowed = ConfirmDiscardUnsavedChanges();

        // Set on every refusing call, never left over from an earlier one. The only
        // way this editor refuses is the user choosing Cancel at the prompt above,
        // and reporting that as a busy tool told them they were blocked by
        // something a moment after they were the something.
        CloseRefusal = allowed ? ToolCloseRefusal.Busy : ToolCloseRefusal.UserDeclined;
        return allowed;
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

    private void HandleLinkPayload(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.TryGetProperty("href", out var hrefProperty))
            {
                FollowLink(hrefProperty.GetString());
            }
        }
        catch (JsonException ex)
        {
            Core.Logging.FileLogger.Debug($"[DiagramEditor] Malformed link payload: {ex.Message}");
        }
    }

    /// <summary>
    /// Acts on a link a diagram cell carries: a Heimdall session, or a web page.
    /// </summary>
    /// <remarks>
    /// A .drawio file can come from anywhere, and a session link is an instruction
    /// to reach a machine and offer it credentials. The request is validated by
    /// <see cref="SessionLaunchRequest.TryParse"/> and then confirmed by the user,
    /// naming the protocol and the destination, because the document is not a
    /// party Heimdall trusts.
    /// </remarks>
    private void FollowLink(string? href)
    {
        if (!SessionLaunchRequest.TryParse(href, out var request))
        {
            OpenExternalLink(href);
            return;
        }

        if (_openSession is null)
        {
            Core.Logging.FileLogger.Warn(
                "[DiagramEditor] A diagram asked for a session but no session opener was provided.");
            ShowInlineError(L("ToolDiagramErrorSessionUnavailable"));
            return;
        }

        var confirmed = Dialogs.MessageDialog.ShowConfirm(
            Window.GetWindow(this),
            L("ToolDiagramTitle"),
            string.Format(
                L("ToolDiagramConfirmOpenSession"),
                request.Protocol,
                request.Destination),
            "info",
            L("ToolDiagramBtnConnect"),
            L("BtnCancel"));

        if (!confirmed) return;

        Core.Logging.FileLogger.Info(
            $"[DiagramEditor] Opening a {request.Protocol} session from a diagram node.");
        _openSession(request);
    }

    private static void OpenExternalLink(string? href)
    {
        // The href comes out of a diagram file, which may have been imported. One decision,
        // shared with the release launcher and the terminal: see Services.ExternalUrlPolicy.
        if (!Services.ExternalUrlPolicy.TryResolveLaunchable(href, out string? launchableUrl))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(launchableUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"[DiagramEditor] Could not open external link: {ex.Message}");
        }
    }

    /// <summary>Reflects the document's name and unsaved state in the header.</summary>
    private void UpdateDocumentIndicator()
    {
        var name = _document.FilePath is { } path
            ? Path.GetFileName(path)
            : L("ToolDiagramUntitled");

        TxtDocumentName.Text = _document.IsDirty
            ? string.Format(L("ToolDiagramDocumentModified"), name)
            : name;
    }

    private void ShowInlineError(string message)
    {
        TxtError.Text = message;
        TxtError.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Reports an outcome that is not a failure on the same line, because a merge
    /// that says nothing is indistinguishable from one that did nothing.
    /// </summary>
    private void ShowInlineStatus(string message)
    {
        TxtError.Text = message;
        TxtError.Visibility = Visibility.Visible;
    }

    private void HideInlineError()
    {
        TxtError.Text = string.Empty;
        TxtError.Visibility = Visibility.Collapsed;
    }

    private void ApplyLocalization()
    {
        HeaderTitle.Text = L("ToolDiagramTitle");
        BtnFile.Content = L("ToolDiagramBtnFile");
        BtnSave.Content = L("ToolDiagramBtnSave");
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
        System.Windows.Automation.AutomationProperties.SetName(BtnFile, L("ToolDiagramBtnFile"));
        System.Windows.Automation.AutomationProperties.SetName(BtnCloseHelp, L("BtnClose"));
        System.Windows.Automation.AutomationProperties.SetName(BtnSave, L("ToolDiagramBtnSave"));
        System.Windows.Automation.AutomationProperties.SetName(BtnInsertLine, L("ToolDiagramBtnInsertLine"));
        System.Windows.Automation.AutomationProperties.SetName(BtnFormat, L("ToolDiagramBtnFormat"));
        System.Windows.Automation.AutomationProperties.SetName(BtnUndo, L("BtnUndo"));
        System.Windows.Automation.AutomationProperties.SetName(BtnRedo, L("ToolDiagramBtnRedo"));
        System.Windows.Automation.AutomationProperties.SetName(BtnZoomOut, L("ToolDiagramBtnZoomOut"));
        System.Windows.Automation.AutomationProperties.SetName(BtnActualSize, L("ToolDiagramBtnActualSize"));
        System.Windows.Automation.AutomationProperties.SetName(BtnZoomIn, L("ToolDiagramBtnZoomIn"));
        System.Windows.Automation.AutomationProperties.SetName(BtnDuplicate, L("BtnDuplicate"));
        System.Windows.Automation.AutomationProperties.SetName(BtnDelete, L("BtnDelete"));
        System.Windows.Automation.AutomationProperties.SetName(TxtDocumentName, L("ToolDiagramDocumentNameLabel"));

        BtnFile.ToolTip = L("ToolDiagramBtnFile");
        BtnSave.ToolTip = L("ToolDiagramBtnSave");
        BtnInsertLine.ToolTip = L("ToolDiagramBtnInsertLine");
        BtnFormat.ToolTip = L("ToolDiagramBtnFormat");
        BtnUndo.ToolTip = L("BtnUndo");
        BtnRedo.ToolTip = L("ToolDiagramBtnRedo");
        BtnZoomOut.ToolTip = L("ToolDiagramBtnZoomOut");
        BtnActualSize.ToolTip = L("ToolDiagramBtnActualSize");
        BtnZoomIn.ToolTip = L("ToolDiagramBtnZoomIn");
        BtnDuplicate.ToolTip = L("BtnDuplicate");
        BtnDelete.ToolTip = L("BtnDelete");

        SetToolbarEnabled(false);
        UpdateDocumentIndicator();
    }

    private void SetToolbarEnabled(bool enabled)
    {
        BtnFile.IsEnabled = enabled;
        BtnSave.IsEnabled = enabled;
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
                DiagramWebView.CoreWebView2.NavigationStarting -= OnNavigationStarting;
                DiagramWebView.CoreWebView2.FrameNavigationStarting -= OnFrameNavigationStarting;
                DiagramWebView.CoreWebView2.NewWindowRequested -= OnNewWindowRequested;
            }
            DiagramWebView.Dispose();
        }
        catch (Exception ex)
        {
            // Disposal may fail if WebView2 was never initialized.
            Core.Logging.FileLogger.Debug($"[DiagramEditor] Dispose: {ex.Message}");
        }

        GC.SuppressFinalize(this);
    }
}
