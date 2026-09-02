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
using Microsoft.Web.WebView2.Wpf;

namespace Heimdall.App.Services;

/// <summary>
/// Generic keyboard shortcut dispatcher. Hosts (typically a window) register
/// fluent <see cref="Register(Key, ModifierKeys, Action, Func{bool}?)"/> calls
/// at startup and forward their <see cref="UIElement.KeyDown"/> events to
/// <see cref="TryHandle(KeyEventArgs)"/>. The service is intentionally
/// host-agnostic: it does not know about Heimdall commands, view-models or
/// localization.
/// </summary>
/// <remarks>
/// <para>
/// Matching semantics:
/// </para>
/// <list type="bullet">
///   <item>
///     Bindings registered with a non-<see cref="ModifierKeys.None"/> mask
///     match only when <see cref="Keyboard.Modifiers"/> equals the mask
///     exactly (strict equality, not bitwise contains).
///   </item>
///   <item>
///     Bindings registered with <see cref="ModifierKeys.None"/> match on
///     <see cref="Key"/> alone, regardless of which modifiers are held -
///     this preserves the legacy laxist behavior of single-key shortcuts
///     such as F1/F11/Delete/Apps/Escape.
///   </item>
///   <item>
///     Registration order is preserved. On a <see cref="Key"/>+modifiers
///     match the first binding whose optional <c>canExecute</c> predicate
///     returns true (or has no predicate) is invoked.
///   </item>
/// </list>
/// </remarks>
public sealed class KeyboardShortcutService
{
    private readonly List<ShortcutBinding> _bindings = new();

    /// <summary>
    /// Registers a keyboard shortcut. Multiple shortcuts may share the same
    /// (<paramref name="key"/>, <paramref name="modifiers"/>) tuple - on a
    /// match, the first one whose <paramref name="canExecute"/> returns
    /// <c>true</c> (or has no predicate) is invoked.
    /// </summary>
    /// <param name="key">The WPF key (e.g. <see cref="Key.N"/>, <see cref="Key.F11"/>).</param>
    /// <param name="modifiers">
    /// Required modifier mask. Use <see cref="ModifierKeys.None"/> for keys
    /// whose legacy behavior fires regardless of modifier state - the matcher
    /// treats <see cref="ModifierKeys.None"/> as "any modifiers accepted".
    /// </param>
    /// <param name="action">Callback invoked on match.</param>
    /// <param name="canExecute">
    /// Optional predicate. If non-null and returns <c>false</c> the binding
    /// is skipped and matching continues to the next registration.
    /// </param>
    public void Register(
        Key key,
        ModifierKeys modifiers,
        Action action,
        Func<bool>? canExecute = null)
    {
        ArgumentNullException.ThrowIfNull(action);
        _bindings.Add(new ShortcutBinding(key, modifiers, action, canExecute));
    }

    /// <summary>
    /// Processes a <see cref="KeyEventArgs"/>. Returns <c>true</c> if a
    /// shortcut matched and was dispatched (caller should set
    /// <see cref="RoutedEventArgs.Handled"/> to <c>true</c>); <c>false</c>
    /// otherwise.
    /// </summary>
    public bool TryHandle(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        return TryHandle(e.Key, Keyboard.Modifiers);
    }

    internal bool TryHandle(Key key, ModifierKeys modifiers)
    {
        foreach (ShortcutBinding binding in _bindings)
        {
            if (binding.Key != key)
            {
                continue;
            }

            if (binding.Modifiers != ModifierKeys.None && modifiers != binding.Modifiers)
            {
                continue;
            }

            if (binding.CanExecute is not null && !binding.CanExecute())
            {
                continue;
            }

            binding.Action();
            return true;
        }

        return false;
    }

    /// <summary>
    /// Convenience helper: returns <c>true</c> if the focused element (or
    /// the originating event source) is an embedded terminal surface
    /// (<see cref="WebView2"/>) or any non-WebView2 embedded content
    /// reported by <paramref name="isEmbeddedContentFocused"/>. The
    /// <see cref="WebView2"/> check catches WebView2
    /// <c>AcceleratorKeyPressed</c> events that WPF's stale
    /// <see cref="Keyboard.FocusedElement"/> misses (known HwndHost focus
    /// gap).
    /// </summary>
    public static bool IsTerminalFocused(
        object originalSource,
        Func<bool> isEmbeddedContentFocused)
    {
        ArgumentNullException.ThrowIfNull(isEmbeddedContentFocused);
        return isEmbeddedContentFocused() || originalSource is WebView2;
    }

    private sealed record ShortcutBinding(
        Key Key,
        ModifierKeys Modifiers,
        Action Action,
        Func<bool>? CanExecute);
}
