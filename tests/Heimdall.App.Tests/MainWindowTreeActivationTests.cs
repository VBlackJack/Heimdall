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
using System.Windows.Input;
using System.Xml.Linq;
using Heimdall.App.ViewModels;
using Heimdall.Core.Configuration;

namespace Heimdall.App.Tests;

public sealed class MainWindowTreeActivationTests
{
    private const string TreeInteractionsRelativePath = @"src\Heimdall.App\MainWindow.TreeInteractions.cs";
    private const string MainWindowXamlRelativePath = @"src\Heimdall.App\MainWindow.xaml";

    private const string KeyHandlerSignature =
        "private async void OnSessionTreeViewPreviewKeyDown(object sender, KeyEventArgs e)";

    // ---- The gesture is wired at all -------------------------------------------------------

    /// <summary>
    /// The defect this file exists to catch was a missing Enter branch in a private WPF event
    /// handler on MainWindow. The handler cannot be reached from a unit test, while the helpers
    /// it calls can: a suite that only calls the helpers stays green when the handler stops
    /// calling them, which is the regression itself. This guard reads the handler's own source,
    /// the technique DeadClickRegressionTests already uses for a private view method, and fails
    /// when the dispatch is removed, reordered, or left unconsumed.
    /// </summary>
    [Fact]
    public void SessionTreeKeyHandler_SourceContract_DispatchesEnterToActivation()
    {
        string body = ExtractMethodBody(ReadTreeInteractionsSource(), KeyHandlerSignature);

        int enterIndex = body.IndexOf("e.Key == Key.Enter", StringComparison.Ordinal);
        int resolveIndex = body.IndexOf("ResolveTreeActivationTarget(", StringComparison.Ordinal);
        int applyIndex = body.IndexOf("e.Handled = ApplyTreeActivation(", StringComparison.Ordinal);
        int deleteGuardIndex = body.IndexOf("e.Key != Key.Delete", StringComparison.Ordinal);

        Assert.True(
            enterIndex >= 0,
            "OnSessionTreeViewPreviewKeyDown must branch on Key.Enter: the tree is otherwise inert "
            + "on Enter while the F1 help and TooltipConnect both promise it connects.");
        Assert.True(
            resolveIndex > enterIndex,
            "The Enter branch must call ResolveTreeActivationTarget to pick the session to act on.");
        Assert.True(
            applyIndex > resolveIndex,
            "The Enter branch must assign ApplyTreeActivation's result to e.Handled: an unconsumed "
            + "Enter tunnels on to the rename editor instead of ending the gesture.");
        Assert.True(
            deleteGuardIndex > applyIndex,
            "The Enter branch must precede the Delete guard, which returns for every non-Delete key "
            + "and would leave a later Enter branch unreachable.");
    }

    /// <summary>
    /// Guards the two inputs the branch must forward. Each carries a decision the resolver
    /// cannot take on its own, so a caller that hardcodes either one restores a defect while
    /// every resolver case below stays green.
    /// </summary>
    [Fact]
    public void SessionTreeKeyHandler_SourceContract_ForwardsRepeatAndSelectionToResolver()
    {
        string body = ExtractMethodBody(ReadTreeInteractionsSource(), KeyHandlerSignature);
        string arguments = ExtractCallArguments(body, "ResolveTreeActivationTarget(");

        Assert.Contains("e.IsRepeat", arguments, StringComparison.Ordinal);
        Assert.Contains("SelectedItems.Contains", arguments, StringComparison.Ordinal);
    }

    /// <summary>
    /// A correct Enter branch on a handler no longer attached to the tree is the same silent
    /// gesture from the user's side.
    /// </summary>
    [Fact]
    public void SessionTree_XamlContract_RoutesPreviewKeyDownToTheHandler()
    {
        XDocument document = XDocument.Load(Path.Combine(FindRepoRoot(), MainWindowXamlRelativePath));
        XNamespace xamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement? tree = document
            .Descendants()
            .SingleOrDefault(element =>
                string.Equals(
                    (string?)element.Attribute(xamlNamespace + "Name"),
                    "SessionTreeView",
                    StringComparison.Ordinal));

        Assert.True(tree is not null, "MainWindow.xaml must declare the sessions tree as SessionTreeView.");
        Assert.Equal("OnSessionTreeViewPreviewKeyDown", (string?)tree.Attribute("PreviewKeyDown"));
    }

    // ---- Which session the gesture acts on -------------------------------------------------

    [Fact]
    public void Enter_OnFocusedServer_ResolvesThatServer()
    {
        ServerItemViewModel focused = CreateServer("focused");
        ServerItemViewModel selected = CreateServer("selected");

        ServerItemViewModel? resolved = Resolve(
            Key.Enter,
            ModifierKeys.None,
            isInlineRenameEditorSource: false,
            focused,
            selected,
            selection: [focused, selected]);

        Assert.Same(focused, resolved);
    }

    [Fact]
    public void Enter_WithoutFocusedNode_FallsBackToTreeSelection()
    {
        ServerItemViewModel selected = CreateServer("selected");

        ServerItemViewModel? resolved = Resolve(
            Key.Enter,
            ModifierKeys.None,
            isInlineRenameEditorSource: false,
            focusedNode: null,
            selected,
            selection: [selected]);

        Assert.Same(selected, resolved);
    }

    [Fact]
    public void Enter_OnFolder_ResolvesNoTarget()
    {
        ServerItemViewModel selected = CreateServer("selected");

        ServerItemViewModel? resolved = Resolve(
            Key.Enter,
            ModifierKeys.None,
            isInlineRenameEditorSource: false,
            new FolderViewModel { Name = "Production", FullPath = "Production" },
            selected,
            selection: [selected]);

        Assert.Null(resolved);
    }

    /// <summary>
    /// Ctrl+Space on one of several selected sessions removes it from the selection but leaves
    /// keyboard focus on it: the highlight moves to another row while focus stays behind. Enter
    /// must follow the highlight, or it connects the session the user has just deselected.
    /// </summary>
    [Fact]
    public void Enter_OnDeselectedFocusedServer_ResolvesNoTarget()
    {
        ServerItemViewModel deselected = CreateServer("deselected");
        ServerItemViewModel stillSelected = CreateServer("still-selected");

        ServerItemViewModel? resolved = Resolve(
            Key.Enter,
            ModifierKeys.None,
            isInlineRenameEditorSource: false,
            deselected,
            selectedItem: null,
            selection: [stillSelected]);

        Assert.Null(resolved);
    }

    /// <summary>
    /// The same gesture on a single selected session empties the selection outright, so nothing
    /// is highlighted and Enter has no server to act on.
    /// </summary>
    [Fact]
    public void Enter_WithEmptySelection_ResolvesNoTarget()
    {
        ServerItemViewModel focused = CreateServer("focused");

        ServerItemViewModel? resolved = Resolve(
            Key.Enter,
            ModifierKeys.None,
            isInlineRenameEditorSource: false,
            focused,
            focused,
            selection: []);

        Assert.Null(resolved);
    }

    [Fact]
    public void Enter_WhileInlineRenaming_ResolvesNoTarget()
    {
        ServerItemViewModel focused = CreateServer("focused");

        ServerItemViewModel? resolved = Resolve(
            Key.Enter,
            ModifierKeys.None,
            isInlineRenameEditorSource: true,
            focused,
            focused,
            selection: [focused]);

        Assert.Null(resolved);
    }

    /// <summary>
    /// A held Enter repeats about thirty times a second. The tool branch opens an uncounted,
    /// uncapped tab per press, so only the first press of a hold may resolve to a session.
    /// </summary>
    [Fact]
    public void Enter_FromKeyboardAutoRepeat_ResolvesNoTarget()
    {
        ServerItemViewModel focused = CreateServer("ping", "TOOL:PING");

        ServerItemViewModel? repeated = Resolve(
            Key.Enter,
            ModifierKeys.None,
            isInlineRenameEditorSource: false,
            focused,
            focused,
            isRepeat: true,
            selection: [focused]);

        ServerItemViewModel? firstPress = Resolve(
            Key.Enter,
            ModifierKeys.None,
            isInlineRenameEditorSource: false,
            focused,
            focused,
            isRepeat: false,
            selection: [focused]);

        Assert.Null(repeated);
        Assert.Same(focused, firstPress);
    }

    [Theory]
    [InlineData(ModifierKeys.Control)]
    [InlineData(ModifierKeys.Shift)]
    [InlineData(ModifierKeys.Control | ModifierKeys.Shift)]
    public void Enter_WithModifiers_ResolvesNoTarget(ModifierKeys modifiers)
    {
        ServerItemViewModel focused = CreateServer("focused");

        ServerItemViewModel? resolved = Resolve(
            Key.Enter,
            modifiers,
            isInlineRenameEditorSource: false,
            focused,
            focused,
            selection: [focused]);

        Assert.Null(resolved);
    }

    /// <summary>
    /// The key test is the resolver's own contract rather than a reachable path: the single
    /// caller enters its branch only for Key.Enter and hands the same key straight back. Kept
    /// so the helper stays inert should a second caller ever route another key through it.
    /// </summary>
    [Theory]
    [InlineData(Key.Delete)]
    [InlineData(Key.F2)]
    public void NonEnterKeys_ResolveNoTarget(Key key)
    {
        ServerItemViewModel focused = CreateServer("focused");

        ServerItemViewModel? resolved = Resolve(
            key,
            ModifierKeys.None,
            isInlineRenameEditorSource: false,
            focused,
            focused,
            selection: [focused]);

        Assert.Null(resolved);
    }

    // ---- What the gesture then does --------------------------------------------------------

    [Fact]
    public void ToolSession_OpensToolTab()
    {
        ServerItemViewModel server = CreateServer("ping", "TOOL:PING");
        ServerItemViewModel? openedTool = null;
        ServerItemViewModel? connected = null;

        bool handled = MainWindow.ApplyTreeActivation(
            server,
            target => openedTool = target,
            target => connected = target);

        Assert.True(handled);
        Assert.Same(server, openedTool);
        Assert.Null(connected);
    }

    [Fact]
    public void RemoteSession_UsesConnectCommand()
    {
        ServerItemViewModel server = CreateServer("shell", "SSH");
        ServerItemViewModel? openedTool = null;
        ServerItemViewModel? connected = null;

        bool handled = MainWindow.ApplyTreeActivation(
            server,
            target => openedTool = target,
            target => connected = target);

        Assert.True(handled);
        Assert.Same(server, connected);
        Assert.Null(openedTool);
    }

    [Fact]
    public void WithoutTarget_IsNotHandled()
    {
        bool openedTool = false;
        bool connected = false;

        bool handled = MainWindow.ApplyTreeActivation(
            server: null,
            _ => openedTool = true,
            _ => connected = true);

        Assert.False(handled);
        Assert.False(openedTool);
        Assert.False(connected);
    }

    /// <param name="selection">
    /// The sessions the view model reports as selected. Null stands for "every session is
    /// selected", so a case that is not about the selection rule states nothing about it.
    /// </param>
    private static ServerItemViewModel? Resolve(
        Key key,
        ModifierKeys modifiers,
        bool isInlineRenameEditorSource,
        object? focusedNode,
        object? selectedItem,
        bool isRepeat = false,
        IReadOnlyList<ServerItemViewModel>? selection = null)
    {
        return MainWindow.ResolveTreeActivationTarget(
            key,
            modifiers,
            isInlineRenameEditorSource,
            isRepeat,
            focusedNode,
            selectedItem,
            candidate => selection is null || selection.Contains(candidate));
    }

    private static ServerItemViewModel CreateServer(string id, string connectionType = "SSH")
    {
        return ServerItemViewModel.FromDto(new ServerProfileDto
        {
            Id = id,
            DisplayName = id,
            RemoteServer = $"{id}.example.test",
            ConnectionType = connectionType
        });
    }

    private static string ReadTreeInteractionsSource()
    {
        string path = Path.Combine(FindRepoRoot(), TreeInteractionsRelativePath);
        Assert.True(File.Exists(path), $"Source file not found: {path}");
        return File.ReadAllText(path);
    }

    private static string ExtractMethodBody(string source, string signature)
    {
        int signatureIndex = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(signatureIndex >= 0, $"Method signature was not found: {signature}");

        int openingBraceIndex = source.IndexOf('{', signatureIndex + signature.Length);
        Assert.True(openingBraceIndex >= 0, $"Opening brace was not found for: {signature}");

        int depth = 0;
        for (int index = openingBraceIndex; index < source.Length; index++)
        {
            switch (source[index])
            {
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0)
                    {
                        return source[(openingBraceIndex + 1)..index];
                    }

                    break;
            }
        }

        throw new InvalidDataException($"Closing brace was not found for: {signature}");
    }

    private static string ExtractCallArguments(string body, string callPrefix)
    {
        int callIndex = body.IndexOf(callPrefix, StringComparison.Ordinal);
        Assert.True(callIndex >= 0, $"Call was not found: {callPrefix}");

        int openingParenIndex = callIndex + callPrefix.Length - 1;
        int depth = 0;
        for (int index = openingParenIndex; index < body.Length; index++)
        {
            switch (body[index])
            {
                case '(':
                    depth++;
                    break;
                case ')':
                    depth--;
                    if (depth == 0)
                    {
                        return body[(openingParenIndex + 1)..index];
                    }

                    break;
            }
        }

        throw new InvalidDataException($"Closing parenthesis was not found for: {callPrefix}");
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Heimdall.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
