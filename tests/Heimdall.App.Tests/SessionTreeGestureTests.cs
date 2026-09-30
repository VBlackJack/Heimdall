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

using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Heimdall.App.Services;
using Heimdall.App.ViewModels;
using Heimdall.Core.Localization;

namespace Heimdall.App.Tests;

/// <summary>
/// The session tree's pointer and keyboard gestures, driven through a real TreeView or TextBox
/// on an STA thread as far as a test host lets an input go: a focus call that WPF answers with
/// its own selection change, a key event raised on the control the window wires.
/// </summary>
public sealed class SessionTreeGestureTests
{
    /// <summary>
    /// The window's selection-changed handler in miniature: an unsuppressed change collapses the
    /// selection onto the row WPF selected, which is what a right-click did to a multi-selection.
    /// </summary>
    private static void MirrorTheWindowSync(TreeView tree, TreeInteractionState treeState, Action collapse)
    {
        tree.SelectedItemChanged += (_, _) =>
        {
            if (!treeState.SuppressSelectedItemSync)
            {
                collapse();
            }

            treeState.SuppressSelectedItemSync = false;
        };
    }

    [Fact]
    public void RightClickOnARowOfTheMultiSelection_FocusesItWithoutCollapsingTheSelection()
    {
        RunOnSta(() =>
        {
            TreeInteractionState treeState = new();
            TreeViewItem primary = new() { Header = "Primary" };
            TreeViewItem pressed = new() { Header = "Also selected, not focused" };
            (Window window, TreeView tree) = ShowTree(primary, pressed);
            int collapses = 0;
            MirrorTheWindowSync(tree, treeState, () => collapses++);

            try
            {
                primary.IsSelected = true;
                DrainDispatcher(window.Dispatcher);
                collapses = 0;

                MainWindow.FocusRowForContextMenu(treeState, pressed, opensBulkMenu: true);
                DrainDispatcher(window.Dispatcher);

                Assert.True(pressed.IsFocused);
                Assert.Equal(0, collapses);
                Assert.False(treeState.SuppressSelectedItemSync);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void RightClickOutsideAMultiSelection_StillSelectsThePressedRow()
    {
        RunOnSta(() =>
        {
            TreeInteractionState treeState = new();
            TreeViewItem other = new() { Header = "Other" };
            TreeViewItem pressed = new() { Header = "Pressed" };
            (Window window, TreeView tree) = ShowTree(other, pressed);
            int collapses = 0;
            MirrorTheWindowSync(tree, treeState, () => collapses++);

            try
            {
                MainWindow.FocusRowForContextMenu(treeState, pressed, opensBulkMenu: false);
                DrainDispatcher(window.Dispatcher);

                Assert.True(pressed.IsFocused);
                Assert.Equal(1, collapses);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void RangePressOnTheRowThatAlreadyHasFocus_LeavesNoSuppressedSyncBehind()
    {
        RunOnSta(() =>
        {
            TreeInteractionState treeState = new();
            TreeViewItem anchor = new() { Header = "Anchor" };
            TreeViewItem pressed = new() { Header = "Pressed" };
            (Window window, TreeView tree) = ShowTree(anchor, pressed);
            int collapses = 0;
            MirrorTheWindowSync(tree, treeState, () => collapses++);

            try
            {
                pressed.Focus();
                DrainDispatcher(window.Dispatcher);
                int ranges = 0;

                MainWindow.ApplyTreeRangePress(treeState, pressed, () => ranges++);
                DrainDispatcher(window.Dispatcher);

                // Focus did not move, so WPF raised no selection change to reset the flag: a
                // leaked flag would swallow the next genuine click's selection.
                Assert.Equal(1, ranges);
                Assert.False(treeState.SuppressSelectedItemSync);

                collapses = 0;
                anchor.IsSelected = true;
                DrainDispatcher(window.Dispatcher);
                Assert.Equal(1, collapses);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Theory]
    [InlineData(Key.Escape, true, 5, "Clear")]
    [InlineData(Key.Escape, false, 5, "None")]
    [InlineData(Key.Down, true, 3, "FocusFirstSession")]
    [InlineData(Key.Down, false, 3, "FocusFirstSession")]
    [InlineData(Key.Down, true, 0, "None")]
    [InlineData(Key.Enter, true, 1, "ConnectOnlyMatch")]
    [InlineData(Key.Enter, true, 2, "None")]
    [InlineData(Key.Enter, false, 1, "None")]
    [InlineData(Key.A, true, 1, "None")]
    public void FilterBoxGesture_Resolves(Key key, bool hasQuery, int matches, string expected)
    {
        Assert.Equal(expected, MainWindow.ResolveSessionFilterGesture(key, ModifierKeys.None, hasQuery, matches).ToString());
    }

    [Fact]
    public void FilterBoxGesture_IgnoresModifiedKeys()
    {
        Assert.Equal(
            "None",
            MainWindow.ResolveSessionFilterGesture(Key.Enter, ModifierKeys.Control, true, 1).ToString());
    }

    [Fact]
    public void FilterBoxKeys_RaisedOnTheBox_ClearMoveAndConnect()
    {
        RunOnSta(() =>
        {
            TextBox box = new() { Text = "web" };
            Window window = new()
            {
                Content = box,
                Width = 320,
                Height = 120,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.ToolWindow
            };
            window.Show();
            window.UpdateLayout();
            int matches = 1;
            int pendingApplied = 0;
            int cleared = 0;
            int focused = 0;
            int connected = 0;
            MainWindow.AttachSessionFilterKeys(box, new MainWindow.SessionFilterKeyTarget(
                MatchCount: () => matches,
                ApplyPendingFilter: () => pendingApplied++,
                Clear: () => cleared++,
                FocusFirstSession: () =>
                {
                    focused++;
                    return true;
                },
                ConnectOnlyMatch: () => connected++));

            try
            {
                Assert.True(RaiseKey(box, Key.Enter));
                Assert.Equal(1, pendingApplied);
                Assert.Equal(1, connected);

                matches = 2;
                Assert.False(RaiseKey(box, Key.Enter));
                Assert.Equal(1, connected);

                Assert.True(RaiseKey(box, Key.Down));
                Assert.Equal(1, focused);

                Assert.True(RaiseKey(box, Key.Escape));
                Assert.Equal(1, cleared);

                box.Text = "";
                Assert.False(RaiseKey(box, Key.Escape));
                Assert.Equal(1, cleared);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task DeleteKeyOnAFolder_SaysWhereFolderDeletionLives()
    {
        var localizer = new LocalizationManager();
        await localizer.LoadAsync(System.IO.Path.Combine(AppContext.BaseDirectory, "locales"), "en");
        FolderViewModel folder = new() { Name = "Ops", FullPath = "Ops" };

        string? refusal = MainWindow.DescribeRefusedTreeDeletion(folder, key => localizer[key]);

        Assert.NotNull(refusal);
        Assert.Contains(localizer["TreeCtxDeleteGroup"], refusal, StringComparison.Ordinal);
        Assert.Null(MainWindow.DescribeRefusedTreeDeletion(null, key => localizer[key]));
    }

    private static bool RaiseKey(UIElement target, Key key)
    {
        PresentationSource source = PresentationSource.FromVisual(target)
            ?? throw new InvalidOperationException("The target is not in a shown window.");
        KeyEventArgs args = new(Keyboard.PrimaryDevice, source, 0, key)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent
        };
        target.RaiseEvent(args);
        return args.Handled;
    }

    private static (Window Window, TreeView Tree) ShowTree(params TreeViewItem[] items)
    {
        TreeView tree = new();
        foreach (TreeViewItem item in items)
        {
            tree.Items.Add(item);
        }

        Window window = new()
        {
            Content = tree,
            Width = 320,
            Height = 240,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.ToolWindow
        };
        window.Show();
        window.UpdateLayout();
        return (window, tree);
    }

    private static void DrainDispatcher(Dispatcher dispatcher)
    {
        dispatcher.Invoke(
            static () => { },
            DispatcherPriority.ApplicationIdle);
    }

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        Thread thread = new(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
