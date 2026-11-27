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

namespace Heimdall.App.Views.EmbeddedRdp;

/// <summary>
/// Decides whether the Win32 keyboard focus sits inside the RDP host window. Pure, so the
/// decision is testable without a live ActiveX control.
/// </summary>
/// <remarks>
/// A focused control reparented out of the main window drags the focus with it and activates
/// WinForms' parking window. The view uses this decision to move the focus back to its own
/// window before the control is detached.
/// </remarks>
internal static class RdpHostFocusPolicy
{
    /// <summary>
    /// True when <paramref name="focusHandle"/> is the host window or one of its descendants.
    /// </summary>
    /// <param name="focusHandle">The window that holds the Win32 focus, or zero when none does.</param>
    /// <param name="hostHandle">The host window, or zero when its handle is not created.</param>
    /// <param name="isDescendant">Tells whether the second window descends from the first.</param>
    internal static bool IsFocusInsideHost(
        IntPtr focusHandle,
        IntPtr hostHandle,
        Func<IntPtr, IntPtr, bool> isDescendant)
    {
        if (focusHandle == IntPtr.Zero || hostHandle == IntPtr.Zero)
        {
            return false;
        }

        return focusHandle == hostHandle || isDescendant(hostHandle, focusHandle);
    }
}
