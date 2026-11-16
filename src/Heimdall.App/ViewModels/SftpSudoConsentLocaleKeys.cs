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

/// <summary>The four texts of one question: its title, its sentence, and the names of its two answers.</summary>
/// <param name="Title">The title of the dialog.</param>
/// <param name="Message">The sentence. A job question takes the path and the job name; the others take the path.</param>
/// <param name="Accept">The button that runs the act as root, named after the act.</param>
/// <param name="Decline">The button that does not, named after the act.</param>
internal readonly record struct SftpSudoQuestionKeys(string Title, string Message, string Accept, string Decline);

/// <summary>
/// The locale keys of the root questions and of the lines that say root was not run, each written
/// once as a literal so a search for a key finds its use. A key is never built by concatenation.
/// </summary>
internal static class SftpSudoConsentLocaleKeys
{
    /// <summary>The line that says root was not tried because of the setting.</summary>
    internal const string NoteNever = "SftpSudoNoteNever";

    /// <summary>The line that says the user declined to run it as root.</summary>
    internal const string NoteDeclined = "SftpSudoNoteDeclined";

    /// <summary>The line that says nobody was asked, because the user did not start the act.</summary>
    internal const string NoteNotAsked = "SftpSudoNoteNotAsked";

    /// <summary>Puts the server's refusal and the note that follows it in one sentence.</summary>
    internal const string RefusalWithNote = "SftpSudoRefusalWithNote";

    /// <summary>The tooltip of the pane's sudo button while the setting is Never.</summary>
    internal const string ModeNeverTooltip = "SftpSudoModeNeverTooltip";

    /// <summary>The tooltip of the pane's sudo button otherwise.</summary>
    internal const string ModeTooltip = "SftpSudoModeTooltip";

    private const string DeleteTitle = "SftpConfirmSudoDeleteTitle";
    private const string DeleteMessage = "SftpConfirmSudoDelete";
    private const string DeleteAccept = "SftpConfirmSudoDeleteAccept";
    private const string DeleteDecline = "SftpConfirmSudoDeleteDecline";

    private const string UploadTitle = "SftpConfirmSudoUploadTitle";
    private const string UploadJobMessage = "SftpConfirmSudoUploadJob";
    private const string UploadAccept = "SftpConfirmSudoUploadAccept";
    private const string UploadDecline = "SftpConfirmSudoUploadDecline";

    private const string DownloadTitle = "SftpConfirmSudoDownloadTitle";
    private const string DownloadJobMessage = "SftpConfirmSudoDownloadJob";
    private const string DownloadAccept = "SftpConfirmSudoDownloadAccept";
    private const string DownloadDecline = "SftpConfirmSudoDownloadDecline";

    private const string RenameTitle = "SftpConfirmSudoRenameTitle";
    private const string RenameMessage = "SftpConfirmSudoRename";
    private const string RenameAccept = "SftpConfirmSudoRenameAccept";
    private const string RenameDecline = "SftpConfirmSudoRenameDecline";

    private const string NewFolderTitle = "SftpConfirmSudoNewFolderTitle";
    private const string NewFolderMessage = "SftpConfirmSudoNewFolder";
    private const string NewFolderAccept = "SftpConfirmSudoNewFolderAccept";
    private const string NewFolderDecline = "SftpConfirmSudoNewFolderDecline";

    private const string ChmodTitle = "SftpConfirmSudoChmodTitle";
    private const string ChmodMessage = "SftpConfirmSudoChmod";
    private const string ChmodAccept = "SftpConfirmSudoChmodAccept";
    private const string ChmodDecline = "SftpConfirmSudoChmodDecline";

    private const string ListingTitle = "SftpConfirmSudoListingTitle";
    private const string ListingMessage = "SftpConfirmSudoListing";
    private const string ListingAccept = "SftpConfirmSudoListingAccept";
    private const string ListingDecline = "SftpConfirmSudoListingDecline";

    private const string EditOpenTitle = "SftpConfirmSudoEditOpenTitle";
    private const string EditOpenMessage = "SftpConfirmSudoEditOpen";
    private const string EditOpenAccept = "SftpConfirmSudoEditOpenAccept";
    private const string EditOpenDecline = "SftpConfirmSudoEditOpenDecline";

    private const string InlineSaveTitle = "SftpConfirmSudoInlineSaveTitle";
    private const string InlineSaveMessage = "SftpConfirmSudoInlineSave";
    private const string InlineSaveAccept = "SftpConfirmSudoInlineSaveAccept";
    private const string InlineSaveDecline = "SftpConfirmSudoInlineSaveDecline";

    /// <summary>Every key this file can hand out, for the guards that check each one is defined.</summary>
    internal static IReadOnlyList<string> All { get; } =
    [
        NoteNever,
        NoteDeclined,
        NoteNotAsked,
        RefusalWithNote,
        ModeNeverTooltip,
        ModeTooltip,
        DeleteTitle,
        DeleteMessage,
        DeleteAccept,
        DeleteDecline,
        UploadTitle,
        UploadJobMessage,
        UploadAccept,
        UploadDecline,
        DownloadTitle,
        DownloadJobMessage,
        DownloadAccept,
        DownloadDecline,
        RenameTitle,
        RenameMessage,
        RenameAccept,
        RenameDecline,
        NewFolderTitle,
        NewFolderMessage,
        NewFolderAccept,
        NewFolderDecline,
        ChmodTitle,
        ChmodMessage,
        ChmodAccept,
        ChmodDecline,
        ListingTitle,
        ListingMessage,
        ListingAccept,
        ListingDecline,
        EditOpenTitle,
        EditOpenMessage,
        EditOpenAccept,
        EditOpenDecline,
        InlineSaveTitle,
        InlineSaveMessage,
        InlineSaveAccept,
        InlineSaveDecline,
    ];

    /// <summary>The note that says why root was not run, or null when it was allowed.</summary>
    internal static string? NoteFor(SftpSudoDecision decision) => decision switch
    {
        SftpSudoDecision.RefusedBySetting => NoteNever,
        SftpSudoDecision.Declined => NoteDeclined,
        SftpSudoDecision.NotAsked => NoteNotAsked,
        _ => null,
    };

    /// <summary>
    /// The texts of the question for an act. A job is worded for its direction (it asks once for
    /// the walk and every file), so the act is the scope's, not the entry's.
    /// </summary>
    internal static SftpSudoQuestionKeys For(SftpRootActKind act) => act switch
    {
        SftpRootActKind.Delete => new(DeleteTitle, DeleteMessage, DeleteAccept, DeleteDecline),
        SftpRootActKind.Upload or SftpRootActKind.Overwrite =>
            new(UploadTitle, UploadJobMessage, UploadAccept, UploadDecline),
        SftpRootActKind.Download => new(DownloadTitle, DownloadJobMessage, DownloadAccept, DownloadDecline),
        SftpRootActKind.Rename => new(RenameTitle, RenameMessage, RenameAccept, RenameDecline),
        SftpRootActKind.NewFolder => new(NewFolderTitle, NewFolderMessage, NewFolderAccept, NewFolderDecline),
        SftpRootActKind.PermissionChange => new(ChmodTitle, ChmodMessage, ChmodAccept, ChmodDecline),
        SftpRootActKind.Listing => new(ListingTitle, ListingMessage, ListingAccept, ListingDecline),
        SftpRootActKind.EditOpen or SftpRootActKind.ExternalEditorSave =>
            new(EditOpenTitle, EditOpenMessage, EditOpenAccept, EditOpenDecline),
        SftpRootActKind.InlineSave => new(InlineSaveTitle, InlineSaveMessage, InlineSaveAccept, InlineSaveDecline),
        _ => throw new ArgumentOutOfRangeException(nameof(act), act, "No question is worded for this act."),
    };
}
