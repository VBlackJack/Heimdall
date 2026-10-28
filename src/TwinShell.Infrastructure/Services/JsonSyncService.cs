/*
 * Copyright 2025 Julien Bombled
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

using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using TwinShell.Core.Enums;
using TwinShell.Core.Helpers;
using TwinShell.Core.Interfaces;
using TwinShell.Core.Models;
using TwinShell.Core.Utilities;

namespace TwinShell.Infrastructure.Services;

/// <summary>
/// Service for GitOps synchronization of TwinShell data via JSON files.
/// Enables collaborative editing through Git-synchronized folders.
/// Decoupled from database context - uses repository interfaces for data access.
/// </summary>
public sealed class JsonSyncService : SyncServiceBase
{
    private readonly IActionRepository _actionRepository;
    private readonly IBatchRepository _batchRepository;
    private readonly ICustomCategoryRepository _categoryRepository;
    private readonly ICommandTemplateRepository _templateRepository;
    private readonly IUnitOfWork? _unitOfWork;
    private readonly ILogger<JsonSyncService>? _logger;
    private readonly ISyncTombstoneRepository? _tombstoneRepository;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>The most file names one "kept" warning lists before it elides the rest.</summary>
    private const int MaxKeptFileNamesListed = 5;

    /// <summary>
    /// The folder's record of deleted entries. Underscore-prefixed, so no build's orphan
    /// removal ever treats it as a managed file.
    /// </summary>
    private const string TombstoneFileName = "_deleted.json";

    /// <summary>
    /// The largest <c>_deleted.json</c> read: tombstones are kept forever, so the file grows
    /// past the per-entry limit, about forty thousand deletes at this size.
    /// </summary>
    private const long MaxTombstoneFileBytes = 4 * 1024 * 1024;

    /// <summary>The import in progress; null between imports.</summary>
    private ImportSession? _importSession;

    public JsonSyncService(
        IActionRepository actionRepository,
        IBatchRepository batchRepository,
        ICustomCategoryRepository categoryRepository,
        ICommandTemplateRepository templateRepository,
        IUnitOfWork? unitOfWork = null,
        ILogger<JsonSyncService>? logger = null,
        ISyncTombstoneRepository? tombstoneRepository = null)
    {
        _actionRepository = actionRepository;
        _batchRepository = batchRepository;
        _categoryRepository = categoryRepository;
        _templateRepository = templateRepository;
        _unitOfWork = unitOfWork;
        _logger = logger;
        _tombstoneRepository = tombstoneRepository;
        _jsonOptions = JsonOptionsHelper.SyncService;
    }

    #region Export

    /// <summary>
    /// Writes this database's own entries to the folder, and the deletes it knows of to the
    /// folder's <c>_deleted.json</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only what an operator made travels (<see cref="SharedLibraryContent"/>): user-created
    /// actions, the templates those actions use, user runbooks and user categories. The
    /// shipped catalogue never does, because every install ships it.
    /// </para>
    /// <para>
    /// The export never deletes a file it did not write, with three exceptions: the file of an
    /// entry a tombstone says was deleted, a second copy of an entry this export just wrote
    /// under another name or folder, and (an explicit exception, the purge of P1-03) an action
    /// or runbook file declaring <c>isUserCreated: false</c>, a shipped entry an older build
    /// wrote, which every install ships for itself. Any other file (unknown, unreadable,
    /// rejected by this build's schema, written by a newer build) stays and is reported: it
    /// belongs to a teammate, and deleting it would push its loss to everybody.
    /// </para>
    /// </remarks>
    public override async Task<SyncExportResult> ExportDataToYamlAsync(string rootFolderPath, CancellationToken cancellationToken = default)
    {
        var result = new SyncExportResult { Success = true };

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Create folder structure
            EnsureDirectoryExists(rootFolderPath);
            EnsureDirectoryExists(Path.Combine(rootFolderPath, ActionsFolderName));
            EnsureDirectoryExists(Path.Combine(rootFolderPath, BatchesFolderName));
            EnsureDirectoryExists(Path.Combine(rootFolderPath, TemplatesFolderName));
            EnsureDirectoryExists(Path.Combine(rootFolderPath, CategoriesFolderName));

            TombstoneSet tombstones = await ReadTombstonesAsync(rootFolderPath, result.Warnings);
            UserContent content = await ApplyTombstonesToExportAsync(await ReadUserContentAsync(), tombstones);
            IReadOnlyDictionary<Guid, DateTime> deleted = tombstones.Entries;

            // Export categories first (they may be referenced by actions)
            cancellationToken.ThrowIfCancellationRequested();
            result.CategoriesExported = await ExportCategoriesAsync(rootFolderPath, content, deleted, result, cancellationToken);

            // Export templates (they may be referenced by actions)
            cancellationToken.ThrowIfCancellationRequested();
            result.TemplatesExported = await ExportTemplatesAsync(rootFolderPath, content, deleted, result, cancellationToken);

            // Export actions (organized by category)
            cancellationToken.ThrowIfCancellationRequested();
            result.ActionsExported = await ExportActionsAsync(rootFolderPath, content, deleted, result, cancellationToken);

            // Export batches
            cancellationToken.ThrowIfCancellationRequested();
            result.BatchesExported = await ExportBatchesAsync(rootFolderPath, content, deleted, result, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            await WriteTombstoneFileAsync(rootFolderPath, tombstones, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Errors.Add($"Export failed: {ex.Message}");
        }

        return result;
    }

    private async Task<int> ExportCategoriesAsync(
        string rootFolderPath,
        UserContent content,
        IReadOnlyDictionary<Guid, DateTime> deleted,
        SyncExportResult result,
        CancellationToken cancellationToken)
    {
        var categoriesPath = Path.Combine(rootFolderPath, CategoriesFolderName);
        ExportPass pass = new(deleted, content.Categories.Select(c => c.PublicId));
        int count = 0;

        foreach (var category in content.Categories)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var model = new CategoryModel
                {
                    Id = category.PublicId,
                    Name = category.Name,
                    Description = category.Description,
                    IconKey = category.IconKey,
                    ColorHex = category.ColorHex,
                    IsSystemCategory = category.IsSystemCategory,
                    DisplayOrder = category.DisplayOrder,
                    IsHidden = category.IsHidden,
                    ModifiedAt = category.ModifiedAt
                };

                var fileName = BuildEntityFileName(category.Name, category.PublicId);
                var filePath = Path.Combine(categoriesPath, fileName);

                await WriteJsonFileAsync(filePath, model, cancellationToken);
                pass.WrittenFullPaths.Add(Path.GetFullPath(filePath));
                count++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                pass.FailedIds.Add(category.PublicId);
                result.Warnings.Add($"Failed to export category '{category.Name}': {ex.Message}");
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        RemoveStaleFiles(
            categoriesPath,
            recursive: false,
            pass,
            isShippedCopy: _ => false,
            result);

        return count;
    }

    private async Task<int> ExportTemplatesAsync(
        string rootFolderPath,
        UserContent content,
        IReadOnlyDictionary<Guid, DateTime> deleted,
        SyncExportResult result,
        CancellationToken cancellationToken)
    {
        var templatesPath = Path.Combine(rootFolderPath, TemplatesFolderName);
        ExportPass pass = new(deleted, content.Templates.Select(t => t.PublicId));
        int count = 0;

        foreach (var template in content.Templates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var parameters = template.Parameters.Select(p => new TemplateParameterModel
                {
                    Name = p.Name,
                    Label = p.Label,
                    Type = p.Type,
                    DefaultValue = p.DefaultValue,
                    Required = p.Required,
                    Description = p.Description,
                    AllowedValues = p.AllowedValues is { Count: > 0 } ? p.AllowedValues.ToList() : null,
                    Quoting = p.Quoting?.ToString()
                }).ToList();

                var model = new TemplateModel
                {
                    Id = template.PublicId,
                    Name = template.Name,
                    Platform = template.Platform.ToString(),
                    CommandPattern = template.CommandPattern,
                    Parameters = parameters
                };

                var fileName = BuildEntityFileName(template.Name, template.PublicId);
                var filePath = Path.Combine(templatesPath, fileName);

                await WriteJsonFileAsync(filePath, model, cancellationToken);
                pass.WrittenFullPaths.Add(Path.GetFullPath(filePath));
                count++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                pass.FailedIds.Add(template.PublicId);
                result.Warnings.Add($"Failed to export template '{template.Name}': {ex.Message}");
            }
        }

        // A template file declares nothing about itself, so the purge of shipped files does not
        // reach it: a shipped template an older build wrote stays, reported, until deleted by hand.
        cancellationToken.ThrowIfCancellationRequested();
        RemoveStaleFiles(
            templatesPath,
            recursive: false,
            pass,
            isShippedCopy: _ => false,
            result);

        return count;
    }

    private async Task<int> ExportActionsAsync(
        string rootFolderPath,
        UserContent content,
        IReadOnlyDictionary<Guid, DateTime> deleted,
        SyncExportResult result,
        CancellationToken cancellationToken)
    {
        var actionsPath = Path.Combine(rootFolderPath, ActionsFolderName);
        ExportPass pass = new(deleted, content.Actions.Select(a => a.PublicId));

        int count = 0;

        foreach (var action in content.Actions)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                // Create category subfolder
                var categoryFolder = SanitizeFileName(action.Category);
                var categoryPath = Path.Combine(actionsPath, categoryFolder);
                EnsureDirectoryExists(categoryPath);

                // Get template references by PublicId
                Guid? windowsTemplatePublicId = action.WindowsCommandTemplate?.PublicId;
                Guid? linuxTemplatePublicId = action.LinuxCommandTemplate?.PublicId;

                var model = new ActionModel
                {
                    Id = action.PublicId,
                    Title = action.Title,
                    Description = action.Description,
                    Category = action.Category,
                    Platform = action.Platform.ToString(),
                    Level = action.Level.ToString(),
                    Tags = action.Tags,
                    WindowsTemplateId = windowsTemplatePublicId,
                    LinuxTemplateId = linuxTemplatePublicId,
                    Examples = action.Examples.Select(e => new ExampleModel
                    {
                        Command = e.Command,
                        Description = e.Description,
                        Platform = e.Platform.ToString()
                    }).ToList(),
                    WindowsExamples = action.WindowsExamples.Select(e => new ExampleModel
                    {
                        Command = e.Command,
                        Description = e.Description,
                        Platform = e.Platform.ToString()
                    }).ToList(),
                    LinuxExamples = action.LinuxExamples.Select(e => new ExampleModel
                    {
                        Command = e.Command,
                        Description = e.Description,
                        Platform = e.Platform.ToString()
                    }).ToList(),
                    Notes = action.Notes,
                    Links = action.Links.Select(l => new LinkModel
                    {
                        Title = l.Title,
                        Url = l.Url
                    }).ToList(),
                    IsUserCreated = action.IsUserCreated,
                    UpdatedAt = action.UpdatedAt,
                    Translations = action.Translations is { Count: > 0 } ? action.Translations : null
                };

                var fileName = BuildEntityFileName(action.Title, action.PublicId);
                var filePath = Path.Combine(categoryPath, fileName);

                await WriteJsonFileAsync(filePath, model, cancellationToken);
                pass.WrittenFullPaths.Add(Path.GetFullPath(filePath));
                count++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                pass.FailedIds.Add(action.PublicId);
                result.Warnings.Add($"Failed to export action '{action.Title}': {ex.Message}");
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        RemoveStaleFiles(
            actionsPath,
            recursive: true,
            pass,
            // The explicit exception of P1-03: a shipped entry an older build wrote.
            isShippedCopy: identity => identity.IsUserCreated == false,
            result);

        return count;
    }

    private async Task<int> ExportBatchesAsync(
        string rootFolderPath,
        UserContent content,
        IReadOnlyDictionary<Guid, DateTime> deleted,
        SyncExportResult result,
        CancellationToken cancellationToken)
    {
        var batchesPath = Path.Combine(rootFolderPath, BatchesFolderName);
        ExportPass pass = new(deleted, content.Batches.Select(b => b.PublicId));
        int count = 0;

        foreach (var batch in content.Batches)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var commands = batch.Commands.Select(c => new BatchCommandModel
                {
                    Id = c.Id,
                    Order = c.Order,
                    ActionId = c.ActionId,
                    ActionPublicId = c.ActionId is not null
                        && content.ActionsById.TryGetValue(c.ActionId, out Core.Models.Action? stepAction)
                            ? stepAction.PublicId
                            : null,
                    ActionTitle = c.ActionTitle,
                    Command = c.Command,
                    Platform = c.Platform.ToString(),
                    Description = c.Description,
                    Parameters = c.Parameters.Count > 0 ? c.Parameters : null
                }).ToList();

                var model = new BatchModel
                {
                    Id = batch.PublicId,
                    Name = batch.Name,
                    Description = batch.Description,
                    ExecutionMode = batch.ExecutionMode.ToString(),
                    Tags = batch.Tags,
                    Variables = batch.Variables.Count > 0 ? batch.Variables : null,
                    Commands = commands,
                    IsUserCreated = batch.IsUserCreated,
                    UpdatedAt = batch.UpdatedAt
                };

                var fileName = BuildEntityFileName(batch.Name, batch.PublicId);
                var filePath = Path.Combine(batchesPath, fileName);

                await WriteJsonFileAsync(filePath, model, cancellationToken);
                pass.WrittenFullPaths.Add(Path.GetFullPath(filePath));
                count++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                pass.FailedIds.Add(batch.PublicId);
                result.Warnings.Add($"Failed to export batch '{batch.Name}': {ex.Message}");
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        RemoveStaleFiles(
            batchesPath,
            recursive: false,
            pass,
            // The explicit exception of P1-03: a shipped entry an older build wrote.
            isShippedCopy: identity => identity.IsUserCreated == false,
            result);

        return count;
    }

    /// <summary>
    /// Reads what an operator made, the part of the database that travels.
    /// </summary>
    private async Task<UserContent> ReadUserContentAsync()
    {
        List<Core.Models.Action> allActions = (await _actionRepository.GetAllWithTemplatesAsync()).ToList();
        List<Core.Models.Action> userActions = allActions.Where(SharedLibraryContent.IsShared).ToList();
        List<CustomCategory> categories = (await _categoryRepository.GetAllAsync())
            .Where(SharedLibraryContent.IsShared)
            .ToList();
        List<CommandBatch> batches = (await _batchRepository.GetAllAsync())
            .Where(SharedLibraryContent.IsShared)
            .ToList();
        Dictionary<string, Core.Models.Action> actionsById = allActions
            .GroupBy(a => a.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        return new UserContent(categories, TemplatesOf(userActions), userActions, batches, actionsById);
    }

    /// <summary>The templates <paramref name="actions"/> use, once each.</summary>
    private static List<CommandTemplate> TemplatesOf(IEnumerable<Core.Models.Action> actions)
        => actions
            .SelectMany(a => new[] { a.WindowsCommandTemplate, a.LinuxCommandTemplate })
            .OfType<CommandTemplate>()
            .GroupBy(t => t.PublicId)
            .Select(group => group.First())
            .ToList();

    #endregion

    #region Tombstones

    /// <summary>
    /// Reads the deletes this sync knows of: the folder's <c>_deleted.json</c> merged with the
    /// deletes recorded in this database, the later time winning for an id on both.
    /// </summary>
    private async Task<TombstoneSet> ReadTombstonesAsync(string rootFolderPath, List<string> warnings)
    {
        TombstoneSet tombstones = ReadFolderTombstones(rootFolderPath, warnings);
        if (_tombstoneRepository is not null)
        {
            foreach ((Guid publicId, DateTime deletedAt) in await _tombstoneRepository.GetAllAsync())
            {
                MergeTombstone(tombstones, publicId, deletedAt);
            }
        }

        return tombstones;
    }

    /// <summary>
    /// Reads the folder's <c>_deleted.json</c>. A file that cannot be read is reported and left
    /// alone: rewriting it would drop the deletes of every teammate.
    /// </summary>
    private TombstoneSet ReadFolderTombstones(string rootFolderPath, List<string> warnings)
    {
        TombstoneSet tombstones = new();
        string filePath = Path.Combine(rootFolderPath, TombstoneFileName);
        if (!File.Exists(filePath))
        {
            return tombstones;
        }

        try
        {
            if (new FileInfo(filePath).Length > MaxTombstoneFileBytes)
            {
                throw new IOException($"larger than {MaxTombstoneFileBytes} bytes");
            }

            string text = File.ReadAllText(filePath);
            TombstoneFileModel? model = JsonSerializer.Deserialize<TombstoneFileModel>(text, _jsonOptions);
            tombstones.FileText = text;
            foreach (TombstoneModel tombstone in model?.Tombstones ?? [])
            {
                if (tombstone.Id != Guid.Empty)
                {
                    MergeTombstone(tombstones, tombstone.Id, tombstone.DeletedAt);
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            tombstones.Entries.Clear();
            tombstones.FileReadable = false;
            warnings.Add($"Could not read '{TombstoneFileName}': the deletes it records are not applied, and it is left as it is: {ex.Message}");
        }

        return tombstones;
    }

    private static void MergeTombstone(TombstoneSet tombstones, Guid publicId, DateTime deletedAt)
    {
        DateTime utc = DateTime.SpecifyKind(deletedAt, DateTimeKind.Utc);
        if (!tombstones.Entries.TryGetValue(publicId, out DateTime current) || utc > current)
        {
            tombstones.Entries[publicId] = utc;
        }
    }

    /// <summary>
    /// Takes out of <paramref name="content"/> what a tombstone deleted after its last change,
    /// and drops the tombstones of entries changed after their delete: a local edit made after
    /// a delete wins, and the entry travels again.
    /// </summary>
    /// <remarks>
    /// An entry held here that another install deleted later than this one last changed it is
    /// not written: the next import deletes it here too. A template has no timestamp of its
    /// own; one an exported action still uses is in use, and that wins.
    /// </remarks>
    private async Task<UserContent> ApplyTombstonesToExportAsync(UserContent content, TombstoneSet tombstones)
    {
        List<Guid> superseded = [];

        bool Survives(Guid publicId, DateTime changedAt)
        {
            if (!tombstones.Entries.TryGetValue(publicId, out DateTime deletedAt))
            {
                return true;
            }

            if (changedAt > deletedAt)
            {
                superseded.Add(publicId);
                return true;
            }

            return false;
        }

        List<Core.Models.Action> actions = content.Actions.Where(a => Survives(a.PublicId, a.UpdatedAt)).ToList();
        List<CommandBatch> batches = content.Batches.Where(b => Survives(b.PublicId, b.UpdatedAt)).ToList();
        List<CustomCategory> categories = content.Categories
            .Where(c => Survives(c.PublicId, c.ModifiedAt ?? c.CreatedAt))
            .ToList();
        List<CommandTemplate> templates = TemplatesOf(actions);
        superseded.AddRange(templates.Select(t => t.PublicId).Where(tombstones.Entries.ContainsKey));

        foreach (Guid publicId in superseded.Distinct())
        {
            tombstones.Entries.Remove(publicId);
            if (_tombstoneRepository is not null)
            {
                await _tombstoneRepository.RemoveAsync(publicId);
            }
        }

        return new UserContent(categories, templates, actions, batches, content.ActionsById);
    }

    /// <summary>
    /// Writes the folder's <c>_deleted.json</c> when the set changed. Tombstones are kept
    /// forever: an install that syncs a year late still learns of the delete.
    /// </summary>
    private async Task WriteTombstoneFileAsync(string rootFolderPath, TombstoneSet tombstones, CancellationToken cancellationToken)
    {
        if (!tombstones.FileReadable || (tombstones.Entries.Count == 0 && tombstones.FileText is null))
        {
            return;
        }

        TombstoneFileModel model = new()
        {
            Tombstones = tombstones.Entries
                .OrderBy(entry => entry.Key)
                .Select(entry => new TombstoneModel { Id = entry.Key, DeletedAt = entry.Value })
                .ToList()
        };
        string json = JsonSerializer.Serialize(model, _jsonOptions);
        if (string.Equals(json, tombstones.FileText, StringComparison.Ordinal))
        {
            return;
        }

        await WriteJsonFileAsync(Path.Combine(rootFolderPath, TombstoneFileName), model, cancellationToken);
    }

    /// <summary>
    /// Deletes here what the folder's tombstones deleted on another install after this
    /// database last changed it.
    /// </summary>
    private async Task ApplyFolderTombstonesAsync(string rootFolderPath, SyncImportResult result, CancellationToken cancellationToken)
    {
        TombstoneSet tombstones = ReadFolderTombstones(rootFolderPath, result.Warnings);
        HashSet<string>? usedTemplateIds = null;

        foreach ((Guid publicId, DateTime deletedAt) in tombstones.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (await _actionRepository.GetByPublicIdAsync(publicId) is { } action)
                {
                    if (SharedLibraryContent.IsShared(action) && action.UpdatedAt <= deletedAt)
                    {
                        await _actionRepository.DeleteAsync(action.Id);
                        await KeepFolderDeleteTimeAsync(publicId, deletedAt, result);
                    }

                    continue;
                }

                if (await _batchRepository.GetByPublicIdAsync(publicId) is { } batch)
                {
                    if (SharedLibraryContent.IsShared(batch) && batch.UpdatedAt <= deletedAt)
                    {
                        await _batchRepository.DeleteAsync(batch.Id);
                        await KeepFolderDeleteTimeAsync(publicId, deletedAt, result);
                    }

                    continue;
                }

                if (await _categoryRepository.GetByPublicIdAsync(publicId) is { } category)
                {
                    if (SharedLibraryContent.IsShared(category) && (category.ModifiedAt ?? category.CreatedAt) <= deletedAt)
                    {
                        await _categoryRepository.DeleteAsync(category.Id);
                        await KeepFolderDeleteTimeAsync(publicId, deletedAt, result);
                    }

                    continue;
                }

                if (await _templateRepository.GetByPublicIdAsync(publicId) is { } template)
                {
                    usedTemplateIds ??= (await _actionRepository.GetAllWithTemplatesAsync())
                        .SelectMany(a => new[] { a.WindowsCommandTemplateId, a.LinuxCommandTemplateId })
                        .OfType<string>()
                        .ToHashSet(StringComparer.Ordinal);

                    // A template an action here still uses stays: the action is what decides.
                    if (!usedTemplateIds.Contains(template.Id))
                    {
                        await _templateRepository.DeleteAsync(template.Id);
                        await KeepFolderDeleteTimeAsync(publicId, deletedAt, result);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"Failed to apply the delete of '{publicId}': {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Counts a delete that came from the folder, and keeps its original time: the repository
    /// recorded it as of now, which would make the delete look later than an edit made on
    /// another install in between.
    /// </summary>
    private async Task KeepFolderDeleteTimeAsync(Guid publicId, DateTime deletedAt, SyncImportResult result)
    {
        result.EntriesDeleted++;
        if (_tombstoneRepository is not null)
        {
            await _tombstoneRepository.SetAsync(publicId, deletedAt);
        }
    }

    #endregion

    #region Import

    public override async Task<SyncImportResult> ImportDataFromYamlAsync(string rootFolderPath, CancellationToken cancellationToken = default)
    {
        var result = new SyncImportResult { Success = true };

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Start transaction for atomic import (all or nothing)
            if (_unitOfWork != null)
            {
                await _unitOfWork.BeginTransactionAsync();
            }

            // Import in order: categories, templates, actions, batches
            // (respecting dependencies)
            ImportSession session = new(ScanActionFiles(Path.Combine(rootFolderPath, ActionsFolderName)));
            _importSession = session;

            await RunImportPipelineAsync(rootFolderPath, result, cancellationToken);

            // The template updates wait for the actions: an action whose local copy is kept
            // keeps its templates too, or a pull would revert a local edit of its command.
            cancellationToken.ThrowIfCancellationRequested();
            await ApplyPendingTemplateUpdatesAsync(session, result, cancellationToken);

            // Deletes made on other installs, last: an entry this import just wrote from a
            // file older than its tombstone goes too.
            cancellationToken.ThrowIfCancellationRequested();
            await ApplyFolderTombstonesAsync(rootFolderPath, result, cancellationToken);

            // Commit transaction if successful
            cancellationToken.ThrowIfCancellationRequested();
            if (_unitOfWork != null && result.Success)
            {
                await _unitOfWork.CommitTransactionAsync();
            }
        }
        catch (OperationCanceledException)
        {
            if (_unitOfWork != null)
            {
                try
                {
                    await _unitOfWork.RollbackTransactionAsync();
                }
                catch (Exception rollbackEx)
                {
                    _logger?.LogWarning(rollbackEx, "Rollback after cancellation failed during import");
                }
            }

            throw;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Errors.Add($"Import failed: {ex.Message}");

            // Rollback transaction on failure
            if (_unitOfWork != null)
            {
                try
                {
                    await _unitOfWork.RollbackTransactionAsync();
                    result.Errors.Add("All changes have been rolled back.");
                }
                catch (Exception rollbackEx)
                {
                    result.Errors.Add($"Rollback failed: {rollbackEx.Message}");
                }
            }
        }
        finally
        {
            _importSession = null;
        }

        return result;
    }

    /// <summary>
    /// Writes the template updates read from the folder, except those of an action whose local
    /// copy was kept.
    /// </summary>
    private async Task ApplyPendingTemplateUpdatesAsync(
        ImportSession session,
        SyncImportResult result,
        CancellationToken cancellationToken)
    {
        foreach ((Guid publicId, CommandTemplate template) in session.PendingTemplateUpdates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (session.KeepLocalTemplateIds.Contains(publicId))
            {
                result.TemplatesSkipped++;
                result.Warnings.Add($"Conflict: Template '{template.Name}' - its action kept the local version, so it did too");
                continue;
            }

            try
            {
                await _templateRepository.UpdateAsync(template);
                result.TemplatesUpdated++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"Failed to import template '{template.Name}': {ex.Message}");
            }
        }
    }

    /// <remarks>
    /// Newer wins, as for actions, on <see cref="CustomCategory.ModifiedAt"/>. A file without a
    /// timestamp (written before categories carried one) wins only over a local category that
    /// has none either: otherwise a pull would revert a local edit.
    /// </remarks>
    protected override async Task ImportCategoriesAsync(string folderPath, SyncImportResult result, CancellationToken cancellationToken)
    {
        var files = Directory.GetFiles(folderPath, "*.json", SearchOption.TopDirectoryOnly);

        foreach (var filePath in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (!ValidateFileSize(filePath))
                {
                    result.Warnings.Add($"File too large, skipped: {filePath}");
                    continue;
                }

                var json = await File.ReadAllTextAsync(filePath, cancellationToken);

                // SECURITY: Validate JSON against schema before processing
                var validationResult = JsonSchemaValidator.ValidateCategory(json);
                if (!validationResult.IsValid)
                {
                    result.Warnings.Add($"Schema validation failed for '{Path.GetFileName(filePath)}': {string.Join("; ", validationResult.Errors.Take(3))}");
                    continue;
                }

                var model = JsonSerializer.Deserialize<CategoryModel>(json, _jsonOptions);

                if (model == null || model.Id == Guid.Empty)
                {
                    result.Warnings.Add($"Invalid category file: {filePath}");
                    continue;
                }

                DateTime? remoteModifiedAt = model.ModifiedAt;
                if (remoteModifiedAt.HasValue)
                {
                    remoteModifiedAt = ImportTimestampNormalizer.ClampFutureToNow(
                        remoteModifiedAt.Value,
                        DateTime.UtcNow,
                        ImportTimestampNormalizer.DefaultFutureSkew,
                        out bool wasClamped);
                    if (wasClamped)
                    {
                        result.Warnings.Add($"Category '{model.Name}' had a future modifiedAt timestamp; clamped to import time.");
                    }
                }

                var existing = await _categoryRepository.GetByPublicIdAsync(model.Id);

                if (existing != null)
                {
                    if (existing.ModifiedAt is { } localModifiedAt
                        && (remoteModifiedAt is null || localModifiedAt > remoteModifiedAt.Value))
                    {
                        result.Conflicts.Add(new SyncEntityConflict
                        {
                            EntityType = "Category",
                            EntityId = model.Id,
                            EntityName = model.Name,
                            FilePath = filePath,
                            LocalUpdatedAt = localModifiedAt,
                            RemoteUpdatedAt = remoteModifiedAt ?? DateTime.MinValue,
                            Resolution = SyncConflictResolution.KeepLocal
                        });
                        result.CategoriesSkipped++;
                        result.Warnings.Add($"Conflict: Category '{model.Name}' - local is newer, kept local version");
                        continue;
                    }

                    // Update existing (remote is newer or same)
                    existing.Name = model.Name;
                    existing.Description = model.Description;
                    existing.IconKey = model.IconKey ?? "folder";
                    existing.ColorHex = model.ColorHex ?? "#2196F3";
                    existing.IsSystemCategory = model.IsSystemCategory;
                    existing.DisplayOrder = model.DisplayOrder;
                    existing.IsHidden = model.IsHidden;
                    existing.ModifiedAt = remoteModifiedAt;

                    await _categoryRepository.UpdateAsync(existing);
                    result.CategoriesUpdated++;
                }
                else
                {
                    // Create new
                    var category = new CustomCategory
                    {
                        Id = Guid.NewGuid().ToString(),
                        PublicId = model.Id,
                        Name = model.Name,
                        Description = model.Description,
                        IconKey = model.IconKey ?? "folder",
                        ColorHex = model.ColorHex ?? "#2196F3",
                        IsSystemCategory = model.IsSystemCategory,
                        DisplayOrder = model.DisplayOrder,
                        IsHidden = model.IsHidden,
                        ModifiedAt = remoteModifiedAt
                    };
                    await _categoryRepository.CreateAsync(category);
                    result.CategoriesCreated++;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"Failed to import category from '{Path.GetFileName(filePath)}': {ex.Message}");
            }
        }
    }

    protected override async Task ImportTemplatesAsync(string folderPath, SyncImportResult result, CancellationToken cancellationToken)
    {
        ImportSession session = _importSession
            ?? throw new InvalidOperationException("Templates are imported only through ImportDataFromYamlAsync.");
        var files = Directory.GetFiles(folderPath, "*.json", SearchOption.TopDirectoryOnly);
        int shippedIgnored = 0;

        foreach (var filePath in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (!ValidateFileSize(filePath))
                {
                    result.Warnings.Add($"File too large, skipped: {filePath}");
                    continue;
                }

                var json = await File.ReadAllTextAsync(filePath, cancellationToken);

                // SECURITY: Validate JSON against schema before processing
                var validationResult = JsonSchemaValidator.ValidateTemplate(json);
                if (!validationResult.IsValid)
                {
                    result.Warnings.Add($"Schema validation failed for '{Path.GetFileName(filePath)}': {string.Join("; ", validationResult.Errors.Take(3))}");
                    continue;
                }

                var model = JsonSerializer.Deserialize<TemplateModel>(json, _jsonOptions);

                if (model == null || model.Id == Guid.Empty)
                {
                    result.Warnings.Add($"Invalid template file: {filePath}");
                    continue;
                }

                // Only shipped action files use it: a shipped template an older build wrote.
                if (session.ActionFiles.IsUsedOnlyByShippedActions(model.Id))
                {
                    shippedIgnored++;
                    continue;
                }

                if (!Enum.TryParse<Platform>(model.Platform, true, out var platform))
                {
                    platform = Platform.Windows;
                }

                var parameters = (model.Parameters ?? new List<TemplateParameterModel>())
                    .Select(p => new TemplateParameter
                    {
                        Name = p.Name,
                        Label = p.Label,
                        Type = p.Type ?? TemplateParameterTypes.Text,
                        DefaultValue = p.DefaultValue,
                        Required = p.Required,
                        Description = p.Description,
                        AllowedValues = p.AllowedValues is { Count: > 0 } ? p.AllowedValues.ToList() : null,
                        Quoting = Enum.TryParse<QuotingMode>(p.Quoting, ignoreCase: true, out var quoting) ? quoting : null
                    }).ToList();

                var existing = await _templateRepository.GetByPublicIdAsync(model.Id);

                if (existing != null)
                {
                    // Update existing, once the actions have decided (see ApplyPendingTemplateUpdatesAsync)
                    existing.Name = model.Name;
                    existing.Platform = platform;
                    existing.CommandPattern = model.CommandPattern;
                    existing.Parameters = parameters;

                    session.PendingTemplateUpdates[model.Id] = existing;
                }
                else
                {
                    // Create new
                    var template = new CommandTemplate
                    {
                        Id = Guid.NewGuid().ToString(),
                        PublicId = model.Id,
                        Name = model.Name,
                        Platform = platform,
                        CommandPattern = model.CommandPattern,
                        Parameters = parameters
                    };
                    await _templateRepository.AddAsync(template);
                    result.TemplatesCreated++;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"Failed to import template from '{Path.GetFileName(filePath)}': {ex.Message}");
            }
        }

        AddShippedFilesIgnoredWarning(result, shippedIgnored, "template");
    }

    protected override async Task ImportActionsAsync(string folderPath, SyncImportResult result, CancellationToken cancellationToken)
    {
        ImportSession session = _importSession
            ?? throw new InvalidOperationException("Actions are imported only through ImportDataFromYamlAsync.");

        // Recursively find all JSON files (organized by category subfolders)
        var files = Directory.GetFiles(folderPath, "*.json", SearchOption.AllDirectories);
        int shippedIgnored = 0;

        foreach (var filePath in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (!ValidateFileSize(filePath))
                {
                    result.Warnings.Add($"File too large, skipped: {filePath}");
                    continue;
                }

                var json = await File.ReadAllTextAsync(filePath, cancellationToken);

                // SECURITY: Validate JSON against schema before processing
                var validationResult = JsonSchemaValidator.ValidateAction(json);
                if (!validationResult.IsValid)
                {
                    result.Warnings.Add($"Schema validation failed for '{Path.GetFileName(filePath)}': {string.Join("; ", validationResult.Errors.Take(3))}");
                    continue;
                }

                var model = JsonSerializer.Deserialize<ActionModel>(json, _jsonOptions);

                if (model == null || model.Id == Guid.Empty)
                {
                    result.Warnings.Add($"Invalid action file: {filePath}");
                    continue;
                }

                // A shipped action an older build wrote: this install ships its own copy, under
                // its own public id, and importing this one would make it a second.
                if (model.IsUserCreated == false)
                {
                    shippedIgnored++;
                    continue;
                }

                DateTime nowUtc = DateTime.UtcNow;
                DateTime? normalizedUpdatedAt = model.UpdatedAt;
                if (normalizedUpdatedAt.HasValue)
                {
                    DateTime clamped = ImportTimestampNormalizer.ClampFutureToNow(
                        normalizedUpdatedAt.Value,
                        nowUtc,
                        ImportTimestampNormalizer.DefaultFutureSkew,
                        out bool wasClamped);
                    if (wasClamped)
                    {
                        normalizedUpdatedAt = clamped;
                        result.Warnings.Add($"Action '{model.Title}' had a future updatedAt timestamp; clamped to import time.");
                    }
                }

                if (!Enum.TryParse<Platform>(model.Platform, true, out var platform))
                {
                    platform = Platform.Windows;
                }

                // Accepts the legacy "Warning" and "Danger" an older schema declared.
                if (!CriticalityLevelNames.TryParse(model.Level, out var level))
                {
                    level = CriticalityLevel.Info;
                }

                // Resolve template references
                CommandTemplate? windowsTemplate = null;
                CommandTemplate? linuxTemplate = null;

                if (model.WindowsTemplateId.HasValue)
                {
                    windowsTemplate = await _templateRepository.GetByPublicIdAsync(model.WindowsTemplateId.Value);
                }

                if (model.LinuxTemplateId.HasValue)
                {
                    linuxTemplate = await _templateRepository.GetByPublicIdAsync(model.LinuxTemplateId.Value);
                }

                var existing = await _actionRepository.GetByPublicIdAsync(model.Id);

                if (existing != null)
                {
                    // Conflict detection: check if local is newer than remote
                    DateTime remoteUpdatedAt = normalizedUpdatedAt ?? DateTime.MinValue;
                    if (existing.UpdatedAt > remoteUpdatedAt)
                    {
                        // Local is newer - this is a conflict
                        result.Conflicts.Add(new SyncEntityConflict
                        {
                            EntityType = "Action",
                            EntityId = model.Id,
                            EntityName = model.Title,
                            FilePath = filePath,
                            LocalUpdatedAt = existing.UpdatedAt,
                            RemoteUpdatedAt = remoteUpdatedAt,
                            Resolution = SyncConflictResolution.KeepLocal
                        });
                        result.ActionsSkipped++;
                        result.Warnings.Add($"Conflict: Action '{model.Title}' - local is newer, kept local version");
                        if (existing.WindowsCommandTemplate is { } localWindowsTemplate)
                        {
                            session.KeepLocalTemplateIds.Add(localWindowsTemplate.PublicId);
                        }

                        if (existing.LinuxCommandTemplate is { } localLinuxTemplate)
                        {
                            session.KeepLocalTemplateIds.Add(localLinuxTemplate.PublicId);
                        }

                        continue;
                    }

                    // Update existing (remote is newer or same)
                    existing.Title = model.Title;
                    existing.Description = model.Description;
                    existing.Category = CategoryMigration.Normalize(model.Category);
                    existing.Platform = platform;
                    existing.Level = level;
                    existing.Tags = model.Tags ?? new List<string>();
                    existing.WindowsCommandTemplate = windowsTemplate;
                    existing.LinuxCommandTemplate = linuxTemplate;
                    existing.Examples = ParseExamples(model.Examples);
                    existing.WindowsExamples = ParseExamples(model.WindowsExamples);
                    existing.LinuxExamples = ParseExamples(model.LinuxExamples);
                    existing.Notes = model.Notes;
                    existing.Links = ParseLinks(model.Links);
                    existing.IsUserCreated = true;
                    existing.UpdatedAt = normalizedUpdatedAt ?? nowUtc;
                    existing.Translations = model.Translations ?? new List<ActionTranslation>();

                    await _actionRepository.UpdateAsync(existing);
                    result.ActionsUpdated++;
                }
                else
                {
                    // Create new
                    var action = new Core.Models.Action
                    {
                        Id = Guid.NewGuid().ToString(),
                        PublicId = model.Id,
                        Title = model.Title,
                        Description = model.Description,
                        Category = CategoryMigration.Normalize(model.Category),
                        Platform = platform,
                        Level = level,
                        Tags = model.Tags ?? new List<string>(),
                        WindowsCommandTemplate = windowsTemplate,
                        LinuxCommandTemplate = linuxTemplate,
                        Examples = ParseExamples(model.Examples),
                        WindowsExamples = ParseExamples(model.WindowsExamples),
                        LinuxExamples = ParseExamples(model.LinuxExamples),
                        Notes = model.Notes,
                        Links = ParseLinks(model.Links),
                        IsUserCreated = true,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = normalizedUpdatedAt ?? nowUtc,
                        Translations = model.Translations ?? new List<ActionTranslation>()
                    };
                    await _actionRepository.AddAsync(action);
                    result.ActionsCreated++;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"Failed to import action from '{Path.GetFileName(filePath)}': {ex.Message}");
            }
        }

        AddShippedFilesIgnoredWarning(result, shippedIgnored, "action");
    }

    protected override async Task ImportBatchesAsync(string folderPath, SyncImportResult result, CancellationToken cancellationToken)
    {
        var files = Directory.GetFiles(folderPath, "*.json", SearchOption.TopDirectoryOnly);
        int shippedIgnored = 0;

        foreach (var filePath in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (!ValidateFileSize(filePath))
                {
                    result.Warnings.Add($"File too large, skipped: {filePath}");
                    continue;
                }

                var json = await File.ReadAllTextAsync(filePath, cancellationToken);

                // SECURITY: Validate JSON against schema before processing
                var validationResult = JsonSchemaValidator.ValidateBatch(json);
                if (!validationResult.IsValid)
                {
                    result.Warnings.Add($"Schema validation failed for '{Path.GetFileName(filePath)}': {string.Join("; ", validationResult.Errors.Take(3))}");
                    continue;
                }

                var model = JsonSerializer.Deserialize<BatchModel>(json, _jsonOptions);

                if (model == null || model.Id == Guid.Empty)
                {
                    result.Warnings.Add($"Invalid batch file: {filePath}");
                    continue;
                }

                if (model.IsUserCreated == false)
                {
                    shippedIgnored++;
                    continue;
                }

                DateTime nowUtc = DateTime.UtcNow;
                DateTime? normalizedUpdatedAt = model.UpdatedAt;
                if (normalizedUpdatedAt.HasValue)
                {
                    DateTime clamped = ImportTimestampNormalizer.ClampFutureToNow(
                        normalizedUpdatedAt.Value,
                        nowUtc,
                        ImportTimestampNormalizer.DefaultFutureSkew,
                        out bool wasClamped);
                    if (wasClamped)
                    {
                        normalizedUpdatedAt = clamped;
                        result.Warnings.Add($"Batch '{model.Name}' had a future updatedAt timestamp; clamped to import time.");
                    }
                }

                if (!Enum.TryParse<BatchExecutionMode>(model.ExecutionMode, true, out var executionMode))
                {
                    executionMode = BatchExecutionMode.StopOnError;
                }

                var commands = new List<BatchCommandItem>();
                foreach (BatchCommandModel c in model.Commands ?? new List<BatchCommandModel>())
                {
                    commands.Add(new BatchCommandItem
                    {
                        Id = c.Id ?? Guid.NewGuid().ToString(),
                        Order = c.Order,
                        ActionId = await ResolveStepActionIdAsync(c.ActionPublicId, c.ActionId),
                        ActionTitle = c.ActionTitle,
                        Command = c.Command,
                        Platform = Enum.TryParse<Platform>(c.Platform, true, out var p) ? p : Platform.Windows,
                        Description = c.Description,
                        Parameters = new Dictionary<string, string>(
                            c.Parameters ?? new Dictionary<string, string>(),
                            StringComparer.Ordinal)
                    });
                }

                var variables = new Dictionary<string, string>(
                    model.Variables ?? new Dictionary<string, string>(),
                    StringComparer.Ordinal);

                var existing = await _batchRepository.GetByPublicIdAsync(model.Id);

                if (existing != null)
                {
                    // Conflict detection: check if local is newer than remote
                    DateTime remoteUpdatedAt = normalizedUpdatedAt ?? DateTime.MinValue;
                    if (existing.UpdatedAt > remoteUpdatedAt)
                    {
                        // Local is newer - this is a conflict
                        result.Conflicts.Add(new SyncEntityConflict
                        {
                            EntityType = "Batch",
                            EntityId = model.Id,
                            EntityName = model.Name,
                            FilePath = filePath,
                            LocalUpdatedAt = existing.UpdatedAt,
                            RemoteUpdatedAt = remoteUpdatedAt,
                            Resolution = SyncConflictResolution.KeepLocal
                        });
                        result.BatchesSkipped++;
                        result.Warnings.Add($"Conflict: Batch '{model.Name}' - local is newer, kept local version");
                        continue;
                    }

                    // Update existing (remote is newer or same)
                    existing.Name = model.Name;
                    existing.Description = model.Description;
                    existing.ExecutionMode = executionMode;
                    existing.Commands = commands;
                    existing.Variables = variables;
                    existing.Tags = model.Tags ?? new List<string>();
                    existing.IsUserCreated = true;
                    existing.UpdatedAt = normalizedUpdatedAt ?? nowUtc;

                    await _batchRepository.UpdateAsync(existing);
                    result.BatchesUpdated++;
                }
                else
                {
                    // Create new
                    var batch = new CommandBatch
                    {
                        Id = Guid.NewGuid().ToString(),
                        PublicId = model.Id,
                        Name = model.Name,
                        Description = model.Description,
                        ExecutionMode = executionMode,
                        Commands = commands,
                        Variables = variables,
                        Tags = model.Tags ?? new List<string>(),
                        IsUserCreated = true,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = normalizedUpdatedAt ?? nowUtc
                    };
                    await _batchRepository.AddAsync(batch);
                    result.BatchesCreated++;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"Failed to import batch from '{Path.GetFileName(filePath)}': {ex.Message}");
            }
        }

        AddShippedFilesIgnoredWarning(result, shippedIgnored, "batch");
    }

    /// <summary>
    /// The local id of the action a runbook step names in the folder.
    /// </summary>
    /// <remarks>
    /// A step carries both the public id of its action, which every install knows it by, and
    /// the local id of the install that wrote it, which only a shipped action shares across
    /// installs. The public id is tried first, then the local id; a step that resolves to
    /// neither keeps the local id it carried and reads as one whose action is missing, as it
    /// always has.
    /// </remarks>
    private async Task<string?> ResolveStepActionIdAsync(Guid? actionPublicId, string? actionId)
    {
        if (actionPublicId is { } publicId
            && await _actionRepository.GetByPublicIdAsync(publicId) is { } action)
        {
            return action.Id;
        }

        return actionId;
    }

    #endregion

    #region Validation

    public override async Task<SyncValidationResult> ValidateFolderAsync(string rootFolderPath, CancellationToken cancellationToken = default)
    {
        var result = new SyncValidationResult { IsValid = true };

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!Directory.Exists(rootFolderPath))
            {
                result.IsValid = false;
                result.Errors.Add($"Folder does not exist: {rootFolderPath}");
                return result;
            }

            await RunValidationPipelineAsync(
                rootFolderPath,
                result,
                "No JSON files found in the folder structure.",
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            result.IsValid = false;
            result.Errors.Add($"Validation failed: {ex.Message}");
        }

        return result;
    }

    protected override Task<int> ValidateCategoriesAsync(
        string folderPath,
        SyncValidationResult result,
        CancellationToken cancellationToken)
        => ValidateJsonFilesAsync<CategoryModel>(
            folderPath,
            result,
            "category",
            JsonSchemaValidator.ValidateCategory,
            cancellationToken);

    protected override Task<int> ValidateTemplatesAsync(
        string folderPath,
        SyncValidationResult result,
        CancellationToken cancellationToken)
        => ValidateJsonFilesAsync<TemplateModel>(
            folderPath,
            result,
            "template",
            JsonSchemaValidator.ValidateTemplate,
            cancellationToken);

    protected override Task<int> ValidateActionsAsync(
        string folderPath,
        SyncValidationResult result,
        CancellationToken cancellationToken)
        => ValidateJsonFilesAsync<ActionModel>(
            folderPath,
            result,
            "action",
            JsonSchemaValidator.ValidateAction,
            cancellationToken,
            SearchOption.AllDirectories);

    protected override Task<int> ValidateBatchesAsync(
        string folderPath,
        SyncValidationResult result,
        CancellationToken cancellationToken)
        => ValidateJsonFilesAsync<BatchModel>(
            folderPath,
            result,
            "batch",
            JsonSchemaValidator.ValidateBatch,
            cancellationToken);

    private async Task<int> ValidateJsonFilesAsync<T>(
        string folderPath,
        SyncValidationResult result,
        string entityType,
        Func<string, SchemaValidationResult> schemaValidate,
        CancellationToken cancellationToken,
        SearchOption searchOption = SearchOption.TopDirectoryOnly)
    {
        string[] files = Directory.GetFiles(folderPath, "*.json", searchOption);
        int validCount = 0;

        foreach (string filePath in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (!ValidateFileSize(filePath))
                {
                    result.Warnings.Add($"File too large: {Path.GetFileName(filePath)}");
                    continue;
                }

                string json = await File.ReadAllTextAsync(filePath, cancellationToken);

                SchemaValidationResult schemaResult = schemaValidate(json);
                if (!schemaResult.IsValid)
                {
                    result.Warnings.Add(
                        $"Schema validation failed for {entityType} file '{Path.GetFileName(filePath)}': "
                        + string.Join("; ", schemaResult.Errors));
                    continue;
                }

                T? model = JsonSerializer.Deserialize<T>(json, _jsonOptions);

                if (model != null)
                {
                    validCount++;
                }
                else
                {
                    result.Warnings.Add($"Invalid {entityType} file: {Path.GetFileName(filePath)}");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"Failed to parse {entityType} file '{Path.GetFileName(filePath)}': {ex.Message}");
            }
        }

        return validCount;
    }

    #endregion

    #region Helpers

    private async Task WriteJsonFileAsync<T>(string filePath, T model, CancellationToken cancellationToken)
    {
        string json = JsonSerializer.Serialize(model, _jsonOptions);
        string tempPath = filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            await File.WriteAllTextAsync(tempPath, json, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(tempPath, filePath, overwrite: true);
        }
        finally
        {
            DeleteTempFileIfExists(tempPath);
        }
    }

    private static void DeleteTempFileIfExists(string tempPath)
    {
        if (!File.Exists(tempPath))
        {
            return;
        }

        try
        {
            File.Delete(tempPath);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Deletes the managed JSON files of <paramref name="folderPath"/> this export did not write,
    /// when deleting them loses nothing.
    /// </summary>
    /// <remarks>
    /// A file goes when a tombstone says its entry was deleted, when it is a second copy of an
    /// entry just written under another name or folder, or when <paramref name="isShippedCopy"/>
    /// recognises a shipped entry an older build wrote. Any other file is one this install
    /// did not write (skipped at import, written by a newer build) and stays: deleting it would
    /// push its loss to everybody. A file whose id cannot be read stays for the same reason.
    /// Underscore-prefixed JSON files, <c>_deleted.json</c> among them, and non-JSON files are
    /// not managed and always stay.
    /// </remarks>
    private void RemoveStaleFiles(
        string folderPath,
        bool recursive,
        ExportPass pass,
        Func<SyncFileIdentity, bool> isShippedCopy,
        SyncExportResult result)
    {
        if (!Directory.Exists(folderPath))
        {
            return;
        }

        SearchOption searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        string[] files = Directory.GetFiles(folderPath, "*.json", searchOption);
        List<string> kept = [];

        foreach (string file in files)
        {
            string fileName = Path.GetFileName(file);
            if (fileName.StartsWith("_", StringComparison.Ordinal))
            {
                continue;
            }

            string fullPath = Path.GetFullPath(file);
            if (pass.WrittenFullPaths.Contains(fullPath))
            {
                continue;
            }

            if (!TryReadFileIdentity(file, out SyncFileIdentity identity))
            {
                kept.Add(fileName);
                continue;
            }

            // Its entry failed to export this time: the file is the last good copy of it.
            if (pass.FailedIds.Contains(identity.Id))
            {
                continue;
            }

            bool deletable = isShippedCopy(identity)
                || pass.CurrentIds.Contains(identity.Id)
                || pass.Deleted.ContainsKey(identity.Id);
            if (!deletable)
            {
                kept.Add(fileName);
                continue;
            }

            try
            {
                File.Delete(file);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"Failed to remove stale file '{fileName}': {ex.Message}");
            }
        }

        if (kept.Count > 0)
        {
            string listed = string.Join(", ", kept.Take(MaxKeptFileNamesListed));
            string more = kept.Count > MaxKeptFileNamesListed ? ", ..." : string.Empty;
            result.Warnings.Add(
                $"Kept {kept.Count} file(s) in '{Path.GetFileName(folderPath)}' that this export did not write "
                + $"(skipped at import, unreadable, or written by a newer build): {listed}{more}");
        }

        if (recursive)
        {
            RemoveEmptySubdirectories(folderPath, result);
        }
    }

    /// <summary>
    /// Reads, without the schema, the id a sync file carries and what it says about itself, so a
    /// file this build would reject can still be told apart from one it may delete.
    /// </summary>
    private static bool TryReadFileIdentity(string filePath, out SyncFileIdentity identity)
    {
        identity = default;

        try
        {
            if (!ValidateFileSize(filePath))
            {
                return false;
            }

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(filePath));
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || ReadGuidProperty(root, "id") is not { } publicId
                || publicId == Guid.Empty)
            {
                return false;
            }

            identity = new SyncFileIdentity(
                publicId,
                ReadBooleanProperty(root, "isUserCreated"),
                ReadBooleanProperty(root, "isSystemCategory"),
                ReadGuidProperty(root, "windowsTemplateId"),
                ReadGuidProperty(root, "linuxTemplateId"));
            return true;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static JsonElement? FindProperty(JsonElement element, string name)
    {
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        return null;
    }

    private static Guid? ReadGuidProperty(JsonElement element, string name)
        => FindProperty(element, name) is { ValueKind: JsonValueKind.String } value
            && Guid.TryParse(value.GetString(), out Guid parsed)
                ? parsed
                : null;

    private static bool? ReadBooleanProperty(JsonElement element, string name)
        => FindProperty(element, name) switch
        {
            { ValueKind: JsonValueKind.True } => true,
            { ValueKind: JsonValueKind.False } => false,
            _ => null
        };

    /// <summary>
    /// Reads which templates the action files of the folder use, shipped and user ones apart.
    /// </summary>
    private static ActionFileScan ScanActionFiles(string actionsPath)
    {
        ActionFileScan scan = new();
        if (!Directory.Exists(actionsPath))
        {
            return scan;
        }

        foreach (string filePath in Directory.GetFiles(actionsPath, "*.json", SearchOption.AllDirectories))
        {
            if (!TryReadFileIdentity(filePath, out SyncFileIdentity identity))
            {
                scan.HasUnreadableFile = true;
                continue;
            }

            HashSet<Guid> uses = identity.IsUserCreated == false ? scan.ShippedTemplateIds : scan.UserTemplateIds;
            if (identity.WindowsTemplateId is { } windowsTemplateId)
            {
                uses.Add(windowsTemplateId);
            }

            if (identity.LinuxTemplateId is { } linuxTemplateId)
            {
                uses.Add(linuxTemplateId);
            }
        }

        return scan;
    }

    private static void AddShippedFilesIgnoredWarning(SyncImportResult result, int count, string entityType)
    {
        if (count > 0)
        {
            result.Warnings.Add(
                $"Ignored {count} shipped {entityType} file(s) written by an older build: every install ships its own copy.");
        }
    }

    private static void RemoveEmptySubdirectories(string rootFolderPath, SyncExportResult result)
    {
        string[] directories = Directory.GetDirectories(rootFolderPath, "*", SearchOption.AllDirectories)
            .OrderByDescending(directory => directory.Length)
            .ToArray();

        foreach (string directory in directories)
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    Directory.Delete(directory);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"Failed to remove empty folder '{Path.GetFileName(directory)}': {ex.Message}");
            }
        }
    }

    private static string SanitizeFileName(string name)
    {
        string sanitized = SanitizeFileNameCore(name);
        return string.IsNullOrWhiteSpace(sanitized) ? Guid.NewGuid().ToString() : sanitized;
    }

    private static string BuildEntityFileName(string name, Guid publicId)
    {
        string sanitized = SanitizeFileNameCore(name);

        // Export filenames now include PublicId, so the first export after this change
        // renames existing files; the follow-up orphan cleanup removes old names.
        return string.IsNullOrWhiteSpace(sanitized)
            ? $"{publicId:N}.json"
            : $"{sanitized}-{publicId:N}.json";
    }

    private static List<CommandExample> ParseExamples(List<ExampleModel>? models)
    {
        if (models == null) return new List<CommandExample>();

        return models.Select(e => new CommandExample
        {
            Command = e.Command,
            Description = e.Description,
            Platform = Enum.TryParse<Platform>(e.Platform, true, out var p) ? p : Platform.Both
        }).ToList();
    }

    private static List<ExternalLink> ParseLinks(List<LinkModel>? models)
    {
        if (models == null) return new List<ExternalLink>();

        return models.Select(l => new ExternalLink
        {
            Title = l.Title,
            Url = l.Url
        }).ToList();
    }

    #endregion

    #region Sync state types

    /// <summary>What an operator made: the part of the database that travels.</summary>
    private sealed record UserContent(
        IReadOnlyList<CustomCategory> Categories,
        IReadOnlyList<CommandTemplate> Templates,
        IReadOnlyList<Core.Models.Action> Actions,
        IReadOnlyList<CommandBatch> Batches,
        IReadOnlyDictionary<string, Core.Models.Action> ActionsById)
    {
        public IEnumerable<Guid> PublicIds
            => Categories.Select(c => c.PublicId)
                .Concat(Templates.Select(t => t.PublicId))
                .Concat(Actions.Select(a => a.PublicId))
                .Concat(Batches.Select(b => b.PublicId));
    }

    /// <summary>What one folder's export wrote and may delete.</summary>
    private sealed class ExportPass
    {
        public ExportPass(IReadOnlyDictionary<Guid, DateTime> deleted, IEnumerable<Guid> currentIds)
        {
            Deleted = deleted;
            CurrentIds = currentIds.ToHashSet();
        }

        /// <summary>The tombstones: the entries deleted here or on another install.</summary>
        public IReadOnlyDictionary<Guid, DateTime> Deleted { get; }

        /// <summary>The ids of the entries this export writes.</summary>
        public HashSet<Guid> CurrentIds { get; }

        /// <summary>The ids whose file could not be written this time.</summary>
        public HashSet<Guid> FailedIds { get; } = new();

        public HashSet<string> WrittenFullPaths { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The deletes a sync knows of: the folder's <c>_deleted.json</c> and this database's own.</summary>
    private sealed class TombstoneSet
    {
        public Dictionary<Guid, DateTime> Entries { get; } = new();

        /// <summary>False when the folder's file exists but could not be read: it is then never rewritten.</summary>
        public bool FileReadable { get; set; } = true;

        /// <summary>The folder's file as read, so an unchanged set is not rewritten.</summary>
        public string? FileText { get; set; }
    }

    /// <summary>The shape of <c>_deleted.json</c>.</summary>
    private sealed class TombstoneFileModel
    {
        public List<TombstoneModel> Tombstones { get; set; } = new();
    }

    private sealed class TombstoneModel
    {
        public Guid Id { get; set; }

        public DateTime DeletedAt { get; set; }
    }

    /// <summary>The templates the action files of a folder use.</summary>
    private sealed class ActionFileScan
    {
        public HashSet<Guid> UserTemplateIds { get; } = new();

        public HashSet<Guid> ShippedTemplateIds { get; } = new();

        /// <summary>An action file whose references could not be read: it may use any template.</summary>
        public bool HasUnreadableFile { get; set; }

        public bool IsUsedOnlyByShippedActions(Guid templateId)
            => ShippedTemplateIds.Contains(templateId) && !UserTemplateIds.Contains(templateId);
    }

    /// <summary>What a sync file says about itself, read without the schema.</summary>
    private readonly record struct SyncFileIdentity(
        Guid Id,
        bool? IsUserCreated,
        bool? IsSystemCategory,
        Guid? WindowsTemplateId,
        Guid? LinuxTemplateId);

    /// <summary>The state of one import across its passes.</summary>
    private sealed class ImportSession
    {
        public ImportSession(ActionFileScan actionFiles)
        {
            ActionFiles = actionFiles;
        }

        public ActionFileScan ActionFiles { get; }

        /// <summary>
        /// Template updates read from the folder, written once the actions have decided.
        /// </summary>
        public Dictionary<Guid, CommandTemplate> PendingTemplateUpdates { get; } = new();

        /// <summary>The templates of actions whose local copy was kept.</summary>
        public HashSet<Guid> KeepLocalTemplateIds { get; } = new();
    }

    #endregion

    #region JSON Models

    private sealed class CategoryModel
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string? IconKey { get; set; }
        public string? ColorHex { get; set; }
        public bool IsSystemCategory { get; set; }
        public int DisplayOrder { get; set; }
        public bool IsHidden { get; set; }

        /// <summary>Timestamp for conflict detection during sync; absent from older files.</summary>
        public DateTime? ModifiedAt { get; set; }
    }

    private sealed class TemplateModel
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Platform { get; set; } = "Windows";
        public string CommandPattern { get; set; } = string.Empty;
        public List<TemplateParameterModel>? Parameters { get; set; }
    }

    private sealed class TemplateParameterModel
    {
        public string Name { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public string Type { get; set; } = "string";
        public string? DefaultValue { get; set; }
        public bool Required { get; set; }
        public string? Description { get; set; }

        /// <summary>The values a choice parameter offers; null for any other type.</summary>
        public List<string>? AllowedValues { get; set; }

        /// <summary>The quoting override, by <see cref="QuotingMode"/> name; null for the default.</summary>
        public string? Quoting { get; set; }
    }

    private sealed class ActionModel
    {
        public Guid Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string Platform { get; set; } = "Windows";
        public string Level { get; set; } = "Info";
        public List<string>? Tags { get; set; }
        public Guid? WindowsTemplateId { get; set; }
        public Guid? LinuxTemplateId { get; set; }
        public List<ExampleModel>? Examples { get; set; }
        public List<ExampleModel>? WindowsExamples { get; set; }
        public List<ExampleModel>? LinuxExamples { get; set; }
        public string? Notes { get; set; }
        public List<LinkModel>? Links { get; set; }

        /// <summary>False on a shipped action an older build wrote; null when the file says nothing.</summary>
        public bool? IsUserCreated { get; set; }
        /// <summary>Timestamp for conflict detection during sync</summary>
        public DateTime? UpdatedAt { get; set; }

        /// <summary>
        /// Other languages of the text above, in the same shape as the shipped seeds and the
        /// library export: an object keyed by culture. Null, and so absent from the file,
        /// when the action has none.
        /// </summary>
        [JsonConverter(typeof(ActionTranslationsJsonConverter))]
        public List<ActionTranslation>? Translations { get; set; }
    }

    private sealed class ExampleModel
    {
        public string Command { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string? Platform { get; set; }
    }

    private sealed class LinkModel
    {
        public string Title { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
    }

    private sealed class BatchModel
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string ExecutionMode { get; set; } = "StopOnError";
        public List<string>? Tags { get; set; }
        public List<BatchCommandModel>? Commands { get; set; }
        /// <summary>Runbook variables with their default values</summary>
        public Dictionary<string, string>? Variables { get; set; }

        /// <summary>False on a shipped runbook an older build wrote; null when the file says nothing.</summary>
        public bool? IsUserCreated { get; set; }
        /// <summary>Timestamp for conflict detection during sync</summary>
        public DateTime? UpdatedAt { get; set; }
    }

    private sealed class BatchCommandModel
    {
        public string? Id { get; set; }
        public int Order { get; set; }
        public string? ActionId { get; set; }

        /// <summary>The public id of the step's action, which resolves on every install.</summary>
        public Guid? ActionPublicId { get; set; }
        public string ActionTitle { get; set; } = string.Empty;
        public string Command { get; set; } = string.Empty;
        public string? Platform { get; set; }
        public string? Description { get; set; }
        /// <summary>Parameter values of a step that references an action</summary>
        public Dictionary<string, string>? Parameters { get; set; }
    }

    #endregion
}
