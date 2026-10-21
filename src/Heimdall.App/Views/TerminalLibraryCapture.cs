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

using System.Text;
using System.Windows.Automation;
using System.Windows.Controls;

namespace Heimdall.App.Views;

/// <summary>What the terminal page asks of the host about the Command Library.</summary>
internal enum TerminalLibraryRequest
{
    /// <summary>Nothing to do: the message was malformed, too large, or carried no text to save.</summary>
    None,

    /// <summary>Save the carried selection (Ctrl+Shift+L).</summary>
    Save,

    /// <summary>Show the host's menu for the carried selection (Shift+right-click).</summary>
    Menu,
}

/// <summary>
/// The host's half of "save the terminal selection to the Command Library": the two messages
/// the page posts and the one-item menu the host shows.
/// </summary>
/// <remarks>
/// <para>Both gestures end in <see cref="EmbeddedSshView.SaveToLibraryRequested"/>. The key
/// chord saves at once; Shift+right-click opens a menu whose single entry is enabled only when
/// the page reported a selection. A plain right-click keeps pasting, as it always has.</para>
/// <para>The menu is a WPF one rather than an entry added through WebView2's
/// <c>ContextMenuRequested</c>: the page cancels every <c>contextmenu</c> event to keep
/// right-click paste, and the default menus are disabled, so that event never reaches the
/// host. The page sends the selection with the request, so the menu never reads a stale
/// one.</para>
/// </remarks>
internal static class TerminalLibraryCapture
{
    /// <summary>Inbound message: <c>library-save:&lt;base64 UTF-8 selection&gt;</c>.</summary>
    public const string SaveMessagePrefix = "library-save:";

    /// <summary>Inbound message: <c>library-menu:&lt;base64 UTF-8 selection, possibly empty&gt;</c>.</summary>
    public const string MenuMessagePrefix = "library-menu:";

    /// <summary>Locale key of the menu entry.</summary>
    public const string MenuItemLabelKey = "TerminalSaveSelectionToLibrary";

    /// <summary>
    /// Reads a page message addressed to the library.
    /// </summary>
    /// <param name="message">The page message.</param>
    /// <param name="request">What the host should do; <see cref="TerminalLibraryRequest.None"/> when nothing.</param>
    /// <param name="selection">The decoded selection; empty unless <paramref name="request"/> carries one.</param>
    /// <returns>
    /// <c>true</c> when the message belongs to the library, whether or not it asks for anything;
    /// <c>false</c> when it is some other message.
    /// </returns>
    public static bool TryRead(string message, out TerminalLibraryRequest request, out string selection)
    {
        ArgumentNullException.ThrowIfNull(message);

        request = TerminalLibraryRequest.None;
        selection = string.Empty;

        TerminalLibraryRequest kind;
        string payload;
        if (message.StartsWith(SaveMessagePrefix, StringComparison.Ordinal))
        {
            kind = TerminalLibraryRequest.Save;
            payload = message[SaveMessagePrefix.Length..];
        }
        else if (message.StartsWith(MenuMessagePrefix, StringComparison.Ordinal))
        {
            kind = TerminalLibraryRequest.Menu;
            payload = message[MenuMessagePrefix.Length..];
        }
        else
        {
            return false;
        }

        if (!EmbeddedSshView.IsInboundWebMessageBase64PayloadWithinLimit(payload)
            || !TryDecode(payload, out string decoded))
        {
            return true;
        }

        // A save with nothing to save is no request; a menu still opens, with its entry disabled.
        if (kind == TerminalLibraryRequest.Save && string.IsNullOrWhiteSpace(decoded))
        {
            return true;
        }

        request = kind;
        selection = decoded;
        return true;
    }

    /// <summary>
    /// The host's menu for a selection: one entry, enabled only when there is text to save.
    /// </summary>
    /// <param name="selection">The selection the page reported with the request.</param>
    /// <param name="label">The entry's localized label.</param>
    /// <param name="save">What choosing the entry does with the selection.</param>
    public static ContextMenu CreateMenu(string selection, string label, Action<string> save)
    {
        ArgumentNullException.ThrowIfNull(save);

        var item = new MenuItem
        {
            Header = label,
            IsEnabled = !string.IsNullOrWhiteSpace(selection),
        };
        AutomationProperties.SetName(item, label);
        item.Click += (_, _) => save(selection);

        var menu = new ContextMenu();
        menu.Items.Add(item);
        return menu;
    }

    private static bool TryDecode(string base64, out string text)
    {
        try
        {
            // The page posts one complete UTF-8 message; no stateful decoder is needed.
            text = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
            return true;
        }
        catch (FormatException)
        {
            text = string.Empty;
            return false;
        }
    }
}
