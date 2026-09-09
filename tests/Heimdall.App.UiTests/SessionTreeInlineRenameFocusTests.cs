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

using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Heimdall.App.Behaviors;
using Heimdall.App.ViewModels;
using Heimdall.Core.Configuration;

namespace Heimdall.App.UiTests;

public sealed class SessionTreeInlineRenameFocusTests
{
    [StaTheory]
    [InlineData(Key.Up, -1)]
    [InlineData(Key.Down, 1)]
    public void AltArrow_SystemEvent_ResolvesTheOrderingDirection(Key key, int expected)
    {
        using HwndSource source = new(new HwndSourceParameters("Session tree key test")
        {
            Width = 1,
            Height = 1,
            WindowStyle = 0,
        });
        KeyEventArgs args = new(Keyboard.PrimaryDevice, source, 0, key);
        typeof(KeyEventArgs).GetMethod("MarkSystem", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(args, null);

        Assert.Equal(Key.System, args.Key);
        Assert.Equal(expected, MainWindow.ResolveTreeNudgeDelta(args, ModifierKeys.Alt));
        Assert.Equal(0, MainWindow.ResolveTreeNudgeDelta(args, ModifierKeys.None));
        Assert.Equal(0, MainWindow.ResolveTreeNudgeDelta(args, ModifierKeys.Alt | ModifierKeys.Control));
    }

    [StaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void RenameEditor_DeletePreview_IsNotConsumedByTreeSelection(bool isFolder)
    {
        object node = isFolder
            ? new FolderViewModel { FullPath = "ops", Name = "ops" }
            : new ServerItemViewModel { Id = "server-1", DisplayName = "Server" };
        TextBox editor = new() { DataContext = node, Text = "Original" };
        TreeViewItem row = new() { DataContext = node, Header = editor };
        TreeView tree = new();
        tree.Items.Add(row);
        bool routed = false;
        tree.PreviewKeyDown += (_, e) =>
        {
            routed = true;
            (bool handled, bool delete) = MainWindow.ResolveTreeDeletion(
                e.Key, ModifierKeys.None, node, 3,
                MainWindow.IsInlineRenameEditorSource(e.OriginalSource as DependencyObject));
            Assert.False(delete);
            e.Handled = handled;
        };
        using HwndSource source = new(new HwndSourceParameters("Session tree editor test")
        {
            Width = 320,
            Height = 240,
            WindowStyle = 0,
        });
        source.RootVisual = tree;
        tree.Measure(new Size(320, 240));
        tree.Arrange(new Rect(0, 0, 320, 240));
        tree.UpdateLayout();
        KeyEventArgs args = new(Keyboard.PrimaryDevice, source, 0, Key.Delete)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent,
        };
        editor.RaiseEvent(args);

        Assert.True(routed);
        Assert.False(args.Handled);
    }

    [StaFact]
    public void ActiveEditor_LostKeyboardFocus_RaisesSingleCommitRequest()
    {
        ServerItemViewModel server = ServerItemViewModel.FromDto(new ServerProfileDto
        {
            Id = "server-1",
            DisplayName = "Original",
            RemoteServer = "server-1.example.test",
            ConnectionType = "SSH",
        });
        server.BeginInlineEdit();
        TextBox editor = new()
        {
            DataContext = server,
        };
        int requestCount = 0;
        InlineRenameFocusBehavior.AddCommitRequestedHandler(
            editor,
            (_, _) => requestCount++);
        InlineRenameFocusBehavior.SetCommitOnLostKeyboardFocus(editor, true);

        RaiseLostKeyboardFocus(editor);

        Assert.Equal(1, requestCount);
    }

    [StaFact]
    public void DisabledEditor_LostKeyboardFocus_DoesNotRaiseCommitRequest()
    {
        (ServerItemViewModel server, TextBox editor) = CreateEditor();
        int requestCount = 0;
        InlineRenameFocusBehavior.AddCommitRequestedHandler(
            editor,
            (_, _) => requestCount++);
        InlineRenameFocusBehavior.SetCommitOnLostKeyboardFocus(editor, true);
        editor.IsEnabled = false;

        RaiseLostKeyboardFocus(editor);

        Assert.Equal(0, requestCount);
        Assert.True(server.IsEditing);
    }

    [StaFact]
    public void CancelledEditor_LostKeyboardFocus_DoesNotRaiseCommitRequest()
    {
        (ServerItemViewModel server, TextBox editor) = CreateEditor();
        int requestCount = 0;
        InlineRenameFocusBehavior.AddCommitRequestedHandler(
            editor,
            (_, _) => requestCount++);
        InlineRenameFocusBehavior.SetCommitOnLostKeyboardFocus(editor, true);
        server.CancelInlineEdit();

        RaiseLostKeyboardFocus(editor);

        Assert.Equal(0, requestCount);
        Assert.False(server.IsEditing);
    }

    private static (ServerItemViewModel Server, TextBox Editor) CreateEditor()
    {
        ServerItemViewModel server = ServerItemViewModel.FromDto(new ServerProfileDto
        {
            Id = "server-1",
            DisplayName = "Original",
            RemoteServer = "server-1.example.test",
            ConnectionType = "SSH",
        });
        server.BeginInlineEdit();
        return (server, new TextBox { DataContext = server });
    }

    private static void RaiseLostKeyboardFocus(TextBox editor)
    {
        KeyboardFocusChangedEventArgs eventArgs = new(
            Keyboard.PrimaryDevice,
            Environment.TickCount,
            editor,
            null)
        {
            RoutedEvent = Keyboard.LostKeyboardFocusEvent,
            Source = editor,
        };
        editor.RaiseEvent(eventArgs);
    }
}
