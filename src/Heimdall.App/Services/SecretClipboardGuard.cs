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

namespace Heimdall.App.Services;

/// <summary>
/// Copies a secret to the clipboard and remembers whether it owes the clipboard a clean-up.
/// </summary>
/// <remarks>
/// The view owns the countdown timer; this class owns the decision. A clear only ever removes
/// the value this guard put there: if something else was copied since, the clipboard is left
/// alone. Closing the tool finishes a clear that was still pending, but never starts one when
/// auto-clear was off for the copy.
/// </remarks>
internal sealed class SecretClipboardGuard
{
    private readonly IClipboardService _clipboard;
    private string? _pendingValue;

    public SecretClipboardGuard(IClipboardService clipboard)
    {
        _clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
    }

    /// <summary>True while a copied value is waiting to be cleared.</summary>
    public bool HasPendingClear => _pendingValue is not null;

    /// <summary>
    /// Copies <paramref name="text"/> as a sensitive value. Returns false when the clipboard
    /// could not be opened.
    /// </summary>
    /// <param name="text">The secret to copy.</param>
    /// <param name="armAutoClear">Whether the value is to be cleared later.</param>
    public bool TryCopy(string text, bool armAutoClear)
    {
        ArgumentNullException.ThrowIfNull(text);

        try
        {
            _clipboard.SetSensitiveText(text);
        }
        catch (ExternalException)
        {
            return false;
        }

        _pendingValue = armAutoClear ? text : null;
        return true;
    }

    /// <summary>
    /// Clears the clipboard when it still holds the value this guard put there. Called when the
    /// countdown ends and when the tool is closed.
    /// </summary>
    public void ClearIfStillHeld()
    {
        var pending = _pendingValue;
        _pendingValue = null;
        if (pending is null)
        {
            return;
        }

        try
        {
            if (string.Equals(_clipboard.GetText(), pending, StringComparison.Ordinal))
            {
                _clipboard.Clear();
            }
        }
        catch (ExternalException)
        {
            // The clipboard may be held open by another application; nothing more can be done.
        }
    }

    /// <summary>Finishes a pending clear when the tool is closed.</summary>
    public void Close() => ClearIfStillHeld();
}
