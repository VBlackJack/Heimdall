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
    private readonly JsonSerializerOptions _jsonOptions;

    public JsonSyncService(
        IActionRepository actionRepository,
        IBatchRepository batchRepository,
        ICustomCategoryRepository categoryRepository,
        ICommandTemplateRepository templateRepository,
        IUnitOfWork? unitOfWork = null,
        ILogger<JsonSyncService>? logger = null)
    {
        _actionRepository = actionRepository;
        _batchRepository = batchRepository;
        _categoryRepository = categoryRepository;
        _templateRepository = templateRepository;
        _unitOfWork = unitOfWork;
        _logger = logger;
        _jsonOptions = JsonOptionsHelper.SyncService;
    }

    #region Export

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

            // Export categories first (they may be referenced by actions)
            cancellationToken.ThrowIfCancellationRequested();
            result.CategoriesExported = await ExportCategoriesAsync(rootFolderPath, result, cancellationToken);

            // Export templates (they may be referenced by actions)
            cancellationToken.ThrowIfCancellationRequested();
            result.TemplatesExported = await ExportTemplatesAsync(rootFolderPath, result, cancellationToken);

            // Export actions (organized by category)
            cancellationToken.ThrowIfCancellationRequested();
            result.ActionsExported = await ExportActionsAsync(rootFolderPath, result, cancellationToken);

            // Export batches
            cancellationToken.ThrowIfCancellationRequested();
            result.BatchesExported = await ExportBatchesAsync(rootFolderPath, result, cancellationToken);
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

    private async Task<int> ExportCategoriesAsync(string rootFolderPath, SyncExportResult result, CancellationToken cancellationToken)
    {
        var categoriesPath = Path.Combine(rootFolderPath, CategoriesFolderName);
        var categories = await _categoryRepository.GetAllAsync();
        HashSet<string> writtenFullPaths = new(StringComparer.OrdinalIgnoreCase);
        int count = 0;

        foreach (var category in categories)
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
                    IsHidden = category.IsHidden
                };

                var fileName = BuildEntityFileName(category.Name, category.PublicId);
                var filePath = Path.Combine(categoriesPath, fileName);

                await WriteJsonFileAsync(filePath, model, cancellationToken);
                writtenFullPaths.Add(Path.GetFullPath(filePath));
                count++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"Failed to export category '{category.Name}': {ex.Message}");
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        RemoveOrphanFiles(categoriesPath, writtenFullPaths, recursive: false, result);

        return count;
    }

    private async Task<int> ExportTemplatesAsync(string rootFolderPath, SyncExportResult result, CancellationToken cancellationToken)
    {
        var templatesPath = Path.Combine(rootFolderPath, TemplatesFolderName);
        var templates = await _templateRepository.GetAllAsync();
        HashSet<string> writtenFullPaths = new(StringComparer.OrdinalIgnoreCase);
        int count = 0;

        foreach (var template in templates)
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
                    Description = p.Description
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
                writtenFullPaths.Add(Path.GetFullPath(filePath));
                count++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"Failed to export template '{template.Name}': {ex.Message}");
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        RemoveOrphanFiles(templatesPath, writtenFullPaths, recursive: false, result);

        return count;
    }

    private async Task<int> ExportActionsAsync(string rootFolderPath, SyncExportResult result, CancellationToken cancellationToken)
    {
        var actionsPath = Path.Combine(rootFolderPath, ActionsFolderName);
        var actions = await _actionRepository.GetAllWithTemplatesAsync();
        HashSet<string> writtenFullPaths = new(StringComparer.OrdinalIgnoreCase);

        int count = 0;

        foreach (var action in actions)
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
                    UpdatedAt = action.UpdatedAt
                };

                var fileName = BuildEntityFileName(action.Title, action.PublicId);
                var filePath = Path.Combine(categoryPath, fileName);

                await WriteJsonFileAsync(filePath, model, cancellationToken);
                writtenFullPaths.Add(Path.GetFullPath(filePath));
                count++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"Failed to export action '{action.Title}': {ex.Message}");
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        RemoveOrphanFiles(actionsPath, writtenFullPaths, recursive: true, result);

        return count;
    }

    private async Task<int> ExportBatchesAsync(string rootFolderPath, SyncExportResult result, CancellationToken cancellationToken)
    {
        var batchesPath = Path.Combine(rootFolderPath, BatchesFolderName);
        var batches = await _batchRepository.GetAllAsync();
        HashSet<string> writtenFullPaths = new(StringComparer.OrdinalIgnoreCase);
        int count = 0;

        foreach (var batch in batches)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var commands = batch.Commands.Select(c => new BatchCommandModel
                {
                    Id = c.Id,
                    Order = c.Order,
                    ActionId = c.ActionId,
                    ActionTitle = c.ActionTitle,
                    Command = c.Command,
                    Platform = c.Platform.ToString(),
                    Description = c.Description
                }).ToList();

                var model = new BatchModel
                {
                    Id = batch.PublicId,
                    Name = batch.Name,
                    Description = batch.Description,
                    ExecutionMode = batch.ExecutionMode.ToString(),
                    Tags = batch.Tags,
                    Commands = commands,
                    IsUserCreated = batch.IsUserCreated,
                    UpdatedAt = batch.UpdatedAt
                };

                var fileName = BuildEntityFileName(batch.Name, batch.PublicId);
                var filePath = Path.Combine(batchesPath, fileName);

                await WriteJsonFileAsync(filePath, model, cancellationToken);
                writtenFullPaths.Add(Path.GetFullPath(filePath));
                count++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"Failed to export batch '{batch.Name}': {ex.Message}");
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        RemoveOrphanFiles(batchesPath, writtenFullPaths, recursive: false, result);

        return count;
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

            await RunImportPipelineAsync(rootFolderPath, result, cancellationToken);

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

        return result;
    }

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

                var existing = await _categoryRepository.GetByPublicIdAsync(model.Id);

                if (existing != null)
                {
                    // Update existing
                    existing.Name = model.Name;
                    existing.Description = model.Description;
                    existing.IconKey = model.IconKey ?? "folder";
                    existing.ColorHex = model.ColorHex ?? "#2196F3";
                    existing.IsSystemCategory = model.IsSystemCategory;
                    existing.DisplayOrder = model.DisplayOrder;
                    existing.IsHidden = model.IsHidden;

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
                        IsHidden = model.IsHidden
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

                if (!Enum.TryParse<Platform>(model.Platform, true, out var platform))
                {
                    platform = Platform.Windows;
                }

                var parameters = (model.Parameters ?? new List<TemplateParameterModel>())
                    .Select(p => new TemplateParameter
                    {
                        Name = p.Name,
                        Label = p.Label,
                        Type = p.Type ?? "string",
                        DefaultValue = p.DefaultValue,
                        Required = p.Required,
                        Description = p.Description
                    }).ToList();

                var existing = await _templateRepository.GetByPublicIdAsync(model.Id);

                if (existing != null)
                {
                    // Update existing
                    existing.Name = model.Name;
                    existing.Platform = platform;
                    existing.CommandPattern = model.CommandPattern;
                    existing.Parameters = parameters;

                    await _templateRepository.UpdateAsync(existing);
                    result.TemplatesUpdated++;
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
    }

    protected override async Task ImportActionsAsync(string folderPath, SyncImportResult result, CancellationToken cancellationToken)
    {
        // Recursively find all JSON files (organized by category subfolders)
        var files = Directory.GetFiles(folderPath, "*.json", SearchOption.AllDirectories);

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

                if (!Enum.TryParse<CriticalityLevel>(model.Level, true, out var level))
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
                        continue;
                    }

                    // Update existing (remote is newer or same)
                    existing.Title = model.Title;
                    existing.Description = model.Description;
                    existing.Category = model.Category;
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
                    existing.IsUserCreated = model.IsUserCreated;
                    existing.UpdatedAt = normalizedUpdatedAt ?? nowUtc;

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
                        Category = model.Category,
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
                        IsUserCreated = model.IsUserCreated,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = normalizedUpdatedAt ?? nowUtc
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
    }

    protected override async Task ImportBatchesAsync(string folderPath, SyncImportResult result, CancellationToken cancellationToken)
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

                var commands = (model.Commands ?? new List<BatchCommandModel>())
                    .Select(c => new BatchCommandItem
                    {
                        Id = c.Id ?? Guid.NewGuid().ToString(),
                        Order = c.Order,
                        ActionId = c.ActionId,
                        ActionTitle = c.ActionTitle,
                        Command = c.Command,
                        Platform = Enum.TryParse<Platform>(c.Platform, true, out var p) ? p : Platform.Windows,
                        Description = c.Description
                    }).ToList();

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
                    existing.Tags = model.Tags ?? new List<string>();
                    existing.IsUserCreated = model.IsUserCreated;
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
                        Tags = model.Tags ?? new List<string>(),
                        IsUserCreated = model.IsUserCreated,
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

    private void RemoveOrphanFiles(
        string folderPath,
        HashSet<string> writtenFullPaths,
        bool recursive,
        SyncExportResult result)
    {
        if (!Directory.Exists(folderPath))
        {
            return;
        }

        // Export is authoritative for managed JSON files in these folders.
        // Underscore-prefixed JSON files and non-JSON files are intentionally preserved.
        SearchOption searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        string[] files = Directory.GetFiles(folderPath, "*.json", searchOption);

        foreach (string file in files)
        {
            string fileName = Path.GetFileName(file);
            if (fileName.StartsWith("_", StringComparison.Ordinal))
            {
                continue;
            }

            string fullPath = Path.GetFullPath(file);
            if (writtenFullPaths.Contains(fullPath))
            {
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
                result.Warnings.Add($"Failed to remove stale file '{Path.GetFileName(file)}': {ex.Message}");
            }
        }

        if (recursive)
        {
            RemoveEmptySubdirectories(folderPath, result);
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
        public bool IsUserCreated { get; set; }
        /// <summary>Timestamp for conflict detection during sync</summary>
        public DateTime? UpdatedAt { get; set; }
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
        public bool IsUserCreated { get; set; }
        /// <summary>Timestamp for conflict detection during sync</summary>
        public DateTime? UpdatedAt { get; set; }
    }

    private sealed class BatchCommandModel
    {
        public string? Id { get; set; }
        public int Order { get; set; }
        public string? ActionId { get; set; }
        public string ActionTitle { get; set; } = string.Empty;
        public string Command { get; set; } = string.Empty;
        public string? Platform { get; set; }
        public string? Description { get; set; }
    }

    #endregion
}
