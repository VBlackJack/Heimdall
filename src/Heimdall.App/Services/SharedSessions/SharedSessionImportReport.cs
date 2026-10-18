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

using Heimdall.Core.Configuration;

namespace Heimdall.App.Services.SharedSessions;

/// <summary>What an entry of a shared source's import report found.</summary>
/// <remarks>The order is the order the report lists the checks in.</remarks>
public enum SharedSessionReportCheck
{
    /// <summary>A session of the file was not loaded; the entry says why.</summary>
    SkippedSession,

    /// <summary>A <c>[NAME]</c> variable has no value; the session was loaded with the token as written.</summary>
    UnresolvedVariable,

    /// <summary>The session's name is not its host's.</summary>
    NameHostMismatch,

    /// <summary>The session does not go through the gateway most sessions of its folder go through.</summary>
    GatewayOutlier,

    /// <summary>Another session of the same folder reaches the same host.</summary>
    DuplicateHost,
}

/// <summary>One finding of an import report.</summary>
/// <param name="Check">What was found.</param>
/// <param name="FolderPath">The folder of the session in the file, slash-separated; empty at the top.</param>
/// <param name="SessionName">The session's name, as written in the file.</param>
/// <param name="Detail">
/// The value the finding is about: the host, the variable, the gateway the session uses, or the
/// value the parser refused. Empty when there is none.
/// </param>
/// <param name="Context">
/// What the finding is measured against, when there is something: the folder's usual gateway for an
/// outlier, the other sessions on the same host for a duplicate. Empty otherwise.
/// </param>
/// <param name="SkipReason">Why the parser skipped a session; null when the shared filter refused it.</param>
/// <param name="Refusal">Why the shared filter refused a session; null when the parser skipped it.</param>
public sealed record SharedSessionReportEntry(
    SharedSessionReportCheck Check,
    string FolderPath,
    string SessionName,
    string Detail,
    string Context = "",
    MobaXtermImportWarningKind? SkipReason = null,
    SharedProfileRefusal? Refusal = null);

/// <summary>A session of the file the shared filter refused, and why.</summary>
/// <param name="Profile">The session as the parser read it.</param>
/// <param name="Reason">Why it was refused.</param>
/// <param name="Detail">The value refused: the protocol, the host or the gateway.</param>
public sealed record SharedRefusedSession(ServerProfileDto Profile, SharedProfileRefusal Reason, string Detail);

/// <summary>
/// What a shared source's file holds that its maintainers may want to fix, as of its last
/// successful read. Informational: nothing in it stops a session from loading or changes one.
/// </summary>
/// <param name="SourceId">The source the report is about.</param>
/// <param name="SourceName">The source's name.</param>
/// <param name="Entries">The findings, in check order then folder then session.</param>
public sealed record SharedSessionImportReport(
    string SourceId,
    string SourceName,
    IReadOnlyList<SharedSessionReportEntry> Entries)
{
    /// <summary>How many findings the report holds.</summary>
    public int Count => Entries.Count;

    /// <summary>Whether the report found nothing.</summary>
    public bool IsEmpty => Entries.Count == 0;

    /// <summary>How many findings of <paramref name="check"/> the report holds.</summary>
    public int CountOf(SharedSessionReportCheck check) => Entries.Count(entry => entry.Check == check);

    /// <summary>The report of <paramref name="sourceId"/> when its file has not been read.</summary>
    public static SharedSessionImportReport Empty(string sourceId, string sourceName) => new(sourceId, sourceName, []);
}
