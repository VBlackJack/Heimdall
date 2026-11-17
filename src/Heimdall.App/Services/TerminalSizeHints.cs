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
/// A host that knows the size its terminal page last reported. Lets the code that tears a view down
/// ask the size without depending on a WebView2 control.
/// </summary>
internal interface ITerminalSizeSource
{
    /// <summary>The last size the page reported, or <see langword="null"/> before it has spoken.</summary>
    TerminalSize? LastKnownTerminalSize { get; }
}

/// <summary>
/// The sizes a connect with no measurable view can fall back on: what the window last showed, and
/// what the terminal that held a given pane or tab showed before it was replaced.
/// </summary>
/// <remarks>
/// <para>This is not a second source of truth. The view's <see cref="TerminalSizeReport"/> keeps the
/// size its own page reported; this class only keeps what outlives the view (a reconnect disposes
/// the old one before the new connect asks) and what no single view knows (the window's latest).</para>
/// <para>Written on the UI thread, read from the connect path on a pool thread.</para>
/// </remarks>
internal sealed class TerminalSizeHints
{
    /// <summary>
    /// More remembered sessions than a user keeps open. Past it the oldest entry is evicted to make
    /// room: a missing hint costs a worse guess, never a failure.
    /// </summary>
    internal const int MaxRememberedSessions = 256;

    /// <summary>
    /// The narrowest terminal worth remembering as a hint. A container shrinking to nothing makes
    /// the fit addon report a few cells, and sizes pass through such values during a split or a
    /// splitter drag. A pipe session keeps its launch size for good, so a degenerate hint is worse
    /// than the default: the default is at least a usable line.
    /// </summary>
    internal const int MinimumHintColumns = 20;

    /// <summary>The shortest terminal worth remembering as a hint; see <see cref="MinimumHintColumns"/>.</summary>
    internal const int MinimumHintRows = 5;

    private readonly object _gate = new();
    private readonly Dictionary<string, (TerminalSize Size, long Sequence)> _previousBySession =
        new(StringComparer.Ordinal);

    private long _sequence;
    private TerminalSize? _window;

    /// <summary>The size most recently reported by any terminal view of the window.</summary>
    public TerminalSize? Window => Volatile.Read(ref _window);

    /// <summary>Whether a size is large enough to be kept as a hint.</summary>
    public static bool IsUsableHint(TerminalSize size)
    {
        ArgumentNullException.ThrowIfNull(size);
        return size.Columns >= MinimumHintColumns && size.Rows >= MinimumHintRows;
    }

    /// <summary>Notes a size reported by a view; the latest usable report wins.</summary>
    public void RememberWindow(TerminalSize size)
    {
        if (IsUsableHint(size))
        {
            Volatile.Write(ref _window, size);
        }
    }

    /// <summary>
    /// Keeps the size the terminal of a pane or tab last showed, under the key the next connect of
    /// that pane or tab will ask with. A size below the minimum is not kept.
    /// </summary>
    public void RememberPrevious(string sessionKey, TerminalSize size)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionKey);
        if (!IsUsableHint(size))
        {
            return;
        }

        lock (_gate)
        {
            Store(sessionKey, size);
        }
    }

    /// <summary>
    /// Hands the size remembered under <paramref name="fromKey"/> to <paramref name="toKey"/> and
    /// forgets the first, so a size left for a reconnect is used by that reconnect only.
    /// </summary>
    public void MovePrevious(string fromKey, string toKey)
    {
        if (string.IsNullOrEmpty(fromKey) || string.IsNullOrEmpty(toKey))
        {
            return;
        }

        lock (_gate)
        {
            if (_previousBySession.Remove(fromKey, out (TerminalSize Size, long Sequence) moved))
            {
                Store(toKey, moved.Size);
            }
        }
    }

    /// <summary>The size remembered for a session key, or <see langword="null"/>.</summary>
    public TerminalSize? PreviousFor(string sessionKey)
    {
        lock (_gate)
        {
            return _previousBySession.TryGetValue(sessionKey, out (TerminalSize Size, long Sequence) entry)
                ? entry.Size
                : null;
        }
    }

    /// <summary>
    /// The size remembered for a session key, forgotten as it is read: a hint serves the connect it
    /// was left for and no other.
    /// </summary>
    public TerminalSize? TakePrevious(string sessionKey)
    {
        lock (_gate)
        {
            return _previousBySession.Remove(sessionKey, out (TerminalSize Size, long Sequence) entry)
                ? entry.Size
                : null;
        }
    }

    /// <summary>Drops whatever is remembered for a session that closed or whose reconnect ended.</summary>
    public void Forget(string sessionKey)
    {
        if (string.IsNullOrEmpty(sessionKey))
        {
            return;
        }

        lock (_gate)
        {
            _previousBySession.Remove(sessionKey);
        }
    }

    private void Store(string sessionKey, TerminalSize size)
    {
        if (!_previousBySession.ContainsKey(sessionKey) && _previousBySession.Count >= MaxRememberedSessions)
        {
            string oldest = _previousBySession.MinBy(entry => entry.Value.Sequence).Key;
            _previousBySession.Remove(oldest);
        }

        _previousBySession[sessionKey] = (size, ++_sequence);
    }
}
