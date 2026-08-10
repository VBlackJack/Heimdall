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
using System.Windows.Controls;
using System.Windows.Input;
using Heimdall.App.Behaviors;
using Heimdall.App.ViewModels;
using Heimdall.Core.Configuration;

namespace Heimdall.App.UiTests;

public sealed class SessionTreeInlineRenameFocusTests
{
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
