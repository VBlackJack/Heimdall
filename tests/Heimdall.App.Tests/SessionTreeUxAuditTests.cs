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
using System.Text.RegularExpressions;
using Heimdall.App.Services;
using Heimdall.App.ViewModels;
using Heimdall.Core.Configuration;

namespace Heimdall.App.Tests;

/// <summary>
/// Guards for the sessions tree's interaction polish: type-ahead, drag scrolling, and the markup
/// that keeps a selected folder, the focus ring and the drop hint visible.
/// </summary>
public sealed class SessionTreeUxAuditTests
{
    private static readonly string[] Names = ["Alpha", "Beta", "Bravo", "Charlie"];

    [Fact]
    public void TypeAhead_OneLetterCyclesThroughTheRowsThatStartWithIt()
    {
        Assert.Equal(1, TreeTypeAhead.FindMatch(Names, -1, "b"));
        Assert.Equal(2, TreeTypeAhead.FindMatch(Names, 1, "b"));
        Assert.Equal(1, TreeTypeAhead.FindMatch(Names, 2, "b"));
    }

    [Fact]
    public void TypeAhead_ALongerPrefixStaysOnTheCurrentRowWhileItMatches()
    {
        Assert.Equal(2, TreeTypeAhead.FindMatch(Names, 2, "br"));
        Assert.Equal(2, TreeTypeAhead.FindMatch(Names, 0, "br"));
    }

    [Fact]
    public void TypeAhead_RepeatedLetterCyclesInsteadOfSearchingForTheDoubleLetter()
    {
        Assert.Equal(2, TreeTypeAhead.FindMatch(Names, 1, "bb"));
    }

    [Fact]
    public void TypeAhead_NoMatchAnswersMinusOne()
    {
        Assert.Equal(-1, TreeTypeAhead.FindMatch(Names, 0, "z"));
        Assert.Equal(-1, TreeTypeAhead.FindMatch([], 0, "a"));
        Assert.Equal(-1, TreeTypeAhead.FindMatch(Names, 0, ""));
    }

    [Fact]
    public void TypeAhead_APauseStartsANewPrefix()
    {
        TreeTypeAhead state = new();
        Assert.Equal("b", state.Append('b', 0));
        Assert.Equal("br", state.Append('r', 500));
        Assert.Equal("c", state.Append('c', 500 + (long)TreeTypeAhead.ResetDelay.TotalMilliseconds + 1));
    }

    [Fact]
    public void TypeAhead_IgnoresControlCharactersAndALeadingSpace()
    {
        TreeTypeAhead state = new();
        Assert.Equal("", state.Append('\u001b', 0));
        Assert.Equal("", state.Append(' ', 0));
        Assert.Equal("a", state.Append('a', 0));
        Assert.Equal("a ", state.Append(' ', 10));
    }

    [Theory]
    [InlineData(-1, 400, 0)]
    [InlineData(200, 400, 0)]
    [InlineData(27, 400, -1)]
    [InlineData(0, 400, -4)]
    [InlineData(373, 400, 1)]
    [InlineData(400, 400, 4)]
    [InlineData(401, 400, 0)]
    public void DragScroll_SpeedsUpTowardTheBorder(double y, double height, int expected)
    {
        Assert.Equal(expected, MainWindow.ResolveTreeDragScrollLines(y, height));
    }

    [Fact]
    public void VisibleNodes_ListFoldersAndOnlyTheChildrenOfExpandedOnes()
    {
        FolderViewModel open = new() { Name = "Open", IsExpanded = true };
        FolderViewModel closed = new() { Name = "Closed", IsExpanded = false };
        FolderViewModel inner = new() { Name = "Inner", IsExpanded = false };
        object leaf = new object();
        open.Children.Add(inner);
        open.Children.Add(leaf);
        closed.Children.Add(new object());

        List<object> nodes = SelectionHelpers.EnumerateVisibleNodes([open, closed]).ToList();

        Assert.Equal([open, inner, leaf, closed], nodes);
    }

    [Fact]
    public void FolderCursor_MarksTheFolderThatTakesFocus()
    {
        TreeInteractionState state = new();
        FolderViewModel folder = new() { Name = "Test" };

        state.MoveFolderCursor(folder);

        Assert.True(folder.IsTreeCursor);
        Assert.Same(folder, state.CursorFolder);
    }

    [Fact]
    public void FolderCursor_StaysWhenFocusLandsOutsideAnyRow()
    {
        TreeInteractionState state = new();
        FolderViewModel folder = new() { Name = "Test" };
        state.MoveFolderCursor(folder);

        state.MoveFolderCursor(null);

        Assert.True(folder.IsTreeCursor);
        Assert.Same(folder, state.CursorFolder);
    }

    [Fact]
    public void FolderCursor_MovesFromOneFolderToTheNext()
    {
        TreeInteractionState state = new();
        FolderViewModel first = new() { Name = "First" };
        FolderViewModel second = new() { Name = "Second" };
        state.MoveFolderCursor(first);

        state.MoveFolderCursor(second);

        Assert.False(first.IsTreeCursor);
        Assert.True(second.IsTreeCursor);
        Assert.Same(second, state.CursorFolder);
    }

    [Fact]
    public void FolderCursor_LeavesWhenASessionRowTakesFocus()
    {
        TreeInteractionState state = new();
        FolderViewModel folder = new() { Name = "Test" };
        state.MoveFolderCursor(folder);

        state.MoveFolderCursor(new ServerItemViewModel());

        Assert.False(folder.IsTreeCursor);
        Assert.Null(state.CursorFolder);
    }

    /// <summary>
    /// The folder row used to paint itself from its container's native selection, which
    /// OnTreeViewSelectedItemChanged always clears for a folder: the trigger could never fire.
    /// </summary>
    [Fact]
    public void TheFolderRowPaintsTheCursorMarkFromTheViewModel()
    {
        string folder = MainWindowMarkup.Block(
            "<HierarchicalDataTemplate DataType=\"{x:Type vm:FolderViewModel}\"",
            "</HierarchicalDataTemplate>");

        Assert.Contains("<DataTrigger Binding=\"{Binding IsTreeCursor}\" Value=\"True\">", folder, StringComparison.Ordinal);
        Assert.Contains("TargetName=\"FolderSelectionChrome\"", folder, StringComparison.Ordinal);
        Assert.DoesNotContain("AncestorType={x:Type TreeViewItem}", folder, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTreeMovesTheFolderCursorOnEveryFocusChange()
    {
        string tree = MainWindowMarkup.Block("<controls:SessionTreeView x:Name=\"SessionTreeView\"", ">");

        Assert.Contains("GotKeyboardFocus=\"OnSessionTreeViewGotKeyboardFocus\"", tree, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDropHintDoesNotSitOnTheBarsBelowTheTree()
    {
        string popup = MainWindowMarkup.Block("<Popup x:Name=\"TreeDropHint\"", ">");

        Assert.DoesNotContain("Placement=\"Bottom\"", popup, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFocusRingIsAtLeastTwoPixelsWide()
    {
        string tokens = File.ReadAllText(Path.Combine(
            SettingsNumericFields.FindRepoRoot(), "src", "Heimdall.App", "Themes", "CommonControls.xaml"));

        Match ring = Regex.Match(tokens, "x:Key=\"SessionTreeRowOutlineThickness\">([0-9.]+)<");
        Assert.True(ring.Success, "the row outline token is gone");
        Assert.True(double.Parse(ring.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) >= 2);
    }

    [Fact]
    public void TheTreeKeepsASingleItemTemplate()
    {
        string tree = MainWindowMarkup.Block(
            "<TreeView.ItemContainerStyle>",
            "</TreeView.ItemContainerStyle>");

        Assert.Single(Regex.Matches(tree, "<ControlTemplate TargetType=\"\\{x:Type TreeViewItem\\}\">"));
    }
}
