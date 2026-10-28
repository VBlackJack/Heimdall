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

namespace Heimdall.App.ViewModels.Dialogs;

/// <summary>
/// Tells a dialog when to put the keyboard focus back in its password box after a failed attempt.
/// </summary>
/// <remarks>
/// <para>
/// The vault dialogs disable their password box while an attempt runs, and a disabled element
/// gives up the keyboard focus for good: when the attempt failed, the next thing the user types
/// went nowhere. A <c>PasswordBox</c> has no read-only mode to use instead, so the focus has to be
/// put back.
/// </para>
/// <para>
/// Putting it back at the moment the attempt ends is not enough. The unlock gate can stay shut for
/// minutes after a failure (the PIN lockout), and focusing a disabled box does nothing. So the
/// failure arms the signal and the signal fires when the box is enabled again, whichever of the
/// two moments that is.
/// </para>
/// </remarks>
public sealed class PasswordRefocusSignal
{
    private bool _pending;

    /// <summary>Raised once per armed failure, when the password box is enabled again.</summary>
    public event EventHandler? Requested;

    /// <summary>Whether a failure is waiting for the password box to be enabled again.</summary>
    public bool IsPending => _pending;

    /// <summary>Records that the attempt failed and the password box should take the focus back.</summary>
    public void Arm() => _pending = true;

    /// <summary>
    /// Fires <see cref="Requested"/> when a failure is waiting and the password box is enabled;
    /// otherwise leaves the failure waiting for the next call.
    /// </summary>
    /// <param name="passwordBoxEnabled">Whether the password box accepts input right now.</param>
    public void Release(bool passwordBoxEnabled)
    {
        if (!_pending || !passwordBoxEnabled)
        {
            return;
        }

        _pending = false;
        Requested?.Invoke(this, EventArgs.Empty);
    }
}
