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

using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Heimdall.App.Services;
using Heimdall.App.Themes;
using Heimdall.App.ViewModels;
using Heimdall.Core.Localization;
using ICSharpCode.AvalonEdit.Highlighting;
using Microsoft.Extensions.DependencyInjection;

namespace Heimdall.App.Views;

/// <summary>
/// Embedded text editor with syntax highlighting powered by AvalonEdit.
/// Used for editing local and remote (SFTP) files with theme-aware colors.
/// </summary>
public partial class EmbeddedEditorView : UserControl
{
    private readonly EmbeddedEditorViewModel _viewModel;
    private readonly LocalizationManager? _localizer;
    private HeimdallThemeService? _themeService;
    private bool _suppressTextChangeNotifications;

    /// <summary>Requests durable persistence when the user saves a remote file.</summary>
    public event Func<string, string, Task<bool>>? SaveRequested
    {
        add => _viewModel.SaveRequested += value;
        remove => _viewModel.SaveRequested -= value;
    }

    /// <summary>Raised when the user closes the editor.</summary>
    public event Action? CloseRequested
    {
        add => _viewModel.CloseRequested += value;
        remove => _viewModel.CloseRequested -= value;
    }

    /// <summary>
    /// Forwards the host's close veto to the view model. Hosts hold the view, not its view model,
    /// so the wiring has to be reachable from here.
    /// </summary>
    internal Func<bool>? CloseRefusedByHost
    {
        get => _viewModel.CloseRefusedByHost;
        set => _viewModel.CloseRefusedByHost = value;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="EmbeddedEditorView"/> class.
    /// </summary>
    /// <param name="localizer">Optional localization manager passed through to the view model.</param>
    public EmbeddedEditorView(LocalizationManager? localizer = null)
    {
        _localizer = localizer;
        _viewModel = new EmbeddedEditorViewModel(localizer);
        InitializeComponent();
        DataContext = _viewModel;
        ApplyTheme();

        Editor.TextChanged += (_, _) =>
        {
            if (!_suppressTextChangeNotifications)
            {
                _viewModel.NotifyTextChanged();
            }
        };

        Editor.TextArea.Caret.PositionChanged += (_, _) =>
            _viewModel.UpdateCursorPosition(Editor.TextArea.Caret.Line, Editor.TextArea.Caret.Column);

        _viewModel.UpdateCursorPosition(Editor.TextArea.Caret.Line, Editor.TextArea.Caret.Column);

        Loaded += OnViewLoaded;
        Unloaded += OnViewUnloaded;
    }

    private void OnViewLoaded(object sender, RoutedEventArgs e)
    {
        if (_themeService is null)
        {
            _themeService = (Application.Current as App)?.Services?.GetService<HeimdallThemeService>();
            if (_themeService is not null)
            {
                _themeService.ThemeChanged += OnThemeServiceThemeChanged;
                // Re-apply in case the active theme was swapped before this view was created.
                ApplyTheme();
            }
        }

        _viewModel.SetDialogService((Application.Current as App)?.Services?.GetService<IDialogService>());
    }

    private void OnViewUnloaded(object sender, RoutedEventArgs e)
    {
        if (_themeService is not null)
        {
            _themeService.ThemeChanged -= OnThemeServiceThemeChanged;
            _themeService = null;
        }
    }

    private void OnThemeServiceThemeChanged(string themeName)
    {
        Dispatcher.BeginInvoke(ApplyTheme);
    }

    /// <summary>
    /// Opens a file for editing with automatic syntax detection.
    /// </summary>
    public async Task OpenFile(string filePath)
    {
        var content = await _viewModel.LoadFileAsync(filePath);
        _viewModel.SyntaxName = ResolveSyntaxName(Path.GetExtension(filePath));
        SetEditorText(content ?? BuildLoadErrorText());
        ApplySyntaxHighlighting();
        _viewModel.UpdateCursorPosition(Editor.TextArea.Caret.Line, Editor.TextArea.Caret.Column);
    }

    /// <summary>
    /// Opens the editor with provided content (for remote files).
    /// </summary>
    public void OpenContent(string fileName, string content, string? syntaxName = null)
    {
        _viewModel.LoadContent(fileName, ResolveSyntaxName(Path.GetExtension(fileName), syntaxName));
        SetEditorText(content);
        ApplySyntaxHighlighting();
        _viewModel.UpdateCursorPosition(Editor.TextArea.Caret.Line, Editor.TextArea.Caret.Column);
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await _viewModel.SaveAsync(Editor.Text);
        }
        catch (Exception ex)
        {
            Heimdall.Core.Logging.FileLogger.Warn(
                $"EmbeddedEditor save handler failed: {ex.Message}");
        }
    }

    private async void OnCloseClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await _viewModel.RequestClose();
        }
        catch (Exception ex)
        {
            Heimdall.Core.Logging.FileLogger.Warn(
                $"EmbeddedEditor close handler failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Applies the editor chrome colors (background, line numbers, selection,
    /// current-line highlight) from the active theme's <see cref="ResourceDictionary"/>.
    /// All ThemeForge themes are Dracula variants and share the same syntax palette, so the
    /// token-level highlighting is fixed Dracula while the outer chrome follows the
    /// theme swap.
    /// </summary>
    public void ApplyTheme()
    {
        // Theme bridge unavailable (e.g. XAML designer preview): keep AvalonEdit's
        // built-in defaults instead of painting hardcoded colors.
        if (TryResolveColor("BackgroundBrush") is not { } background
            || TryResolveColor("TextPrimaryBrush") is not { } foreground
            || TryResolveColor("TextSecondaryBrush") is not { } lineNumber
            || TryResolveColor("HighlightBrush") is not { } selection
            || TryResolveColor("BorderBrush") is not { } border)
        {
            return;
        }

        Editor.Background = new SolidColorBrush(background);
        Editor.Foreground = new SolidColorBrush(foreground);
        Editor.LineNumbersForeground = new SolidColorBrush(lineNumber);

        Editor.TextArea.SelectionBrush = new SolidColorBrush(selection);
        Editor.TextArea.SelectionForeground = null;

        var currentLineBrush = new SolidColorBrush(selection) { Opacity = 0.3 };
        Editor.TextArea.TextView.CurrentLineBackground = currentLineBrush;
        Editor.TextArea.TextView.CurrentLineBorder = new System.Windows.Media.Pen(
            new SolidColorBrush(border), 1);

        // Syntax tokens use the fixed Dracula palette — it reads well against every
        // Dracula variant and avoids per-theme highlight-definition plumbing.
        DraculaSyntaxPalette.Apply(Editor.SyntaxHighlighting);
    }

    private static System.Windows.Media.Color? TryResolveColor(string brushKey)
    {
        return Application.Current?.TryFindResource(brushKey) is SolidColorBrush brush
            ? brush.Color
            : null;
    }

    private void ApplySyntaxHighlighting()
    {
        Editor.SyntaxHighlighting = ResolveHighlighting(_viewModel.SyntaxName);
        DraculaSyntaxPalette.Apply(Editor.SyntaxHighlighting);
    }

    private void SetEditorText(string content)
    {
        _suppressTextChangeNotifications = true;
        try
        {
            Editor.Text = content;
            Editor.CaretOffset = 0;
        }
        finally
        {
            _suppressTextChangeNotifications = false;
        }

        _viewModel.IsModified = false;
    }

    private string BuildLoadErrorText()
    {
        return string.IsNullOrEmpty(_viewModel.LoadErrorMessage)
            ? string.Empty
            : (_localizer?.Format("EditorLoadErrorBody", _viewModel.LoadErrorMessage)
                ?? $"Error loading file: {_viewModel.LoadErrorMessage}");
    }

    private static IHighlightingDefinition? ResolveHighlighting(string syntaxName)
    {
        if (string.Equals(syntaxName, "Plain Text", StringComparison.Ordinal))
        {
            return null;
        }

        return HighlightingManager.Instance.GetDefinition(syntaxName);
    }

    private static string ResolveSyntaxName(string? ext, string? explicitSyntaxName = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitSyntaxName))
        {
            return explicitSyntaxName;
        }

        if (string.IsNullOrEmpty(ext))
        {
            return "Plain Text";
        }

        var builtin = HighlightingManager.Instance.GetDefinitionByExtension(ext)?.Name;
        if (!string.IsNullOrEmpty(builtin))
        {
            return builtin;
        }

        return ext.ToLowerInvariant() switch
        {
            ".yml" or ".yaml" or ".toml" => "MarkDown",
            ".conf" or ".cfg" or ".ini" or ".env" or ".properties" or ".service" => "MarkDown",
            ".ps1" or ".psm1" or ".psd1" => "PowerShell",
            ".sh" or ".bash" or ".bashrc" or ".zshrc" or ".profile" => "Boo",
            ".md" or ".markdown" => "MarkDown",
            ".json" or ".jsonc" => "JavaScript",
            ".ts" or ".tsx" or ".jsx" => "JavaScript",
            ".scss" or ".less" => "CSS",
            ".py" or ".pyw" => "Python",
            ".rb" => "Ruby",
            ".log" or ".txt" or ".csv" => "Plain Text",
            _ => "Plain Text"
        };
    }
}
