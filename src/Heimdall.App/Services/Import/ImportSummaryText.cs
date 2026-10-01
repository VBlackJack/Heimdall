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

using Heimdall.Core.Localization;

namespace Heimdall.App.Services.Import;

/// <summary>
/// The result lines of the imports, each count worded by its own number.
/// </summary>
/// <remarks>
/// An import reports several counts in one sentence: "3 imported, 1 replaced, 0 skipped". No
/// single key pair can make every participle agree with its own number in French or Spanish, so
/// the sentence key keeps only the order and the punctuation, and each placeholder receives a
/// fragment counted on its own, such as "1 imported" or "2 skipped".
/// </remarks>
internal static class ImportSummaryText
{
    /// <summary>The result of a JSON profile import.</summary>
    internal static string Profiles(LocalizationManager localizer, int imported, int replaced, int renamed, int skipped) =>
        localizer.Format(
            "StatusImportProfileSummary",
            Imported(localizer, imported),
            localizer.FormatCount(replaced, "ImportCountReplacedOne", "ImportCountReplaced", replaced),
            localizer.FormatCount(renamed, "ImportCountAutoRenamedOne", "ImportCountAutoRenamed", renamed),
            Skipped(localizer, skipped));

    /// <summary>The result of an RDP file import.</summary>
    internal static string RdpFiles(
        LocalizationManager localizer,
        int imported,
        int replaced,
        int renamed,
        int skipped,
        int passwordsIgnored) =>
        localizer.Format(
            "StatusImportRdpSummary",
            Imported(localizer, imported),
            localizer.FormatCount(replaced, "ImportCountReplacedOne", "ImportCountReplaced", replaced),
            localizer.FormatCount(renamed, "ImportCountAutoRenamedOne", "ImportCountAutoRenamed", renamed),
            Skipped(localizer, skipped),
            localizer.FormatCount(
                passwordsIgnored,
                "ImportCountPasswordBlobsIgnoredOne",
                "ImportCountPasswordBlobsIgnored",
                passwordsIgnored));

    /// <summary>The SSH gateway line of a JSON profile import.</summary>
    internal static string Gateways(LocalizationManager localizer, int created, int merged, int orphanReferences) =>
        localizer.Format(
            "StatusImportProfileGatewaySummary",
            localizer.FormatCount(created, "ImportCountGatewaysCreatedOne", "ImportCountGatewaysCreated", created),
            localizer.FormatCount(merged, "ImportCountGatewaysMergedOne", "ImportCountGatewaysMerged", merged),
            localizer.FormatCount(
                orphanReferences,
                "ImportCountOrphanReferencesOne",
                "ImportCountOrphanReferences",
                orphanReferences));

    /// <summary>The result of an OpenSSH config import.</summary>
    internal static string OpenSsh(LocalizationManager localizer, int imported, int skippedDuplicates, int warnings) =>
        localizer.Format(
            "ToastImportOpenSshResult",
            Imported(localizer, imported),
            SkippedDuplicates(localizer, skippedDuplicates),
            Warnings(localizer, warnings));

    /// <summary>The result of a PuTTY sessions import.</summary>
    internal static string Putty(
        LocalizationManager localizer,
        int imported,
        int skippedDuplicates,
        int invalid,
        int warnings) =>
        localizer.Format(
            "ToastImportPuttyResult",
            Imported(localizer, imported),
            SkippedDuplicates(localizer, skippedDuplicates),
            localizer.FormatCount(invalid, "ImportCountInvalidOne", "ImportCountInvalid", invalid),
            Warnings(localizer, warnings));

    /// <summary>The result of a known_hosts import.</summary>
    internal static string KnownHosts(
        LocalizationManager localizer,
        int imported,
        int skippedTrusted,
        int skippedConflict,
        int warnings) =>
        localizer.Format(
            "ToastImportKnownHostsResult",
            localizer.FormatCount(imported, "KnownHostsCountImportedOne", "KnownHostsCountImported", imported),
            localizer.FormatCount(
                skippedTrusted,
                "KnownHostsCountSkippedTrustedOne",
                "KnownHostsCountSkippedTrusted",
                skippedTrusted),
            localizer.FormatCount(
                skippedConflict,
                "KnownHostsCountSkippedConflictOne",
                "KnownHostsCountSkippedConflict",
                skippedConflict),
            Warnings(localizer, warnings));

    /// <summary>The partial migration line, "2 examined, 1 imported, 1 skipped".</summary>
    internal static string MigrationPartial(LocalizationManager localizer, int examined, int imported, int skipped) =>
        localizer.Format(
            "MigrationPartialSummary",
            localizer.FormatCount(examined, "ImportCountExaminedOne", "ImportCountExamined", examined),
            Imported(localizer, imported),
            Skipped(localizer, skipped));

    /// <summary>The preview line of a session import dialog.</summary>
    internal static string SessionPreview(LocalizationManager localizer, int candidates, int fresh, int duplicates, int invalid)
    {
        string candidatesText = localizer.FormatCount(candidates, "ImportCountCandidatesOne", "ImportCountCandidates", candidates);
        string freshText = localizer.FormatCount(fresh, "ImportCountNewOne", "ImportCountNew", fresh);
        string duplicatesText = localizer.FormatCount(duplicates, "ImportCountDuplicatesOne", "ImportCountDuplicates", duplicates);
        return invalid > 0
            ? localizer.Format(
                "LabelImportSessionsPreviewSummary",
                candidatesText,
                freshText,
                duplicatesText,
                localizer.FormatCount(invalid, "ImportCountInvalidOne", "ImportCountInvalid", invalid))
            : localizer.Format("LabelImportSessionsPreviewSummaryNoInvalid", candidatesText, freshText, duplicatesText);
    }

    /// <summary>The preview line of the known_hosts import dialog.</summary>
    internal static string KnownHostsPreview(LocalizationManager localizer, int entries, int fresh, int trusted, int conflicting) =>
        localizer.Format(
            "SummaryKnownHostsItems",
            localizer.FormatCount(entries, "KnownHostsCountEntriesOne", "KnownHostsCountEntries", entries),
            localizer.FormatCount(fresh, "KnownHostsCountNewOne", "KnownHostsCountNew", fresh),
            localizer.FormatCount(trusted, "KnownHostsCountTrustedOne", "KnownHostsCountTrusted", trusted),
            localizer.FormatCount(conflicting, "KnownHostsCountConflictingOne", "KnownHostsCountConflicting", conflicting));

    private static string Imported(LocalizationManager localizer, int count) =>
        localizer.FormatCount(count, "ImportCountImportedOne", "ImportCountImported", count);

    private static string Skipped(LocalizationManager localizer, int count) =>
        localizer.FormatCount(count, "ImportCountSkippedOne", "ImportCountSkipped", count);

    private static string SkippedDuplicates(LocalizationManager localizer, int count) =>
        localizer.FormatCount(count, "ImportCountSkippedDuplicatesOne", "ImportCountSkippedDuplicates", count);

    private static string Warnings(LocalizationManager localizer, int count) =>
        localizer.FormatCount(count, "ImportCountWarningsOne", "ImportCountWarnings", count);
}
