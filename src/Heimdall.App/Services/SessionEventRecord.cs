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
/// The kind of session lifecycle event recorded by <see cref="ISessionEventLog"/>.
/// Serialized to its name ("Connected" / "Disconnected") in the NDJSON event log.
/// </summary>
public enum SessionEventKind
{
    /// <summary>The graphical session reached a connected state.</summary>
    Connected,

    /// <summary>The graphical session ended (remote drop, user action, or tab teardown).</summary>
    Disconnected
}

/// <summary>
/// Immutable description of a single graphical-protocol session lifecycle event
/// (one connect or one disconnect). Construct exclusively through the
/// <see cref="Connected"/> / <see cref="Disconnected"/> factories so the
/// connect-only and disconnect-only fields can never be set in incoherent combinations.
/// </summary>
/// <remarks>
/// <see cref="Host"/> is expected to be pre-sanitized by the caller; the event-log writer
/// additionally strips any leading "user@" prefix as defense in depth before persisting it.
/// This record never carries credentials.
/// </remarks>
public sealed record SessionEventRecord
{
    private SessionEventRecord(
        DateTime timestampUtc,
        string protocol,
        SessionEventKind kind,
        string host,
        string? title,
        string? reasonKey,
        int? reasonCode,
        long? durationMs,
        string? endTrigger,
        int? extendedReasonCode)
    {
        TimestampUtc = timestampUtc;
        Protocol = protocol;
        Kind = kind;
        Host = host;
        Title = title;
        ReasonKey = reasonKey;
        ReasonCode = reasonCode;
        DurationMs = durationMs;
        EndTrigger = endTrigger;
        ExtendedReasonCode = extendedReasonCode;
    }

    /// <summary>UTC instant the event occurred.</summary>
    public DateTime TimestampUtc { get; }

    /// <summary>Protocol label: "RDP", "VNC", or "CITRIX".</summary>
    public string Protocol { get; }

    /// <summary>Whether this event is a connect or a disconnect.</summary>
    public SessionEventKind Kind { get; }

    /// <summary>Target host or display host (pre-sanitized; "user@" stripped at write time).</summary>
    public string Host { get; }

    /// <summary>Human-readable session title, if available.</summary>
    public string? Title { get; }

    /// <summary>Symbolic RDP disconnect reason key; null for connects and for VNC/Citrix.</summary>
    public string? ReasonKey { get; }

    /// <summary>Numeric RDP disconnect code; null for connects and for VNC/Citrix.</summary>
    public int? ReasonCode { get; }

    /// <summary>Session duration in milliseconds; set on disconnect only, null on connect.</summary>
    public long? DurationMs { get; }

    /// <summary>End trigger for a disconnect: "remote", "user", or "teardown"; null on connect.</summary>
    public string? EndTrigger { get; }

    /// <summary>
    /// Numeric RDP extended disconnect code; null for connects and for VNC/Citrix.
    /// </summary>
    /// <remarks>
    /// Recorded because the reason key is resolved from BOTH codes, so the key beside a primary
    /// number alone could not always be re-derived by a reader. With both present, the line
    /// explains itself without knowing which decoder won.
    /// </remarks>
    public int? ExtendedReasonCode { get; }

    /// <summary>
    /// Builds a connect event. Reason and duration fields are intentionally unset.
    /// </summary>
    /// <param name="protocol">Protocol label ("RDP" / "VNC" / "CITRIX").</param>
    /// <param name="host">Target host (pre-sanitized).</param>
    /// <param name="title">Optional human-readable session title.</param>
    public static SessionEventRecord Connected(string protocol, string host, string? title)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(protocol);
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        return new SessionEventRecord(
            DateTime.UtcNow,
            protocol,
            SessionEventKind.Connected,
            host,
            title,
            reasonKey: null,
            reasonCode: null,
            durationMs: null,
            endTrigger: null,
            extendedReasonCode: null);
    }

    /// <summary>
    /// Builds a disconnect event. Reason/code/duration/trigger are all optional so VNC and
    /// Citrix (which have no reason code) can omit them.
    /// </summary>
    /// <param name="protocol">Protocol label ("RDP" / "VNC" / "CITRIX").</param>
    /// <param name="host">Target host (pre-sanitized).</param>
    /// <param name="title">Optional human-readable session title.</param>
    /// <param name="reasonKey">Symbolic RDP disconnect key, or null.</param>
    /// <param name="reasonCode">Numeric RDP disconnect code, or null.</param>
    /// <param name="durationMs">Session duration in milliseconds; negatives are clamped to zero.</param>
    /// <param name="endTrigger">"remote", "user", or "teardown", or null.</param>
    public static SessionEventRecord Disconnected(
        string protocol,
        string host,
        string? title,
        string? reasonKey = null,
        int? reasonCode = null,
        long? durationMs = null,
        string? endTrigger = null,
        int? extendedReasonCode = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(protocol);
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        if (durationMs is < 0)
        {
            durationMs = 0;
        }

        return new SessionEventRecord(
            DateTime.UtcNow,
            protocol,
            SessionEventKind.Disconnected,
            host,
            title,
            reasonKey,
            reasonCode,
            durationMs,
            endTrigger,
            extendedReasonCode);
    }
}
