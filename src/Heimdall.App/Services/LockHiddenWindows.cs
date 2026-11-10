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

namespace Heimdall.App.Services;

/// <summary>
/// Which windows the workspace lock is holding hidden. Pure bookkeeping: it decides what to hide
/// and what to show again, and the caller does the hiding, so the rules are testable without a
/// window.
/// </summary>
/// <typeparam name="T">The window type.</typeparam>
internal sealed class LockHiddenWindows<T>
    where T : class
{
    private readonly HashSet<T> _hidden = [];

    /// <summary>True from <see cref="BeginLock"/> until <see cref="EndLock"/>.</summary>
    public bool IsLocked { get; private set; }

    /// <summary>
    /// Starts the lock. Returns the windows to hide: the visible ones not already held.
    /// </summary>
    public IReadOnlyList<T> BeginLock(IEnumerable<T> visibleWindows)
    {
        ArgumentNullException.ThrowIfNull(visibleWindows);
        IsLocked = true;
        return [.. visibleWindows.Where(_hidden.Add)];
    }

    /// <summary>
    /// Ends the lock. Returns the held windows that are still open, to show again; a window that
    /// closed meanwhile is dropped, because showing a closed window throws.
    /// </summary>
    public IReadOnlyList<T> EndLock(IEnumerable<T> openWindows)
    {
        ArgumentNullException.ThrowIfNull(openWindows);
        IsLocked = false;
        HashSet<T> open = [.. openWindows];
        List<T> toShow = [.. _hidden.Where(open.Contains)];
        _hidden.Clear();
        return toShow;
    }

    /// <summary>
    /// Registers a window that has just been opened. While locked it must not stay visible: it is
    /// held, and this returns true so the caller hides it. Otherwise false.
    /// </summary>
    public bool RegisterNewWindow(T window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return IsLocked && _hidden.Add(window);
    }

    /// <summary>Whether the lock is holding <paramref name="window"/> hidden.</summary>
    public bool IsHeld(T window) => _hidden.Contains(window);

    /// <summary>Forgets a window that closed.</summary>
    public void Forget(T window) => _hidden.Remove(window);
}
