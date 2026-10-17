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

using System.Windows.Input;
using Heimdall.App.Services;

namespace Heimdall.App.Views;

/// <summary>
/// The way out of a session surface hosted in WebView2 (SSH terminal, VNC viewer, web console).
/// Once such a surface has the keyboard, Tab and Shift+Tab belong to the page, so without this
/// a keyboard user could never reach the session header again.
/// </summary>
/// <remarks>
/// The gesture is the one the RDP surface already answers, so one shortcut leaves every kind of
/// session. The SSH and VNC pages catch it themselves and post <see cref="Message"/>; the web
/// console is a page Heimdall does not author, so its view checks the key it receives instead.
/// </remarks>
internal static class WebViewFocusRelease
{
    /// <summary>What the terminal and VNC pages post when the gesture is pressed.</summary>
    public const string Message = "release-focus:";

    /// <summary>Whether a key event is the release gesture, with exactly its modifiers.</summary>
    /// <param name="key">The event's key.</param>
    /// <param name="systemKey">The event's system key, which carries the key when Alt is down.</param>
    /// <param name="modifiers">The modifiers held during the event.</param>
    public static bool IsGesture(Key key, Key systemKey, ModifierKeys modifiers)
    {
        RdpShortcut gesture = RdpDefaultShortcuts.ReleaseFocus;
        Key pressed = key == Key.System ? systemKey : key;
        return pressed == gesture.Key && modifiers == gesture.Modifiers;
    }
}
