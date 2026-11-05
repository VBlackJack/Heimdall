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

/// <summary>What happened to a shared source on the last load.</summary>
public enum SharedSessionSourceStatus
{
    /// <summary>The source is turned off; nothing was read.</summary>
    Disabled,

    /// <summary>The file was read and its sessions are held.</summary>
    Loaded,

    /// <summary>The source could not be read; <see cref="SharedSessionSourceState.ErrorMessage"/> says why.</summary>
    Error,
}

/// <summary>
/// One shared source as of the last load.
/// </summary>
/// <param name="SourceId">The source identifier.</param>
/// <param name="Name">The name the user gave the source.</param>
/// <param name="ExpandedPath">The path read, environment variables expanded.</param>
/// <param name="Status">What happened.</param>
/// <param name="ErrorMessage">The localized reason, when <paramref name="Status"/> is an error.</param>
/// <param name="ProfileCount">How many shared profiles the source holds.</param>
/// <param name="GatewayCount">How many shared gateways the source holds.</param>
/// <param name="Warnings">What the parser could not take as it is.</param>
/// <param name="DroppedProfileCount">How many parsed sessions the sanitizer refused.</param>
/// <param name="Notice">
/// Why what is held is not what the file holds now, localized, or null when it is: the file could
/// not be read and the offline copy stands in, or a change could not be taken and the previous
/// version is kept. The sessions are still held and connect.
/// </param>
/// <param name="OfflineCopyFromUtc">
/// When the offline copy standing in for the file was taken, or null when the sessions come from
/// the file itself.
/// </param>
/// <param name="Report">
/// What the last successful read of the file found to report, or null when it was never read.
/// </param>
/// <param name="Variables">
/// The <c>[NAME]</c> variables the file's sessions use in a login field, resolved or not, sorted;
/// a session the parser or the shared filter later refused counts too, since the file still
/// asks for the variable. Null when the file was never read.
/// </param>
public sealed record SharedSessionSourceState(
    string SourceId,
    string Name,
    string ExpandedPath,
    SharedSessionSourceStatus Status,
    string? ErrorMessage,
    int ProfileCount,
    int GatewayCount,
    IReadOnlyList<MobaXtermImportWarning> Warnings,
    int DroppedProfileCount,
    string? Notice = null,
    DateTime? OfflineCopyFromUtc = null,
    SharedSessionImportReport? Report = null,
    IReadOnlyList<string>? Variables = null)
{
    /// <summary>The variables the file's sessions use; empty when the file was never read.</summary>
    public IReadOnlyList<string> ReferencedVariables => Variables ?? [];

    /// <summary>How many findings the import report holds, information included; zero without a report.</summary>
    public int IssueCount => Report?.Count ?? 0;

    /// <summary>How many findings of the import report are warnings; zero without a report.</summary>
    public int WarningCount => Report?.WarningCount ?? 0;

    /// <summary>Whether the sessions held come from the offline copy rather than the file.</summary>
    public bool IsOfflineCopy => OfflineCopyFromUtc is not null;
}

/// <summary>A reload of one shared source, and how many warnings its report held before it.</summary>
/// <param name="State">The state the source is in after the reload.</param>
/// <param name="PreviousWarningCount">How many warnings its import report held before.</param>
public sealed record SharedSessionSourceReload(SharedSessionSourceState State, int PreviousWarningCount)
{
    /// <summary>Whether the reload changed how many warnings the import report holds.</summary>
    public bool WarningCountChanged => State.WarningCount != PreviousWarningCount;
}
