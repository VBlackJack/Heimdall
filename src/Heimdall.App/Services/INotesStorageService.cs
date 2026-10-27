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
using Heimdall.Core.Models;

namespace Heimdall.App.Services;

/// <summary>
/// Narrow storage contract consumed by <c>NotesToolViewModel</c> and the
/// remaining view-side NotesTool plumbing.
/// </summary>
public interface INotesStorageService
{
    string NotesRootPath { get; }

    void EnsureInitialized();

    Task<IReadOnlyList<NoteListItem>> ListNotesAsync(
        string? searchText,
        NoteSortOrder sortOrder = NoteSortOrder.DateDescending,
        CancellationToken cancellationToken = default);

    Task<string> ResolveOrCreateNoteAsync(string noteReference, CancellationToken cancellationToken = default);

    Task<string> CreateNoteAsync(
        NoteTemplateKind templateKind,
        ToolContext? context,
        LocalizationManager? localizer = null,
        CancellationToken cancellationToken = default);

    string CreateFolder(string folderName, string? parentFolder = null);

    Task<string> MoveNoteToFolderAsync(string filePath, string targetFolder, CancellationToken cancellationToken = default);

    Task<string> LoadNoteAsync(string filePath, CancellationToken cancellationToken = default);

    Task SaveNoteAsync(string filePath, string content, CancellationToken cancellationToken = default);

    /// <summary>
    /// Synchronous save for the close and dispose paths, which cannot await.
    /// </summary>
    /// <returns>
    /// False when the note was not written because another write held the lock past the
    /// wait: the caller must keep the note dirty rather than report it saved.
    /// </returns>
    bool SaveNote(string filePath, string content);

    Task DeleteNoteAsync(string filePath, CancellationToken cancellationToken = default);

    Task<string> RenameNoteAsync(string filePath, string newName, CancellationToken cancellationToken = default);

    Task<string> DuplicateNoteAsync(string filePath, string duplicateLabel, CancellationToken cancellationToken = default);

    string GetRelativePath(string filePath);

    bool IsUnderNotesRoot(string filePath);
}
