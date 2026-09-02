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
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Heimdall.App.Services;
using Heimdall.App.ViewModels.Tools;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Logging;
using Heimdall.Core.Models;
using ICSharpCode.AvalonEdit.CodeCompletion;
using Microsoft.Extensions.DependencyInjection;

namespace Heimdall.App.Views.Tools;

public partial class NotesToolView : UserControl, IToolView
{
    private const double NotesSidebarCollapseWidth = 760;
    private readonly record struct NotesLaunchRequest(string? PreferredPath, NoteTemplateKind? TemplateKind);
    private const string MarkdownLinkSuffix = "](url)";
    private const string MarkdownLinkTemplate = "[text](url)";
    private const string MarkdownImageTemplate = "![alt](url)";
    private const string MarkdownTableTemplate = "| H1 | H2 | H3 |\n|---|---|---|\n| a | b | c |\n";

    private readonly NotesToolViewModel _vm;
    private readonly DispatcherTimer _saveTimer;

    private CompletionWindow? _completionWindow;
    private LocalizationManager? _localizer;
    private bool _useMilkdown;
    private ToolContext? _context;
    private bool _disposed;
    private bool _initializeRequested;
    private bool _initializeStarted;
    private bool _isLoadingNote;
    private bool _suppressSelectionChanged = false;
    private bool _sidebarVisible = true;
    private bool _responsiveSidebarHidden;
    private string? _lastDragOverDiagnostic;
    private bool _vmOutputHandlersAttached;
    private GridLength _savedSidebarWidth = new(LoadSidebarWidth());

    public NotesToolView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnViewSizeChanged;

        _vm = ResolveViewModel();
        _vm.PropertyChanged += OnViewModelPropertyChanged;
        DataContext = _vm;

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(850) };
        _saveTimer.Tick += OnSaveTimerTick;

        Editor.TextChanged += OnEditorTextChanged;
        Editor.TextArea.TextEntered += OnTextAreaTextEntered;
        Editor.TextArea.PreviewMouseDown += OnEditorPreviewMouseDown;
        Editor.IsReadOnly = true;
        Editor.Options.ConvertTabsToSpaces = false;
        Editor.Options.HighlightCurrentLine = true;
        Editor.Options.EnableHyperlinks = false;
        Editor.ContextMenu = BuildEditorContextMenu();

        try
        {
            Editor.SyntaxHighlighting = MarkdownHighlighting.Create();
            Editor.TextArea.TextView.LineTransformers.Add(new MarkdownLivePreviewTransformer());
        }
        catch { /* Highlighting is cosmetic - do not block initialization */ }
    }

    public void Initialize(ToolContext? context, LocalizationManager? localizer)
    {
        _context = context;
        _localizer = localizer;
        _vm.SetContext(context);
        _initializeRequested = true;
        StartInitializationIfNeeded();
    }

    public bool CanClose()
    {
        _saveTimer.Stop();
        return _vm.TrySaveSynchronously();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AttachVmOutputHandlers();
        StartInitializationIfNeeded(fromLoadedEvent: true);
        Dispatcher.BeginInvoke(UpdateResponsiveLayout, DispatcherPriority.Loaded);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        DetachVmOutputHandlers();
    }

    private async void StartInitializationIfNeeded(bool fromLoadedEvent = false)
    {
        if (!_initializeRequested || _initializeStarted || _disposed)
        {
            return;
        }

        // IsLoaded can sometimes be false inside the Loaded event handler depending on TabControl virtualization.
        if (!fromLoadedEvent && !IsLoaded)
        {
            return;
        }

        _initializeStarted = true;
        await InitializeAsync(_context).ConfigureAwait(true);
    }

    private async Task InitializeAsync(ToolContext? context)
    {
        try
        {
            Core.Logging.FileLogger.Info(
                $"NotesToolView init start: Loaded={IsLoaded}, " +
                $"MilkdownControlLoaded={MilkdownEditor.IsLoaded}, " +
                $"MilkdownAsset={MilkdownEditorControl.IsAvailable}, " +
                $"WebView2={Services.WebView2Helper.IsAvailable}");

            // Apply persisted sidebar width
            if (SidebarBorder.Parent is Grid layoutGrid)
            {
                layoutGrid.ColumnDefinitions[0].Width = _savedSidebarWidth;
            }
            UpdateResponsiveLayout();

            await _vm.InitializeAsync().ConfigureAwait(true);
            UpdateSelectionState();

            await TryInitializeMilkdownAsync().ConfigureAwait(true);

            var launchRequest = ResolveLaunchRequest(context);
            var requestedPath = launchRequest.PreferredPath;

            if (launchRequest.TemplateKind is { } templateKind)
            {
                await _vm.NewNoteFromTemplateCommand.ExecuteAsync(templateKind.ToString());
                requestedPath = _vm.CurrentNotePath;
            }
            else
            {
                await _vm.ReloadAsync(requestedPath).ConfigureAwait(true);
            }

            Core.Logging.FileLogger.Info(
                $"NotesToolView init complete: UseMilkdown={_useMilkdown}, CurrentNote={_vm.CurrentNotePath ?? "<none>"}");
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Error("NotesToolView init failed", ex);
            _useMilkdown = false;
            UpdateSelectionState();
            _vm.SetStatus(string.Format(L("ToolNotesStatusError"), ex.Message), isError: true);
        }
    }

    private async Task TryInitializeMilkdownAsync()
    {
        Core.Logging.FileLogger.Info(
            $"Milkdown check: IsAvailable={MilkdownEditorControl.IsAvailable}, " +
            $"WebView2={Services.WebView2Helper.IsAvailable}");

        if (!MilkdownEditorControl.IsAvailable)
        {
            Core.Logging.FileLogger.Info("Milkdown not available, using AvalonEdit fallback");
            return;
        }

        try
        {
            MilkdownEditor.ContentChanged += OnMilkdownContentChanged;
            MilkdownEditor.LinkClicked += OnMilkdownLinkClicked;
            MilkdownEditor.EditorReady += OnMilkdownReady;

            // Keep the control in the visual tree during startup without exposing
            // it until the WebView2 host is fully initialized.
            MilkdownEditor.Visibility = Visibility.Hidden;
            await MilkdownEditor.InitializeAsync().ConfigureAwait(true);

            if (!MilkdownEditor.IsHostInitialized)
            {
                Core.Logging.FileLogger.Info("Milkdown init returned without error but WebView2 host not created, using AvalonEdit fallback");
                MilkdownEditor.Visibility = Visibility.Collapsed;
                Editor.Visibility = Visibility.Visible;
                return;
            }

            _useMilkdown = true;
            UpdateSelectionState();
            Core.Logging.FileLogger.Info("Milkdown editor initialized successfully");
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"Milkdown init failed, fallback to AvalonEdit: {ex.Message}");
            _useMilkdown = false;
            MilkdownEditor.Visibility = Visibility.Collapsed;
            Editor.Visibility = Visibility.Visible;
        }
    }

    private void OnMilkdownReady()
    {
        SendContextMenuLabels();
        _vm.SetStatus(L("ToolNotesStatusReady"));
    }

    private void SendContextMenuLabels()
    {
        var labels = new Dictionary<string, string>
        {
            ["bold"] = L("ToolNotesCtxBold"),
            ["italic"] = L("ToolNotesCtxItalic"),
            ["strikethrough"] = L("ToolNotesCtxStrikethrough"),
            ["inlineCode"] = L("ToolNotesCtxInlineCode"),
            ["codeBlock"] = L("ToolNotesCtxCodeBlock"),
            ["link"] = L("ToolNotesCtxLink"),
            ["image"] = L("ToolNotesCtxImage"),
            ["noteLink"] = L("ToolNotesCtxNoteLink"),
            ["heading1"] = L("ToolNotesCtxHeading1"),
            ["heading2"] = L("ToolNotesCtxHeading2"),
            ["heading3"] = L("ToolNotesCtxHeading3"),
            ["bulletList"] = L("ToolNotesCtxBulletList"),
            ["numberedList"] = L("ToolNotesCtxNumberedList"),
            ["taskList"] = L("ToolNotesCtxTaskList"),
            ["blockquote"] = L("ToolNotesCtxBlockquote"),
            ["table"] = L("ToolNotesCtxTable"),
            ["horizontalRule"] = L("ToolNotesCtxHorizontalRule"),
        };
        MilkdownEditor.SetContextMenuLabels(labels);
    }

    private void OnMilkdownContentChanged(string markdown, bool dirty)
    {
        _lastMilkdownContent = markdown;

        if (_isLoadingNote || _vm.CurrentNotePath is null)
        {
            return;
        }

        if (dirty)
        {
            _vm.CurrentMarkdown = markdown;
            _vm.IsDirty = true;
            _vm.SetStatus(L("ToolNotesStatusModified"));
            _saveTimer.Stop();
            _saveTimer.Start();
        }
    }

    private async void OnMilkdownLinkClicked(string noteReference)
    {
        try
        {
            await _vm.OpenLinkedNoteAsync(noteReference).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _vm.SetStatus(string.Format(L("ToolNotesStatusError"), ex.Message), isError: true);
        }
    }

    private async Task DeleteCurrentNoteAsync()
    {
        if (_vm.CurrentNotePath is null)
        {
            return;
        }

        var dialogService = (Application.Current as App)?.Services
            ?.GetService(typeof(IDialogService)) as IDialogService;
        if (dialogService is null)
        {
            return;
        }

        var confirmed = await dialogService.ShowConfirmAsync(
            L("ConfirmTitle"),
            string.Format(L("ToolNotesDeleteConfirm"), SelectedNoteTitleText.Text),
            "warning");
        if (!confirmed)
        {
            return;
        }

        try
        {
            await _vm.DeleteCurrentNoteCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            _vm.SetStatus(string.Format(L("ToolNotesStatusError"), ex.Message), isError: true);
        }
    }

    private async Task RenameCurrentNoteAsync()
    {
        if (_vm.CurrentNotePath is null)
        {
            return;
        }

        await FlushPendingChangesAsync().ConfigureAwait(true);

        var currentName = ExtractHeaderTitle(GetCurrentEditorContent(), _vm.CurrentNotePath);
        var dialog = new Views.Dialogs.InputDialog(_localizer)
        {
            Title = L("ToolNotesRenameTitle"),
            Prompt = L("ToolNotesRenamePrompt"),
            InputText = currentName,
            Owner = Window.GetWindow(this)
        };

        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.InputText))
        {
            return;
        }

        try
        {
            await _vm.RenameCurrentNoteCommand.ExecuteAsync(dialog.InputText);
        }
        catch (Exception ex)
        {
            _vm.SetStatus(string.Format(L("ToolNotesStatusError"), ex.Message), isError: true);
        }
    }

    private async Task DuplicateCurrentNoteAsync()
    {
        if (_vm.CurrentNotePath is null)
        {
            return;
        }

        await FlushPendingChangesAsync().ConfigureAwait(true);

        try
        {
            await _vm.DuplicateCurrentNoteCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            _vm.SetStatus(string.Format(L("ToolNotesStatusError"), ex.Message), isError: true);
        }
    }

    private async Task FlushPendingChangesAsync()
    {
        _saveTimer.Stop();
        _vm.CurrentMarkdown = _useMilkdown ? await GetMilkdownContentAsync().ConfigureAwait(true) : Editor.Text;
        await _vm.SaveCommand.ExecuteAsync(null);
    }

    private async void OnEditorPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left
            || !Keyboard.Modifiers.HasFlag(ModifierKeys.Control)
            || _vm.CurrentNotePath is null)
        {
            return;
        }

        var position = Editor.GetPositionFromPoint(e.GetPosition(Editor));
        if (position is null)
        {
            return;
        }

        var offset = Editor.Document.GetOffset(position.Value.Location);
        var line = Editor.Document.GetLineByOffset(offset);
        var lineText = Editor.Document.GetText(line);
        var col = offset - line.Offset;

        foreach (System.Text.RegularExpressions.Match match in
            SimpleMarkdownConverter.NoteLinkRegex().Matches(lineText))
        {
            if (col >= match.Index && col < match.Index + match.Length)
            {
                var noteRef = match.Groups[1].Value.Trim();
                e.Handled = true;
                try
                {
                    await _vm.OpenLinkedNoteAsync(noteRef).ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    _vm.SetStatus(string.Format(L("ToolNotesStatusError"), ex.Message), isError: true);
                }
                return;
            }
        }
    }

    private string _lastMilkdownContent = string.Empty;

    private Task<string> GetMilkdownContentAsync()
    {
        // Content is synced via ContentChanged events; use the last known value
        return Task.FromResult(_lastMilkdownContent);
    }

    private void UpdateSelectionState()
    {
        var hasSelection = _vm.HasSelection;

        if (_useMilkdown)
        {
            Editor.Visibility = Visibility.Collapsed;
            MilkdownEditor.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
            MilkdownEditor.SetReadOnly(!hasSelection);
        }
        else
        {
            MilkdownEditor.Visibility = Visibility.Collapsed;
            Editor.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
            Editor.IsReadOnly = !hasSelection;
        }
    }

    private string GetCurrentEditorContent()
        => _useMilkdown ? _lastMilkdownContent : Editor.Text;

    private static string ExtractHeaderTitle(string markdown, string filePath)
    {
        foreach (var rawLine in (markdown ?? string.Empty).Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                return line[2..].Trim();
            }
        }

        return Path.GetFileNameWithoutExtension(filePath);
    }

    private void SetStatus(string message, bool isError = false)
        => _vm.SetStatus(message, isError);

    private NotesLaunchRequest ResolveLaunchRequest(ToolContext? context)
    {
        if (string.IsNullOrWhiteSpace(context?.Argument))
        {
            return new NotesLaunchRequest(null, null);
        }

        var argument = context.Argument.Trim();
        if (TryParseTemplateArgument(argument, out var templateKind))
        {
            return new NotesLaunchRequest(null, templateKind);
        }

        if (argument.StartsWith("template:", StringComparison.OrdinalIgnoreCase))
        {
            return new NotesLaunchRequest(null, null);
        }

        var resolved = Path.IsPathRooted(argument)
            ? argument
            : Path.Combine(_vm.NotesRootPath, argument);

        return _vm.IsUnderNotesRoot(resolved)
            ? new NotesLaunchRequest(resolved, null)
            : new NotesLaunchRequest(null, null);
    }

    private static bool TryParseTemplateArgument(string argument, out NoteTemplateKind templateKind)
    {
        const string prefix = "template:";

        templateKind = default;
        if (!argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return Enum.TryParse(argument[prefix.Length..], ignoreCase: true, out templateKind);
    }

    private static AppSettings? ResolveAppSettings()
        => (Application.Current as App)?.Services?.GetService<AppSettings>();

    private static int LoadSidebarWidth()
    {
        var width = ResolveAppSettings()?.NotesSidebarWidth;
        if (width is >= 240)
        {
            return width.Value;
        }

        return 300;
    }

    private static void PersistSidebarWidth(int width)
    {
        try
        {
            var configManager = (Application.Current as App)?
                .Services?.GetService<IConfigManager>();
            if (configManager is null)
            {
                return;
            }

            _ = configManager.MergeSettingAsync(s => s.NotesSidebarWidth = width);
        }
        catch
        {
            // Non-critical UI preference - swallow errors
        }
    }

    private static void OpenFolder(string path)
    {
        if (!Directory.Exists(path) && !File.Exists(path))
        {
            Directory.CreateDirectory(path);
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        });
    }

    private string L(string key) => _localizer?[key] ?? key;

    private async Task CreateNoteAsync(NoteTemplateKind templateKind, NoteTreeNode? contextNode = null)
    {
        try
        {
            await FlushPendingChangesAsync().ConfigureAwait(true);
            await _vm.NewNoteFromTemplateCommand.ExecuteAsync(templateKind.ToString()).ConfigureAwait(true);

            var targetFolder = contextNode?.FolderPath
                ?? (contextNode?.FilePath is not null ? Path.GetDirectoryName(contextNode.FilePath) : null);

            if (!string.IsNullOrWhiteSpace(targetFolder)
                && _vm.CurrentNotePath is not null
                && !string.Equals(targetFolder, Path.GetDirectoryName(_vm.CurrentNotePath), StringComparison.OrdinalIgnoreCase))
            {
                await _vm.MoveNoteToFolderAsync(_vm.CurrentNotePath, targetFolder).ConfigureAwait(true);
            }

            EnsureNoteHostFocused();
        }
        catch (Exception ex)
        {
            _vm.SetStatus(string.Format(L("ToolNotesStatusError"), ex.Message), isError: true);
        }
    }

    private void OnNewNoteClick(object sender, RoutedEventArgs e)
    {
        var currentContext = NotesTreeView.SelectedItem as NoteTreeNode;

        var menu = new ContextMenu
        {
            PlacementTarget = BtnNewNote,
            Placement = PlacementMode.Bottom,
            StaysOpen = false
        };

        var newBlank = new MenuItem { Header = L("ToolNotesBtnNew") };
        newBlank.Click += async (_, _) => await CreateNoteAsync(NoteTemplateKind.Blank, currentContext).ConfigureAwait(true);
        menu.Items.Add(newBlank);

        var newDaily = new MenuItem { Header = L("ToolNotesBtnDaily") };
        newDaily.Click += async (_, _) => await CreateNoteAsync(NoteTemplateKind.Daily, currentContext).ConfigureAwait(true);
        menu.Items.Add(newDaily);

        var newIncident = new MenuItem { Header = L("ToolNotesBtnIncident") };
        newIncident.Click += async (_, _) => await CreateNoteAsync(NoteTemplateKind.Incident, currentContext).ConfigureAwait(true);
        menu.Items.Add(newIncident);

        var newProcedure = new MenuItem { Header = L("ToolNotesBtnProcedure") };
        newProcedure.Click += async (_, _) => await CreateNoteAsync(NoteTemplateKind.Procedure, currentContext).ConfigureAwait(true);
        menu.Items.Add(newProcedure);

        menu.Items.Add(new Separator());

        var newFolder = new MenuItem { Header = L("ToolNotesNewFolder") };
        newFolder.Click += (_, _) => CreateFolderFromContextMenu(null);
        menu.Items.Add(newFolder);

        BtnNewNote.ContextMenu = menu;
        menu.IsOpen = true;
    }

    private void OnNoteActionsClick(object sender, RoutedEventArgs e)
    {
        if (_vm.CurrentNotePath is null)
        {
            return;
        }

        var menu = new ContextMenu
        {
            PlacementTarget = BtnNoteActions,
            Placement = PlacementMode.Bottom,
            StaysOpen = false
        };

        var rename = new MenuItem { Header = L("ToolNotesBtnRename") };
        rename.Click += async (_, _) => await RenameCurrentNoteAsync().ConfigureAwait(true);
        menu.Items.Add(rename);

        var duplicate = new MenuItem { Header = L("ToolNotesBtnDuplicate") };
        duplicate.Click += async (_, _) => await DuplicateCurrentNoteAsync().ConfigureAwait(true);
        menu.Items.Add(duplicate);

        var openFolder = new MenuItem { Header = L("ToolNotesBtnOpenFolder") };
        openFolder.Click += (_, _) => OpenFolder(Path.GetDirectoryName(_vm.CurrentNotePath) ?? _vm.NotesRootPath);
        menu.Items.Add(openFolder);

        menu.Items.Add(new Separator());

        var delete = new MenuItem
        {
            Header = L("ToolNotesBtnDelete")
        };
        if (Application.Current.TryFindResource("ErrorBrush") is System.Windows.Media.Brush deleteBrush)
        {
            delete.Foreground = deleteBrush;
        }
        delete.Click += async (_, _) => await DeleteCurrentNoteAsync().ConfigureAwait(true);
        menu.Items.Add(delete);

        menu.IsOpen = true;
    }

    private async void OnNotesTreeSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_suppressSelectionChanged)
        {
            return;
        }

        if (e.NewValue is NoteTreeNode { FilePath: not null } node)
        {
            await _vm.OpenNoteCommand.ExecuteAsync(node.FilePath).ConfigureAwait(true);
        }
    }

    private void OnTreeViewPreviewRightClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // Prevent the session tab context menu from intercepting the right-click
        e.Handled = true;

        // Select the clicked tree item
        if (e.OriginalSource is DependencyObject source)
        {
            var treeViewItem = FindParent<TreeViewItem>(source);
            if (treeViewItem is not null)
            {
                treeViewItem.IsSelected = true;
            }
        }

        // Rebuild and show our context menu
        OnTreeViewContextMenuOpening(sender, null!);
        TreeContextMenu.IsOpen = true;
    }

    private async void OnTreeViewPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None ||
            NotesTreeView.SelectedItem is not NoteTreeNode { FilePath: not null })
        {
            return;
        }

        switch (e.Key)
        {
            case Key.F2:
                e.Handled = true;
                await RenameCurrentNoteAsync().ConfigureAwait(true);
                break;

            case Key.Delete:
                e.Handled = true;
                await DeleteCurrentNoteAsync().ConfigureAwait(true);
                break;
        }
    }

    private static T? FindParent<T>(DependencyObject? child) where T : DependencyObject
    {
        var current = GetParentObject(child);
        while (current is not null)
        {
            if (current is T target) return target;
            current = GetParentObject(current);
        }
        return null;
    }

    private static TreeViewItem? FindParentTreeViewItem(DependencyObject? current)
    {
        while (current is not null)
        {
            if (current is TreeViewItem item)
            {
                return item;
            }

            current = GetParentObject(current);
        }

        return null;
    }

    private static DependencyObject? GetParentObject(DependencyObject? current)
    {
        if (current is null)
        {
            return null;
        }

        if (current is FrameworkContentElement frameworkContentElement)
        {
            return frameworkContentElement.Parent;
        }

        if (current is ContentElement contentElement)
        {
            return ContentOperations.GetParent(contentElement);
        }

        if (current is Visual || current is System.Windows.Media.Media3D.Visual3D)
        {
            return VisualTreeHelper.GetParent(current);
        }

        return LogicalTreeHelper.GetParent(current);
    }

    private void OnTreeViewContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        TreeContextMenu.Items.Clear();

        var treeItem = NotesTreeView.SelectedItem as NoteTreeNode;

        // ── Create ──
        var newBlank = new MenuItem { Header = L("ToolNotesBtnNew") };
        newBlank.Click += async (_, _) => await CreateNoteAsync(NoteTemplateKind.Blank, treeItem).ConfigureAwait(true);
        TreeContextMenu.Items.Add(newBlank);

        var newDaily = new MenuItem { Header = L("ToolNotesBtnDaily") };
        newDaily.Click += async (_, _) => await CreateNoteAsync(NoteTemplateKind.Daily, treeItem).ConfigureAwait(true);
        TreeContextMenu.Items.Add(newDaily);

        var newIncident = new MenuItem { Header = L("ToolNotesBtnIncident") };
        newIncident.Click += async (_, _) => await CreateNoteAsync(NoteTemplateKind.Incident, treeItem).ConfigureAwait(true);
        TreeContextMenu.Items.Add(newIncident);

        var newProcedure = new MenuItem { Header = L("ToolNotesBtnProcedure") };
        newProcedure.Click += async (_, _) => await CreateNoteAsync(NoteTemplateKind.Procedure, treeItem).ConfigureAwait(true);
        TreeContextMenu.Items.Add(newProcedure);

        TreeContextMenu.Items.Add(new Separator());

        // ── New Folder ──
        var newFolder = new MenuItem { Header = L("ToolNotesNewFolder") };
        newFolder.Click += (_, _) => CreateFolderFromContextMenu(treeItem);
        TreeContextMenu.Items.Add(newFolder);

        // ── File actions ──
        if (treeItem is { FilePath: not null })
        {
            TreeContextMenu.Items.Add(new Separator());

            var rename = new MenuItem { Header = L("ToolNotesBtnRename") };
            rename.Click += async (_, _) => await RenameCurrentNoteAsync().ConfigureAwait(true);
            TreeContextMenu.Items.Add(rename);

            var duplicate = new MenuItem { Header = L("ToolNotesBtnDuplicate") };
            duplicate.Click += async (_, _) => await DuplicateCurrentNoteAsync().ConfigureAwait(true);
            TreeContextMenu.Items.Add(duplicate);

            TreeContextMenu.Items.Add(new Separator());

            var openFolder = new MenuItem { Header = L("ToolNotesBtnOpenFolder") };
            openFolder.Click += (_, _) => OpenFolder(
                Path.GetDirectoryName(treeItem.FilePath) ?? _vm.NotesRootPath);
            TreeContextMenu.Items.Add(openFolder);

            TreeContextMenu.Items.Add(new Separator());

            var delete = new MenuItem
            {
                Header = L("ToolNotesBtnDelete")
            };
            if (Application.Current.TryFindResource("ErrorBrush") is System.Windows.Media.Brush treeDeleteBrush)
            {
                delete.Foreground = treeDeleteBrush;
            }
            delete.Click += async (_, _) => await DeleteCurrentNoteAsync().ConfigureAwait(true);
            TreeContextMenu.Items.Add(delete);
        }

        // ── Folder actions ──
        if (treeItem is { IsFolder: true, FolderPath: not null })
        {
            TreeContextMenu.Items.Add(new Separator());

            var openInExplorer = new MenuItem { Header = L("ToolNotesBtnOpenFolder") };
            openInExplorer.Click += (_, _) => OpenFolder(treeItem.FolderPath);
            TreeContextMenu.Items.Add(openInExplorer);
        }
    }

    private async void CreateFolderFromContextMenu(NoteTreeNode? contextNode)
    {
        var parentFolder = contextNode?.FolderPath
            ?? (contextNode?.FilePath is not null ? Path.GetDirectoryName(contextNode.FilePath) : null)
            ?? _vm.NotesRootPath;

        var dialog = new Views.Dialogs.InputDialog(_localizer)
        {
            Title = L("ToolNotesNewFolder"),
            Prompt = L("ToolNotesNewFolderPrompt"),
            Owner = Window.GetWindow(this)
        };

        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.InputText))
        {
            return;
        }

        try
        {
            await _vm.CreateFolderAsync(dialog.InputText, parentFolder).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _vm.SetStatus(string.Format(L("ToolNotesStatusError"), ex.Message), isError: true);
        }
    }

    // ── TreeView internal drag & drop ─────────────────────────────

    private System.Windows.Point _dragStartPoint;
    private bool _isDragging;

    private void OnTreeViewPreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(NotesTreeView);

        if (e.OriginalSource is DependencyObject source)
        {
            var treeViewItem = FindParent<TreeViewItem>(source);
            if (treeViewItem is not null)
            {
                treeViewItem.IsSelected = true;
            }
        }

        FileLogger.Debug($"[NotesTool][DragDrop] mouse-down selected={DescribeNode(NotesTreeView.SelectedItem as NoteTreeNode)}");
    }

    private void OnTreeViewPreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed || _isDragging)
        {
            return;
        }

        var currentPos = e.GetPosition(NotesTreeView);
        if (Math.Abs(currentPos.X - _dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(currentPos.Y - _dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var treeViewItem = FindParent<TreeViewItem>(e.OriginalSource as DependencyObject);
        if (treeViewItem?.DataContext is not NoteTreeNode { FilePath: not null } sourceNode)
        {
            return;
        }

        _isDragging = true;
        try
        {
            _lastDragOverDiagnostic = null;
            var data = new System.Windows.DataObject("NoteTreeNode", sourceNode);
            FileLogger.Info($"[NotesTool][DragDrop] start source={DescribeNode(sourceNode)}");
            var result = DragDrop.DoDragDrop(treeViewItem, data, System.Windows.DragDropEffects.Move);
            FileLogger.Info($"[NotesTool][DragDrop] complete source={DescribeNode(sourceNode)} result={result}");
        }
        finally
        {
            _isDragging = false;
        }
    }

    private void OnTreeViewInternalDragOver(object sender, System.Windows.DragEventArgs e)
    {
        e.Effects = System.Windows.DragDropEffects.None;

        var sourceNode = e.Data.GetDataPresent("NoteTreeNode")
            ? e.Data.GetData("NoteTreeNode") as NoteTreeNode
            : null;
        var targetFolderPath = GetDropTargetFolderPath(e);

        if (sourceNode is null)
        {
            LogDragOverOnce($"no-source target={DescribeFolderPath(targetFolderPath)}");
            return;
        }

        if (targetFolderPath is not null
            && !string.Equals(Path.GetDirectoryName(sourceNode.FilePath), targetFolderPath, StringComparison.OrdinalIgnoreCase))
        {
            e.Effects = System.Windows.DragDropEffects.Move;
        }

        LogDragOverOnce($"source={DescribeNode(sourceNode)} target={DescribeFolderPath(targetFolderPath)} effect={e.Effects}");
        e.Handled = true;
    }

    private async void OnTreeViewInternalDrop(object sender, System.Windows.DragEventArgs e)
    {
        if (!e.Data.GetDataPresent("NoteTreeNode"))
        {
            FileLogger.Warn("[NotesTool][DragDrop] drop rejected: missing NoteTreeNode payload");
            return;
        }

        var sourceNode = e.Data.GetData("NoteTreeNode") as NoteTreeNode;
        var targetFolderPath = GetDropTargetFolderPath(e);

        if (sourceNode?.FilePath is null || targetFolderPath is null)
        {
            FileLogger.Warn($"[NotesTool][DragDrop] drop rejected: source={DescribeNode(sourceNode)} target={DescribeFolderPath(targetFolderPath)}");
            return;
        }

        if (string.Equals(
            Path.GetDirectoryName(sourceNode.FilePath),
            targetFolderPath,
            StringComparison.OrdinalIgnoreCase))
        {
            FileLogger.Info($"[NotesTool][DragDrop] drop skipped: source already in target folder source={DescribeNode(sourceNode)} target={DescribeFolderPath(targetFolderPath)}");
            return;
        }

        try
        {
            FileLogger.Info($"[NotesTool][DragDrop] move source={DescribeNode(sourceNode)} target={DescribeFolderPath(targetFolderPath)}");
            var newPath = await _vm.MoveNoteToFolderAsync(sourceNode.FilePath, targetFolderPath)
                .ConfigureAwait(true);
            SetStatus(string.Format(L("ToolNotesStatusMoved"), Path.GetFileName(newPath)));
            FileLogger.Info($"[NotesTool][DragDrop] move completed newPath={newPath}");
        }
        catch (Exception ex)
        {
            _vm.SetStatus(string.Format(L("ToolNotesStatusError"), ex.Message), isError: true);
            FileLogger.Error($"[NotesTool][DragDrop] move failed source={DescribeNode(sourceNode)} target={DescribeFolderPath(targetFolderPath)}", ex);
        }

        e.Handled = true;
    }

    private string GetDropTargetFolderPath(System.Windows.DragEventArgs e)
    {
        var point = e.GetPosition(NotesTreeView);
        if (NotesTreeView.InputHitTest(point) is DependencyObject hit)
        {
            var hitTreeViewItem = FindParentTreeViewItem(hit);
            var hitTarget = TryResolveDropTarget(hitTreeViewItem?.DataContext as NoteTreeNode);
            if (hitTarget is not null)
            {
                return hitTarget;
            }
        }

        if (e.OriginalSource is DependencyObject source)
        {
            var treeViewItem = FindParentTreeViewItem(source);
            var originalTarget = TryResolveDropTarget(treeViewItem?.DataContext as NoteTreeNode);
            if (originalTarget is not null)
            {
                return originalTarget;
            }
        }

        return _vm.NotesRootPath;
    }

    private string? TryResolveDropTarget(NoteTreeNode? node)
    {
        if (node is null)
        {
            return null;
        }

        if (node.IsFolder)
        {
            return node.FolderPath;
        }

        return node.FilePath is null
            ? null
            : Path.GetDirectoryName(node.FilePath) ?? _vm.NotesRootPath;
    }

    private void LogDragOverOnce(string message)
    {
        if (string.Equals(_lastDragOverDiagnostic, message, StringComparison.Ordinal))
        {
            return;
        }

        _lastDragOverDiagnostic = message;
        FileLogger.Debug($"[NotesTool][DragDrop] over {message}");
    }

    private static string DescribeNode(NoteTreeNode? node)
    {
        if (node is null)
        {
            return "<null>";
        }

        if (node.IsFolder)
        {
            return $"folder:{node.FolderPath ?? node.Name}";
        }

        return $"note:{node.FilePath}";
    }

    private string DescribeFolderPath(string? folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return "<null>";
        }

        return string.Equals(folderPath, _vm.NotesRootPath, StringComparison.OrdinalIgnoreCase)
            ? $"root:{folderPath}"
            : $"folder:{folderPath}";
    }

    private void OnEditorTextChanged(object? sender, EventArgs e)
    {
        if (_isLoadingNote || _vm.CurrentNotePath is null)
        {
            return;
        }

        _vm.CurrentMarkdown = Editor.Text ?? string.Empty;
        _vm.IsDirty = true;
        _vm.SetStatus(L("ToolNotesStatusModified"));

        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private async void OnSaveTimerTick(object? sender, EventArgs e)
    {
        _saveTimer.Stop();

        try
        {
            await FlushPendingChangesAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _vm.SetStatus(string.Format(L("ToolNotesStatusError"), ex.Message), isError: true);
        }
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        var path = _vm.CurrentNotePath is not null
            ? Path.GetDirectoryName(_vm.CurrentNotePath) ?? _vm.NotesRootPath
            : _vm.NotesRootPath;
        OpenFolder(path);
    }

    // ── Drag & drop ─────────────────────────────────────────────────

    private void OnDragOver(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetDataPresent("NoteTreeNode"))
        {
            FileLogger.Debug("[NotesTool][DragDrop] sidebar drag-over routed to internal note handler");
            OnTreeViewInternalDragOver(sender, e);
            return;
        }

        if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)
            && e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] files
            && files.Any(f => f.EndsWith(".md", StringComparison.OrdinalIgnoreCase)))
        {
            e.Effects = System.Windows.DragDropEffects.Copy;
        }
        else
        {
            e.Effects = System.Windows.DragDropEffects.None;
        }

        e.Handled = true;
    }

    private async void OnDrop(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetDataPresent("NoteTreeNode"))
        {
            FileLogger.Debug("[NotesTool][DragDrop] sidebar drop routed to internal note handler");
            OnTreeViewInternalDrop(sender, e);
            return;
        }

        if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is not string[] files)
        {
            return;
        }

        var imported = 0;
        foreach (var source in files.Where(f => f.EndsWith(".md", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var fileName = Path.GetFileName(source);
                var target = Path.Combine(_vm.NotesRootPath, fileName);
                if (File.Exists(target))
                {
                    var baseName = Path.GetFileNameWithoutExtension(fileName);
                    target = Path.Combine(_vm.NotesRootPath,
                        $"{baseName}-{DateTime.Now:HHmmss}.md");
                }

                File.Copy(source, target);
                imported++;
            }
            catch
            {
                // Skip files that fail to copy
            }
        }

        if (imported > 0)
        {
            await _vm.ReloadAsync().ConfigureAwait(true);
            _vm.SetStatus(string.Format(L("ToolNotesStatusImported"), imported));
        }
    }

    // ── Auto-completion for [[ ──────────────────────────────────────

    private void OnTextAreaTextEntered(object sender, System.Windows.Input.TextCompositionEventArgs e)
    {
        if (e.Text != "[" || _vm.CurrentNotePath is null)
        {
            return;
        }

        var offset = Editor.CaretOffset;
        if (offset < 2 || Editor.Document.GetCharAt(offset - 2) != '[')
        {
            return;
        }

        ShowNoteLinkCompletion();
    }

    private void ShowNoteLinkCompletion()
    {
        if (_completionWindow is not null)
        {
            return;
        }

        var window = new CompletionWindow(Editor.TextArea);
        var data = window.CompletionList.CompletionData;

        foreach (var note in _vm.AllNotes)
        {
            if (!string.Equals(note.FilePath, _vm.CurrentNotePath, StringComparison.OrdinalIgnoreCase))
            {
                data.Add(new NoteLinkCompletionData(note.Title));
            }
        }

        if (data.Count == 0)
        {
            return;
        }

        _completionWindow = window;
        window.Show();
        window.Closed += (_, _) => _completionWindow = null;
    }

    // ── TreeView collapse/expand ──────────────────────────────────

    private void OnCollapseAllClick(object sender, RoutedEventArgs e)
        => SetAllTreeItemsExpanded(NotesTreeView, false);

    private void OnExpandAllClick(object sender, RoutedEventArgs e)
        => SetAllTreeItemsExpanded(NotesTreeView, true);

    private static void SetAllTreeItemsExpanded(ItemsControl parent, bool expanded)
    {
        foreach (var item in parent.Items)
        {
            if (parent.ItemContainerGenerator.ContainerFromItem(item) is TreeViewItem tvi)
            {
                tvi.IsExpanded = expanded;
                SetAllTreeItemsExpanded(tvi, expanded);
            }
        }
    }

    // ── Editor context menu ────────────────────────────────────────────

    private ContextMenu BuildEditorContextMenu()
    {
        var menu = new ContextMenu();
        menu.Opened += (_, _) => RefreshEditorContextMenu(menu);
        return menu;
    }

    private void RefreshEditorContextMenu(ContextMenu menu)
    {
        menu.Items.Clear();

        AddCtxItem(menu, "ToolNotesCtxBold", () => WrapEditorSelection("**", "**"));
        AddCtxItem(menu, "ToolNotesCtxItalic", () => WrapEditorSelection("*", "*"));
        AddCtxItem(menu, "ToolNotesCtxStrikethrough", () => WrapEditorSelection("~~", "~~"));
        AddCtxItem(menu, "ToolNotesCtxInlineCode", () => WrapEditorSelection("`", "`"));
        menu.Items.Add(new Separator());
        AddCtxItem(menu, "ToolNotesCtxCodeBlock", () => WrapEditorSelection("```\n", "\n```"));
        AddCtxItem(menu, "ToolNotesCtxBlockquote", () => PrefixEditorLines("> "));
        menu.Items.Add(new Separator());
        AddCtxItem(menu, "ToolNotesCtxLink", () =>
        {
            var sel = Editor.SelectedText;
            if (!string.IsNullOrEmpty(sel))
                WrapEditorSelection("[", MarkdownLinkSuffix);
            else
                InsertInEditor(MarkdownLinkTemplate);
        });
        AddCtxItem(menu, "ToolNotesCtxImage", () => InsertInEditor(MarkdownImageTemplate));
        AddCtxItem(menu, "ToolNotesCtxNoteLink", () => WrapEditorSelection("[[", "]]"));
        menu.Items.Add(new Separator());
        AddCtxItem(menu, "ToolNotesCtxHeading1", () => PrefixEditorLines("# "));
        AddCtxItem(menu, "ToolNotesCtxHeading2", () => PrefixEditorLines("## "));
        AddCtxItem(menu, "ToolNotesCtxHeading3", () => PrefixEditorLines("### "));
        menu.Items.Add(new Separator());
        AddCtxItem(menu, "ToolNotesCtxBulletList", () => PrefixEditorLines("- "));
        AddCtxItem(menu, "ToolNotesCtxNumberedList", () => PrefixEditorLines("1. "));
        AddCtxItem(menu, "ToolNotesCtxTaskList", () => PrefixEditorLines("- [ ] "));
        menu.Items.Add(new Separator());
        AddCtxItem(menu, "ToolNotesCtxTable", () => InsertInEditor(MarkdownTableTemplate));
        AddCtxItem(menu, "ToolNotesCtxHorizontalRule", () => InsertInEditor("\n---\n"));
    }

    private void AddCtxItem(ContextMenu menu, string labelKey, Action action)
    {
        var item = new MenuItem { Header = L(labelKey) };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }

    private void WrapEditorSelection(string prefix, string suffix)
    {
        var sel = Editor.SelectedText ?? string.Empty;
        Editor.SelectedText = prefix + sel + suffix;
    }

    private void PrefixEditorLines(string prefix)
    {
        var sel = Editor.SelectedText;
        if (!string.IsNullOrEmpty(sel))
        {
            var lines = sel.Split('\n');
            Editor.SelectedText = string.Join("\n", lines.Select(l => prefix + l));
        }
        else
        {
            InsertInEditor(prefix);
        }
    }

    private void InsertInEditor(string text)
    {
        var offset = Editor.CaretOffset;
        Editor.Document.Insert(offset, text);
        Editor.CaretOffset = offset + text.Length;
    }

    // ── Sidebar toggle ───────────────────────────────────────────────

    private void OnToggleSidebarClick(object sender, RoutedEventArgs e)
    {
        if (_responsiveSidebarHidden && !_sidebarVisible)
        {
            _responsiveSidebarHidden = false;
        }

        SetSidebarVisibility(!_sidebarVisible, persistWidth: true);
    }

    private void OnViewSizeChanged(object sender, SizeChangedEventArgs e)
        => UpdateResponsiveLayout();

    private void UpdateResponsiveLayout()
    {
        if (NotesLayoutGrid is null)
        {
            return;
        }

        if (ActualWidth < NotesSidebarCollapseWidth)
        {
            if (_sidebarVisible)
            {
                _responsiveSidebarHidden = true;
                SetSidebarVisibility(false, persistWidth: false);
            }
            return;
        }

        if (_responsiveSidebarHidden)
        {
            _responsiveSidebarHidden = false;
            SetSidebarVisibility(true, persistWidth: false);
        }
    }

    private void SetSidebarVisibility(bool visible, bool persistWidth)
    {
        var grid = NotesLayoutGrid ?? SidebarBorder.Parent as Grid;
        if (grid is null)
        {
            return;
        }

        _sidebarVisible = visible;

        if (visible)
        {
            if (_savedSidebarWidth.Value < 200)
            {
                _savedSidebarWidth = new GridLength(280);
            }

            grid.ColumnDefinitions[0].Width = _savedSidebarWidth;
            grid.ColumnDefinitions[0].MinWidth = 200;
            grid.ColumnDefinitions[1].Width = new GridLength(6);
            SidebarBorder.Visibility = Visibility.Visible;
            EditorSplitter.Visibility = Visibility.Visible;
            return;
        }

        var currentWidth = grid.ColumnDefinitions[0].ActualWidth;
        if (currentWidth >= 200)
        {
            _savedSidebarWidth = new GridLength(currentWidth);
        }

        grid.ColumnDefinitions[0].MinWidth = 0;
        grid.ColumnDefinitions[0].Width = new GridLength(0);
        grid.ColumnDefinitions[1].Width = new GridLength(0);
        SidebarBorder.Visibility = Visibility.Collapsed;
        EditorSplitter.Visibility = Visibility.Collapsed;

        if (persistWidth && _savedSidebarWidth.Value >= 200)
        {
            PersistSidebarWidth((int)_savedSidebarWidth.Value);
        }
    }

    // ── Help ────────────────────────────────────────────────────────

    private void OnHelpClick(object sender, RoutedEventArgs e)
    {
        ToggleHelpPanel(L("ToolHelpNOTES"));
    }

    private void OnCloseHelpClick(object sender, RoutedEventArgs e)
    {
        HelpPanel.Visibility = Visibility.Collapsed;
    }

    private void OnMarkdownHelpClick(object sender, RoutedEventArgs e)
    {
        ToggleHelpPanel(L("ToolNotesMarkdownHelp"));
    }

    private void ToggleHelpPanel(string helpText)
    {
        var normalizedHelp = helpText.Replace("\\n", "\n");
        var isSameContent = HelpPanel.Visibility == Visibility.Visible
            && string.Equals(TxtHelpContent.Text, normalizedHelp, StringComparison.Ordinal);

        if (isSameContent)
        {
            HelpPanel.Visibility = Visibility.Collapsed;
            return;
        }

        TxtHelpContent.Text = normalizedHelp;
        HelpPanel.Visibility = Visibility.Visible;
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        if (e.PropertyName is nameof(NotesToolViewModel.CurrentNotePath)
            or nameof(NotesToolViewModel.HasSelection)
            or nameof(NotesToolViewModel.SelectedNote))
        {
            ApplyViewModelSelection();
        }
    }

    private void ApplyViewModelSelection()
    {
        _isLoadingNote = true;
        try
        {
            UpdateSelectionState();
            SynchronizeTreeSelection();

            var content = _vm.CurrentMarkdown ?? string.Empty;
            _lastMilkdownContent = content;

            if (_useMilkdown)
            {
                MilkdownEditor.SetContent(content);
                MilkdownEditor.SetReadOnly(!_vm.HasSelection);
            }
            else
            {
                if (!string.Equals(Editor.Text, content, StringComparison.Ordinal))
                {
                    Editor.Text = content;
                }

                Editor.IsReadOnly = !_vm.HasSelection;
                if (_vm.HasSelection)
                {
                    Editor.CaretOffset = 0;
                }
            }
        }
        finally
        {
            _isLoadingNote = false;
        }
    }

    private void SynchronizeTreeSelection()
    {
        if (_vm.SelectedNote is null)
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
        {
            _suppressSelectionChanged = true;
            try
            {
                SelectTreeNode(NotesTreeView, _vm.SelectedNote);
            }
            finally
            {
                _suppressSelectionChanged = false;
            }
        }));
    }

    private static bool SelectTreeNode(ItemsControl parent, NoteTreeNode target)
    {
        foreach (var item in parent.Items)
        {
            if (item is not NoteTreeNode node)
            {
                continue;
            }

            if (parent.ItemContainerGenerator.ContainerFromItem(item) is not TreeViewItem treeViewItem)
            {
                continue;
            }

            if (ReferenceEquals(node, target))
            {
                treeViewItem.IsSelected = true;
                treeViewItem.BringIntoView();
                return true;
            }

            if (node.Children.Count == 0)
            {
                continue;
            }

            var wasExpanded = treeViewItem.IsExpanded;
            treeViewItem.IsExpanded = true;
            treeViewItem.UpdateLayout();
            if (SelectTreeNode(treeViewItem, target))
            {
                return true;
            }

            treeViewItem.IsExpanded = wasExpanded;
        }

        return false;
    }

    private void EnsureNoteHostFocused()
    {
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (_useMilkdown)
            {
                MilkdownEditor.FocusEditor();
            }
            else
            {
                Editor.Focus();
                Editor.SelectAll();
            }
        });
    }

    private void AttachVmOutputHandlers()
    {
        if (_vmOutputHandlersAttached)
        {
            return;
        }

        _vm.CopyConfluenceRequested += OnCopyConfluenceRequested;
        _vm.ExportConfluenceRequested += OnExportConfluenceRequested;
        _vm.ExportHtmlRequested += OnExportHtmlRequested;
        _vmOutputHandlersAttached = true;
    }

    private void DetachVmOutputHandlers()
    {
        if (!_vmOutputHandlersAttached)
        {
            return;
        }

        _vm.CopyConfluenceRequested -= OnCopyConfluenceRequested;
        _vm.ExportConfluenceRequested -= OnExportConfluenceRequested;
        _vm.ExportHtmlRequested -= OnExportHtmlRequested;
        _vmOutputHandlersAttached = false;
    }

    private void OnCopyConfluenceRequested(object? sender, string payload)
    {
        try
        {
            Clipboard.SetText(payload);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            return;
        }

        CopyFeedbackHelper.ShowCopyFeedback(BtnCopyConfluence);
    }

    private async void OnExportConfluenceRequested(object? sender, string payload)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = L("ToolNotesExportConfluenceFilter"),
            FileName = $"{Path.GetFileNameWithoutExtension(_vm.CurrentNotePath)}.storage.xml"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            await File.WriteAllTextAsync(dialog.FileName, payload).ConfigureAwait(true);
            CopyFeedbackHelper.ShowCopyFeedback(BtnExportConfluence);
        }
        catch (Exception ex)
        {
            _vm.SetStatus(string.Format(L("ToolNotesStatusError"), ex.Message), isError: true);
        }
    }

    private async void OnExportHtmlRequested(object? sender, string payload)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = L("ToolNotesExportHtmlFilter"),
            FileName = $"{Path.GetFileNameWithoutExtension(_vm.CurrentNotePath)}.html"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            await File.WriteAllTextAsync(dialog.FileName, payload).ConfigureAwait(true);
            CopyFeedbackHelper.ShowCopyFeedback(BtnExportHtml);
        }
        catch (Exception ex)
        {
            _vm.SetStatus(string.Format(L("ToolNotesStatusError"), ex.Message), isError: true);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        SizeChanged -= OnViewSizeChanged;
        _saveTimer.Tick -= OnSaveTimerTick;
        _saveTimer.Stop();
        DetachVmOutputHandlers();
        _vm.PropertyChanged -= OnViewModelPropertyChanged;
        Editor.TextChanged -= OnEditorTextChanged;
        Editor.TextArea.TextEntered -= OnTextAreaTextEntered;
        Editor.TextArea.PreviewMouseDown -= OnEditorPreviewMouseDown;
        MilkdownEditor.ContentChanged -= OnMilkdownContentChanged;
        MilkdownEditor.LinkClicked -= OnMilkdownLinkClicked;
        MilkdownEditor.EditorReady -= OnMilkdownReady;
        _vm.TrySaveSynchronously();
        _vm.Dispose();

        // Persist sidebar width if it was resized via the GridSplitter
        if (_sidebarVisible && !_responsiveSidebarHidden && SidebarBorder.Parent is Grid layoutGrid)
        {
            var currentWidth = (int)layoutGrid.ColumnDefinitions[0].ActualWidth;
            if (currentWidth >= 200 && currentWidth != (int)_savedSidebarWidth.Value)
            {
                PersistSidebarWidth(currentWidth);
            }
        }

        if (_useMilkdown)
        {
            try { MilkdownEditor.Dispose(); }
            catch { /* Best effort */ }
        }

        GC.SuppressFinalize(this);
    }

    private static NotesToolViewModel ResolveViewModel()
    {
        var services = (Application.Current as App)?.Services;
        return services?.GetRequiredService<NotesToolViewModel>()
            ?? throw new InvalidOperationException("NotesToolViewModel is not registered.");
    }

    private sealed class NoteLinkCompletionData : ICSharpCode.AvalonEdit.CodeCompletion.ICompletionData
    {
        public NoteLinkCompletionData(string title)
        {
            Text = title;
        }

        public System.Windows.Media.ImageSource? Image => null;
        public string Text { get; }
        public object Content => Text;
        public object Description => Text;
        public double Priority => 0;

        public void Complete(
            ICSharpCode.AvalonEdit.Editing.TextArea textArea,
            ICSharpCode.AvalonEdit.Document.ISegment completionSegment,
            EventArgs insertionRequestEventArgs)
        {
            textArea.Document.Replace(completionSegment, $"{Text}]]");
        }
    }
}
