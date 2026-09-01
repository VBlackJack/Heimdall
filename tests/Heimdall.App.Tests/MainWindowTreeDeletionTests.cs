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
using Heimdall.App.ViewModels;
using Heimdall.Core.Configuration;

namespace Heimdall.App.Tests;

/// <summary>
/// Delete on the sessions tree, and the node it acts on.
/// </summary>
/// <remarks>
/// Clicking a folder leaves the previously selected session selected, highlighted and shown in
/// the detail panel; the folder gets only the focus ring. Enter already refuses to act in that
/// state. Delete used to fall through to the window-level shortcut, which deletes the selected
/// session - so the confirmation named a session the user had not touched.
/// </remarks>
public sealed class MainWindowTreeDeletionTests
{
    private const string TreeInteractionsRelativePath = @"src\Heimdall.App\MainWindow.TreeInteractions.cs";

    private const string KeyHandlerSignature =
        "private async void OnSessionTreeViewPreviewKeyDown(object sender, KeyEventArgs e)";

    // ---- The resolver is reached, and reached with the focused node ------------------------

    /// <summary>
    /// The resolver can be correct while the handler never calls it, which is the defect itself.
    /// The argument assertion matters just as much: the tree's own SelectedItem is never a folder
    /// - OnTreeViewSelectedItemChanged pushes a folder container's IsSelected back to false - so a
    /// resolver fed from it would never see the case it exists for and would pass every unit test
    /// below while changing nothing on screen.
    /// </summary>
    [Fact]
    public void SessionTreeKeyHandler_SourceContract_ResolvesDeleteFromTheFocusedNode()
    {
        string body = ExtractMethodBody(ReadTreeInteractionsSource(), KeyHandlerSignature);

        int deleteGuardIndex = body.IndexOf("e.Key != Key.Delete", StringComparison.Ordinal);
        int resolveIndex = body.IndexOf("ResolveTreeDeletion(", StringComparison.Ordinal);

        Assert.True(
            deleteGuardIndex >= 0,
            "OnSessionTreeViewPreviewKeyDown must still gate its tail on Key.Delete.");
        Assert.True(
            resolveIndex > deleteGuardIndex,
            "The Delete branch must call ResolveTreeDeletion to decide what the press acts on: "
            + "without it the press falls through to the window-level shortcut, which deletes "
            + "the selected session whatever the focus ring is on.");

        string arguments = ExtractCallArguments(body, "ResolveTreeDeletion(");

        Assert.Contains("Keyboard.FocusedElement", arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("SessionTreeView.SelectedItem", arguments, StringComparison.Ordinal);
    }

    /// <summary>
    /// A resolution that consumes nothing changes nothing: the window-level shortcut runs next
    /// and deletes the selected session.
    /// </summary>
    [Fact]
    public void SessionTreeKeyHandler_SourceContract_ConsumesEveryDeleteItResolves()
    {
        string body = ExtractMethodBody(ReadTreeInteractionsSource(), KeyHandlerSignature);

        int resolveIndex = body.IndexOf("ResolveTreeDeletion(", StringComparison.Ordinal);
        Assert.True(resolveIndex >= 0, "The Delete branch must call ResolveTreeDeletion.");

        int consumeIndex = body.IndexOf("e.Handled = true;", resolveIndex, StringComparison.Ordinal);
        int noOpReturnIndex = body.IndexOf(
            "if (!deleteSelection)",
            resolveIndex,
            StringComparison.Ordinal);

        Assert.True(consumeIndex > resolveIndex, "The resolved press must be consumed.");
        Assert.True(
            noOpReturnIndex > consumeIndex,
            "The press must be consumed before the branch that acts on nothing returns, or a "
            + "folder-focused Delete still reaches the window-level DeleteServerCommand.");
    }

    // ---- What the press resolves to --------------------------------------------------------

    [Fact]
    public void Delete_OnFocusedFolder_IsConsumedAndDeletesNothing()
    {
        (bool handled, bool deleteSelection) = Resolve(CreateFolder(), selectionCount: 1);

        Assert.True(handled);
        Assert.False(deleteSelection);
    }

    /// <summary>
    /// Focus wins over the selection, exactly as it does for Enter: a plural selection made
    /// elsewhere does not turn a press on a folder into a bulk delete.
    /// </summary>
    [Fact]
    public void Delete_OnFocusedFolder_WithPluralSelection_StillDeletesNothing()
    {
        (bool handled, bool deleteSelection) = Resolve(CreateFolder(), selectionCount: 4);

        Assert.True(handled);
        Assert.False(deleteSelection);
    }

    [Fact]
    public void Delete_WithPluralSelection_DeletesTheSelection()
    {
        (bool handled, bool deleteSelection) = Resolve(CreateServer("focused"), selectionCount: 3);

        Assert.True(handled);
        Assert.True(deleteSelection);
    }

    /// <summary>
    /// A single selection stays with the window-level shortcut, which owns the confirmation and
    /// the Ctrl+Del gesture the F1 help documents.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Delete_OnFocusedServer_WithoutPluralSelection_FallsThrough(int selectionCount)
    {
        (bool handled, bool deleteSelection) = Resolve(CreateServer("focused"), selectionCount);

        Assert.False(handled);
        Assert.False(deleteSelection);
    }

    /// <summary>
    /// Nothing focused is the tree's own selection speaking, and the window-level shortcut reads
    /// that same selection: consuming here would make Delete inert after a click in empty space.
    /// </summary>
    [Fact]
    public void Delete_WithoutFocusedNode_FallsThrough()
    {
        (bool handled, bool deleteSelection) = Resolve(focusedNode: null, selectionCount: 1);

        Assert.False(handled);
        Assert.False(deleteSelection);
    }

    /// <summary>
    /// Ctrl+Del is the window-level shortcut's own gesture and must reach it untouched.
    /// </summary>
    [Theory]
    [InlineData(ModifierKeys.Control)]
    [InlineData(ModifierKeys.Shift)]
    [InlineData(ModifierKeys.Control | ModifierKeys.Shift)]
    public void Delete_WithModifiers_FallsThrough(ModifierKeys modifiers)
    {
        (bool handled, bool deleteSelection) = MainWindow.ResolveTreeDeletion(
            Key.Delete,
            modifiers,
            CreateFolder(),
            selectionCount: 3);

        Assert.False(handled);
        Assert.False(deleteSelection);
    }

    [Theory]
    [InlineData(Key.Enter)]
    [InlineData(Key.F2)]
    [InlineData(Key.Back)]
    public void NonDeleteKeys_FallThrough(Key key)
    {
        (bool handled, bool deleteSelection) = MainWindow.ResolveTreeDeletion(
            key,
            ModifierKeys.None,
            CreateFolder(),
            selectionCount: 3);

        Assert.False(handled);
        Assert.False(deleteSelection);
    }

    private static (bool Handled, bool DeleteSelection) Resolve(
        object? focusedNode,
        int selectionCount)
    {
        return MainWindow.ResolveTreeDeletion(
            Key.Delete,
            ModifierKeys.None,
            focusedNode,
            selectionCount);
    }

    private static FolderViewModel CreateFolder() =>
        new() { Name = "Legacy", FullPath = "Legacy" };

    private static ServerItemViewModel CreateServer(string id)
    {
        return ServerItemViewModel.FromDto(new ServerProfileDto
        {
            Id = id,
            DisplayName = id,
            RemoteServer = $"{id}.example.test",
            ConnectionType = "SSH"
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
