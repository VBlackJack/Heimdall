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

namespace Heimdall.App.ViewModels;

/// <summary>
/// The locale keys of the root indicator, each written once as a literal so a search for a key
/// finds its use. A key is never built by concatenation or interpolation.
/// </summary>
internal static class SftpRootActivityLocaleKeys
{
    internal const string BadgeLabel = "SftpRootBadgeLabel";
    internal const string BadgeAnnouncement = "SftpRootBadgeAnnouncement";
    internal const string Summary = "SftpRootSummary";
    internal const string SummaryListingsOnly = "SftpRootSummaryListingsOnly";
    internal const string SummaryJoin = "SftpRootSummaryJoin";
    internal const string SummaryOperationsOne = "SftpRootSummaryOperationsOne";
    internal const string SummaryOperations = "SftpRootSummaryOperations";
    internal const string SummaryFailedOne = "SftpRootSummaryFailedOne";
    internal const string SummaryFailed = "SftpRootSummaryFailed";
    internal const string SummaryListingsOne = "SftpRootSummaryListingsOne";
    internal const string SummaryListings = "SftpRootSummaryListings";
    internal const string ListingLineOne = "SftpRootListingLineOne";
    internal const string ListingLine = "SftpRootListingLine";
    internal const string ActLine = "SftpRootActLine";
    internal const string ActRowName = "A11ySftpRootActRow";
    internal const string ActivityMenuName = "A11ySftpRootActivityMenu";

    private const string ActUpload = "SftpRootActUpload";
    private const string ActOverwrite = "SftpRootActOverwrite";
    private const string ActInlineSave = "SftpRootActInlineSave";
    private const string ActExternalEditorSave = "SftpRootActExternalEditorSave";
    private const string ActEditOpen = "SftpRootActEditOpen";
    private const string ActNewFolder = "SftpRootActNewFolder";
    private const string ActRename = "SftpRootActRename";
    private const string ActPermissionChange = "SftpRootActPermissionChange";
    private const string ActDownload = "SftpRootActDownload";
    private const string ActDelete = "SftpRootActDelete";
    private const string OutcomeSucceeded = "SftpRootOutcomeSucceeded";
    private const string OutcomeFailed = "SftpRootOutcomeFailed";
    private const string OutcomeCancelled = "SftpRootOutcomeCancelled";

    /// <summary>Every key the indicator can ask for, for the guards that check each one is defined.</summary>
    internal static IReadOnlyList<string> All { get; } =
    [
        BadgeLabel,
        BadgeAnnouncement,
        Summary,
        SummaryListingsOnly,
        SummaryJoin,
        SummaryOperationsOne,
        SummaryOperations,
        SummaryFailedOne,
        SummaryFailed,
        SummaryListingsOne,
        SummaryListings,
        ListingLineOne,
        ListingLine,
        ActLine,
        ActRowName,
        ActivityMenuName,
        ActUpload,
        ActOverwrite,
        ActInlineSave,
        ActExternalEditorSave,
        ActEditOpen,
        ActNewFolder,
        ActRename,
        ActPermissionChange,
        ActDownload,
        ActDelete,
        OutcomeSucceeded,
        OutcomeFailed,
        OutcomeCancelled,
    ];

    /// <summary>The key of the sentence that names an act of this kind.</summary>
    internal static string ForKind(SftpRootActKind kind)
    {
        return kind switch
        {
            SftpRootActKind.Upload => ActUpload,
            SftpRootActKind.Overwrite => ActOverwrite,
            SftpRootActKind.InlineSave => ActInlineSave,
            SftpRootActKind.ExternalEditorSave => ActExternalEditorSave,
            SftpRootActKind.EditOpen => ActEditOpen,
            SftpRootActKind.NewFolder => ActNewFolder,
            SftpRootActKind.Rename => ActRename,
            SftpRootActKind.PermissionChange => ActPermissionChange,
            SftpRootActKind.Download => ActDownload,
            SftpRootActKind.Delete => ActDelete,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
    }

    /// <summary>The key of the word that says how an act ended.</summary>
    internal static string ForOutcome(SftpRootActOutcome outcome)
    {
        return outcome switch
        {
            SftpRootActOutcome.Succeeded => OutcomeSucceeded,
            SftpRootActOutcome.Failed => OutcomeFailed,
            SftpRootActOutcome.Cancelled => OutcomeCancelled,
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null),
        };
    }
}
