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

using System;
using System.Collections.Generic;
using Heimdall.Ssh;

namespace Heimdall.App.Services;

/// <summary>
/// Whether a tunnel failure should be reported in full, and how many identical
/// ones were left out of the log since the previous full report.
/// </summary>
/// <param name="ShouldReport">True for the first failure of its kind in the window.</param>
/// <param name="SuppressedRepeats">
/// On a full report, how many identical failures went unreported since the last
/// one. On a suppressed failure, its rank among the repeats of the current window.
/// </param>
internal readonly record struct TunnelFailureReportDecision(bool ShouldReport, int SuppressedRepeats);

/// <summary>
/// Where a tunnel failure diagnosis is written. A full report and a held-back
/// repeat go to different log levels, and a test can observe both.
/// </summary>
/// <param name="fullReport">True for a full report, false for a held-back repeat.</param>
/// <param name="message">The composed diagnosis.</param>
internal delegate void TunnelFailureLogWriter(bool fullReport, string message);

/// <summary>
/// Decides whether a tunnel failure adds anything to the log.
/// <para>
/// Reconnecting a session set dials every profile that shares a gateway. When
/// that gateway is unreachable or refuses authentication, each profile produces
/// the same diagnosis, and the second through tenth copies bury the line that
/// mattered. This class reports the first failure of a given
/// (gateway chain, failure code, message) triple and holds back its identical
/// repeats for a short window. A different gateway, a different failure code,
/// or the same code carrying a different message is a different triple and is
/// always reported - so "identical" is a comparison, not an assumption.
/// </para>
/// <para>
/// This changes what is written to the log, never whether a connection is
/// attempted: it is not a retry policy and not a circuit breaker.
/// </para>
/// </summary>
internal sealed class TunnelFailureLogCoalescer
{
    /// <summary>
    /// How long identical failures stay folded into the first report. A session
    /// set reconnects within a few seconds of itself; this window covers that
    /// burst without hiding a failure the user provoked again later by hand.
    /// </summary>
    internal static readonly TimeSpan DefaultWindow = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Upper bound on tracked pairs, so a long-lived session cannot grow this
    /// map without limit. Reaching it evicts the entries whose window has
    /// already closed, which cannot suppress anything.
    /// </summary>
    private const int MaxTrackedPairs = 64;

    private readonly Dictionary<FailureKey, FailureEntry> _entries = new();
    private readonly object _gate = new();
    private readonly TimeSpan _window;
    private readonly TimeProvider _timeProvider;

    public TunnelFailureLogCoalescer()
        : this(TimeProvider.System, DefaultWindow)
    {
    }

    internal TunnelFailureLogCoalescer(TimeProvider timeProvider, TimeSpan window)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero);

        _timeProvider = timeProvider;
        _window = window;
    }

    /// <summary>
    /// Records a failure and answers whether it should be reported in full.
    /// </summary>
    /// <param name="gatewayChainKey">Identity of the gateway chain that failed.</param>
    /// <param name="failureCode">Classified failure, or null when unclassified.</param>
    /// <param name="message">
    /// The composed message this failure would report. It is part of the key, so
    /// the thing being deduplicated is the thing being compared: ten profiles
    /// failing on one gateway still produce one line because their messages are
    /// byte-identical, while the same gateway failing under the same code with a
    /// different message - a different target host, or a diagnosis that changed
    /// because the user loaded a key between attempts - reopens immediately
    /// instead of being folded into a report that no longer describes it.
    /// </param>
    public TunnelFailureReportDecision Evaluate(
        string? gatewayChainKey,
        SshFailureCode? failureCode,
        string? message = null)
    {
        FailureKey key = new FailureKey(gatewayChainKey ?? string.Empty, failureCode, message ?? string.Empty);
        DateTimeOffset now = _timeProvider.GetUtcNow();

        lock (_gate)
        {
            if (_entries.TryGetValue(key, out FailureEntry entry)
                && now - entry.ReportedAt <= _window)
            {
                int repeat = entry.SuppressedRepeats + 1;
                _entries[key] = entry with { SuppressedRepeats = repeat };
                return new TunnelFailureReportDecision(false, repeat);
            }

            int carried = _entries.TryGetValue(key, out FailureEntry expired)
                ? expired.SuppressedRepeats
                : 0;

            EvictClosedWindowsIfCrowdedUnderLock(key, now);
            _entries[key] = new FailureEntry(now, 0);
            return new TunnelFailureReportDecision(true, carried);
        }
    }

    private void EvictClosedWindowsIfCrowdedUnderLock(FailureKey incoming, DateTimeOffset now)
    {
        if (_entries.Count < MaxTrackedPairs || _entries.ContainsKey(incoming))
        {
            return;
        }

        List<FailureKey> closed = new List<FailureKey>();
        foreach (KeyValuePair<FailureKey, FailureEntry> pair in _entries)
        {
            if (now - pair.Value.ReportedAt > _window)
            {
                closed.Add(pair.Key);
            }
        }

        foreach (FailureKey key in closed)
        {
            _entries.Remove(key);
        }

        // Every tracked window is still open: they are all suppressing, and the
        // map cannot be trimmed without losing a live decision. Start over
        // rather than grow without bound; the cost is one extra full report per
        // pair, never a silenced failure.
        if (_entries.Count >= MaxTrackedPairs)
        {
            _entries.Clear();
        }
    }

    private readonly record struct FailureKey(
        string GatewayChainKey,
        SshFailureCode? FailureCode,
        string Message);

    private readonly record struct FailureEntry(DateTimeOffset ReportedAt, int SuppressedRepeats);
}
