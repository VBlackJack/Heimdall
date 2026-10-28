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

using System.Runtime.InteropServices;
using System.Text;

namespace Heimdall.App.Services;

/// <summary>
/// The clipboard operations of the VNC view, guarded against a clipboard held by another process.
/// </summary>
/// <remarks>
/// The Windows clipboard is one shared resource: while another process holds it, the WPF calls throw
/// a <see cref="COMException"/> (CLIPBRD_E_CANT_OPEN), an <see cref="ExternalException"/>. Left
/// unguarded it surfaces on the UI thread as an unhandled exception. Here it is reported to the
/// caller to log and nothing else happens: no dialog, as in the SSH view.
/// </remarks>
internal static class VncClipboardAccess
{
    /// <summary>The UTF-8 text the page sent as base64, or null when the payload is not valid base64.</summary>
    internal static string? DecodePayload(string base64)
    {
        ArgumentNullException.ThrowIfNull(base64);

        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(base64));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>Writes <paramref name="text"/>; false when the clipboard could not be opened.</summary>
    internal static bool TrySetText(Action<string> setText, string text, Action<Exception> onFailure)
    {
        ArgumentNullException.ThrowIfNull(setText);
        ArgumentNullException.ThrowIfNull(onFailure);

        try
        {
            setText(text);
            return true;
        }
        catch (ExternalException ex)
        {
            onFailure(ex);
            return false;
        }
    }

    /// <summary>The clipboard text, or null when there is none or the clipboard could not be opened.</summary>
    internal static string? TryGetText(Func<bool> containsText, Func<string> getText, Action<Exception> onFailure)
    {
        ArgumentNullException.ThrowIfNull(containsText);
        ArgumentNullException.ThrowIfNull(getText);
        ArgumentNullException.ThrowIfNull(onFailure);

        try
        {
            return containsText() ? getText() : null;
        }
        catch (ExternalException ex)
        {
            onFailure(ex);
            return null;
        }
    }
}
