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
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using TwinShell.Core.Enums;
using TwinShell.Core.Helpers;
using TwinShell.Core.Interfaces;
using TwinShell.Persistence;
using TwinShell.Persistence.Entities;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace TwinShell.Infrastructure.Services;

/// <summary>
/// Service for GitOps synchronization of TwinShell data via YAML files.
/// Enables collaborative editing through Git-synchronized folders.
/// </summary>
public sealed class YamlSyncService : ISyncService
{
    private readonly TwinShellDbContext _dbContext;
    private readonly ISerializer _yamlSerializer;
    private readonly IDeserializer _yamlDeserializer;

    // Folder structure constants
    private const string ActionsFolderName = "actions";
    private const string BatchesFolderName = "batches";
    private const string TemplatesFolderName = "templates";
    private const string CategoriesFolderName = "categories";

    // File size limit for security (100KB per individual sync file)
    private const long MaxFileSizeBytes = 100 * 1024; // 100 KB

    public YamlSyncService(TwinShellDbContext dbContext)
    {
        _dbContext = dbContext;

        _yamlSerializer = new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .DisableAliases()
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
            .Build();

        _yamlDeserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
    }

    #region Export

    public async Task<SyncExportResult> ExportDataToYamlAsync(string rootFolderPath, CancellationToken cancellationToken = default)
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
        var categories = await _dbContext.CustomCategories.ToListAsync(cancellationToken);
        int count = 0;

        foreach (var category in categories)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var yamlModel = new CategoryYamlModel
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

                var fileName = SanitizeFileName(category.Name) + ".yaml";
                var filePath = Path.Combine(categoriesPath, fileName);

                await WriteYamlFileAsync(filePath, yamlModel, cancellationToken);
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

        return count;
    }

    private async Task<int> ExportTemplatesAsync(string rootFolderPath, SyncExportResult result, CancellationToken cancellationToken)
    {
        var templatesPath = Path.Combine(rootFolderPath, TemplatesFolderName);
        var templates = await _dbContext.CommandTemplates.ToListAsync(cancellationToken);
        int count = 0;

        foreach (var template in templates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var parameters = DeserializeJson<List<TemplateParameterYaml>>(template.ParametersJson) ?? new();

                var yamlModel = new TemplateYamlModel
                {
                    Id = template.PublicId,
                    Name = template.Name,
                    Platform = template.Platform.ToString(),
                    CommandPattern = template.CommandPattern,
                    Parameters = parameters
                };

                var fileName = SanitizeFileName(template.Name) + ".yaml";
                var filePath = Path.Combine(templatesPath, fileName);

                await WriteYamlFileAsync(filePath, yamlModel, cancellationToken);
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

        return count;
    }

    private async Task<int> ExportActionsAsync(string rootFolderPath, SyncExportResult result, CancellationToken cancellationToken)
    {
        var actionsPath = Path.Combine(rootFolderPath, ActionsFolderName);
        var actions = await _dbContext.Actions
            .Include(a => a.WindowsCommandTemplate)
            .Include(a => a.LinuxCommandTemplate)
            .ToListAsync(cancellationToken);

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
                Guid? windowsTemplatePublicId = null;
                Guid? linuxTemplatePublicId = null;

                if (action.WindowsCommandTemplate != null)
                {
                    windowsTemplatePublicId = action.WindowsCommandTemplate.PublicId;
                }

                if (action.LinuxCommandTemplate != null)
                {
                    linuxTemplatePublicId = action.LinuxCommandTemplate.PublicId;
                }

                var yamlModel = new ActionYamlModel
                {
                    Id = action.PublicId,
                    Title = action.Title,
                    Description = action.Description,
                    Category = action.Category,
                    Platform = action.Platform.ToString(),
                    Level = action.Level.ToString(),
                    Tags = DeserializeJson<List<string>>(action.TagsJson) ?? new(),
                    WindowsTemplateId = windowsTemplatePublicId,
                    LinuxTemplateId = linuxTemplatePublicId,
                    Examples = DeserializeJson<List<ExampleYaml>>(action.ExamplesJson) ?? new(),
                    WindowsExamples = DeserializeJson<List<ExampleYaml>>(action.WindowsExamplesJson) ?? new(),
                    LinuxExamples = DeserializeJson<List<ExampleYaml>>(action.LinuxExamplesJson) ?? new(),
                    Notes = action.Notes,
                    Links = DeserializeJson<List<LinkYaml>>(action.LinksJson) ?? new(),
                    IsUserCreated = action.IsUserCreated
                };

                var fileName = SanitizeFileName(action.Title) + ".yaml";
                var filePath = Path.Combine(categoryPath, fileName);

                await WriteYamlFileAsync(filePath, yamlModel, cancellationToken);
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

        return count;
    }

    private async Task<int> ExportBatchesAsync(string rootFolderPath, SyncExportResult result, CancellationToken cancellationToken)
    {
        var batchesPath = Path.Combine(rootFolderPath, BatchesFolderName);
        var batches = await _dbContext.CommandBatches.ToListAsync(cancellationToken);
        int count = 0;

        foreach (var batch in batches)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var commands = DeserializeJson<List<BatchCommandYaml>>(batch.CommandsJson) ?? new();

                var yamlModel = new BatchYamlModel
                {
                    Id = batch.PublicId,
                    Name = batch.Name,
                    Description = batch.Description,
                    ExecutionMode = batch.ExecutionMode.ToString(),
                    Tags = DeserializeJson<List<string>>(batch.TagsJson) ?? new(),
                    Commands = commands,
                    IsUserCreated = batch.IsUserCreated
                };

                var fileName = SanitizeFileName(batch.Name) + ".yaml";
                var filePath = Path.Combine(batchesPath, fileName);

                await WriteYamlFileAsync(filePath, yamlModel, cancellationToken);
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

        return count;
    }

    #endregion

    #region Import

    public async Task<SyncImportResult> ImportDataFromYamlAsync(string rootFolderPath, CancellationToken cancellationToken = default)
    {
        var result = new SyncImportResult { Success = true };
        IDbContextTransaction? transaction = null;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

            // Import in order: categories, templates, actions, batches
            // (respecting dependencies)

            var categoriesPath = Path.Combine(rootFolderPath, CategoriesFolderName);
            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(categoriesPath))
            {
                await ImportCategoriesAsync(categoriesPath, result, cancellationToken);
            }

            var templatesPath = Path.Combine(rootFolderPath, TemplatesFolderName);
            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(templatesPath))
            {
                await ImportTemplatesAsync(templatesPath, result, cancellationToken);
            }

            var actionsPath = Path.Combine(rootFolderPath, ActionsFolderName);
            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(actionsPath))
            {
                await ImportActionsAsync(actionsPath, result, cancellationToken);
            }

            var batchesPath = Path.Combine(rootFolderPath, BatchesFolderName);
            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(batchesPath))
            {
                await ImportBatchesAsync(batchesPath, result, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (transaction != null)
            {
                try
                {
                    await transaction.RollbackAsync();
                }
                catch
                {
                }
            }

            throw;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Errors.Add($"Import failed: {ex.Message}");

            if (transaction != null)
            {
                try
                {
                    await transaction.RollbackAsync();
                }
                catch (Exception rollbackEx)
                {
                    result.Errors.Add($"Rollback failed: {rollbackEx.Message}");
                }
            }
        }
        finally
        {
            if (transaction != null)
            {
                await transaction.DisposeAsync();
            }
        }

        return result;
    }

    private async Task ImportCategoriesAsync(string folderPath, SyncImportResult result, CancellationToken cancellationToken)
    {
        var files = Directory.GetFiles(folderPath, "*.yaml", SearchOption.TopDirectoryOnly);

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

                var yaml = await File.ReadAllTextAsync(filePath, cancellationToken);
                var model = _yamlDeserializer.Deserialize<CategoryYamlModel>(yaml);

                if (model == null || model.Id == Guid.Empty)
                {
                    result.Warnings.Add($"Invalid category file: {filePath}");
                    continue;
                }

                var existing = await _dbContext.CustomCategories
                    .FirstOrDefaultAsync(c => c.PublicId == model.Id, cancellationToken);

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
                    existing.ModifiedAt = DateTime.UtcNow;
                    result.CategoriesUpdated++;
                }
                else
                {
                    // Create new
                    var entity = new CustomCategoryEntity
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
                        CreatedAt = DateTime.UtcNow
                    };
                    _dbContext.CustomCategories.Add(entity);
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

    private async Task ImportTemplatesAsync(string folderPath, SyncImportResult result, CancellationToken cancellationToken)
    {
        var files = Directory.GetFiles(folderPath, "*.yaml", SearchOption.TopDirectoryOnly);

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

                var yaml = await File.ReadAllTextAsync(filePath, cancellationToken);
                var model = _yamlDeserializer.Deserialize<TemplateYamlModel>(yaml);

                if (model == null || model.Id == Guid.Empty)
                {
                    result.Warnings.Add($"Invalid template file: {filePath}");
                    continue;
                }

                if (!Enum.TryParse<Platform>(model.Platform, true, out var platform))
                {
                    platform = Platform.Windows;
                }

                var parametersJson = SerializeJson(model.Parameters ?? new List<TemplateParameterYaml>());

                var existing = await _dbContext.CommandTemplates
                    .FirstOrDefaultAsync(t => t.PublicId == model.Id, cancellationToken);

                if (existing != null)
                {
                    // Update existing
                    existing.Name = model.Name;
                    existing.Platform = platform;
                    existing.CommandPattern = model.CommandPattern;
                    existing.ParametersJson = parametersJson;
                    result.TemplatesUpdated++;
                }
                else
                {
                    // Create new
                    var entity = new CommandTemplateEntity
                    {
                        Id = Guid.NewGuid().ToString(),
                        PublicId = model.Id,
                        Name = model.Name,
                        Platform = platform,
                        CommandPattern = model.CommandPattern,
                        ParametersJson = parametersJson
                    };
                    _dbContext.CommandTemplates.Add(entity);
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

    private async Task ImportActionsAsync(string folderPath, SyncImportResult result, CancellationToken cancellationToken)
    {
        // Recursively find all YAML files (organized by category subfolders)
        var files = Directory.GetFiles(folderPath, "*.yaml", SearchOption.AllDirectories);

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

                var yaml = await File.ReadAllTextAsync(filePath, cancellationToken);
                var model = _yamlDeserializer.Deserialize<ActionYamlModel>(yaml);

                if (model == null || model.Id == Guid.Empty)
                {
                    result.Warnings.Add($"Invalid action file: {filePath}");
                    continue;
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
                string? windowsTemplateId = null;
                string? linuxTemplateId = null;

                if (model.WindowsTemplateId.HasValue)
                {
                    var template = await _dbContext.CommandTemplates
                        .FirstOrDefaultAsync(t => t.PublicId == model.WindowsTemplateId.Value, cancellationToken);
                    windowsTemplateId = template?.Id;
                }

                if (model.LinuxTemplateId.HasValue)
                {
                    var template = await _dbContext.CommandTemplates
                        .FirstOrDefaultAsync(t => t.PublicId == model.LinuxTemplateId.Value, cancellationToken);
                    linuxTemplateId = template?.Id;
                }

                var tagsJson = SerializeJson(model.Tags ?? new List<string>());
                var examplesJson = SerializeJson(model.Examples ?? new List<ExampleYaml>());
                var windowsExamplesJson = SerializeJson(model.WindowsExamples ?? new List<ExampleYaml>());
                var linuxExamplesJson = SerializeJson(model.LinuxExamples ?? new List<ExampleYaml>());
                var linksJson = SerializeJson(model.Links ?? new List<LinkYaml>());

                var existing = await _dbContext.Actions
                    .FirstOrDefaultAsync(a => a.PublicId == model.Id, cancellationToken);

                if (existing != null)
                {
                    // Update existing
                    existing.Title = model.Title;
                    existing.Description = model.Description;
                    existing.Category = model.Category;
                    existing.Platform = platform;
                    existing.Level = level;
                    existing.TagsJson = tagsJson;
                    existing.WindowsCommandTemplateId = windowsTemplateId;
                    existing.LinuxCommandTemplateId = linuxTemplateId;
                    existing.ExamplesJson = examplesJson;
                    existing.WindowsExamplesJson = windowsExamplesJson;
                    existing.LinuxExamplesJson = linuxExamplesJson;
                    existing.Notes = model.Notes;
                    existing.LinksJson = linksJson;
                    existing.IsUserCreated = model.IsUserCreated;
                    existing.UpdatedAt = DateTime.UtcNow;
                    result.ActionsUpdated++;
                }
                else
                {
                    // Create new
                    var entity = new ActionEntity
                    {
                        Id = Guid.NewGuid().ToString(),
                        PublicId = model.Id,
                        Title = model.Title,
                        Description = model.Description,
                        Category = model.Category,
                        Platform = platform,
                        Level = level,
                        TagsJson = tagsJson,
                        WindowsCommandTemplateId = windowsTemplateId,
                        LinuxCommandTemplateId = linuxTemplateId,
                        ExamplesJson = examplesJson,
                        WindowsExamplesJson = windowsExamplesJson,
                        LinuxExamplesJson = linuxExamplesJson,
                        Notes = model.Notes,
                        LinksJson = linksJson,
                        IsUserCreated = model.IsUserCreated,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    };
                    _dbContext.Actions.Add(entity);
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

    private async Task ImportBatchesAsync(string folderPath, SyncImportResult result, CancellationToken cancellationToken)
    {
        var files = Directory.GetFiles(folderPath, "*.yaml", SearchOption.TopDirectoryOnly);

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

                var yaml = await File.ReadAllTextAsync(filePath, cancellationToken);
                var model = _yamlDeserializer.Deserialize<BatchYamlModel>(yaml);

                if (model == null || model.Id == Guid.Empty)
                {
                    result.Warnings.Add($"Invalid batch file: {filePath}");
                    continue;
                }

                if (!Enum.TryParse<BatchExecutionMode>(model.ExecutionMode, true, out var executionMode))
                {
                    executionMode = BatchExecutionMode.StopOnError;
                }

                var commandsJson = SerializeJson(model.Commands ?? new List<BatchCommandYaml>());
                var tagsJson = SerializeJson(model.Tags ?? new List<string>());

                var existing = await _dbContext.CommandBatches
                    .FirstOrDefaultAsync(b => b.PublicId == model.Id, cancellationToken);

                if (existing != null)
                {
                    // Update existing
                    existing.Name = model.Name;
                    existing.Description = model.Description;
                    existing.ExecutionMode = executionMode;
                    existing.CommandsJson = commandsJson;
                    existing.TagsJson = tagsJson;
                    existing.IsUserCreated = model.IsUserCreated;
                    existing.UpdatedAt = DateTime.UtcNow;
                    result.BatchesUpdated++;
                }
                else
                {
                    // Create new
                    var entity = new CommandBatchEntity
                    {
                        Id = Guid.NewGuid().ToString(),
                        PublicId = model.Id,
                        Name = model.Name,
                        Description = model.Description,
                        ExecutionMode = executionMode,
                        CommandsJson = commandsJson,
                        TagsJson = tagsJson,
                        IsUserCreated = model.IsUserCreated,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    };
                    _dbContext.CommandBatches.Add(entity);
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

    public async Task<SyncValidationResult> ValidateFolderAsync(string rootFolderPath, CancellationToken cancellationToken = default)
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

            // Check categories
            var categoriesPath = Path.Combine(rootFolderPath, CategoriesFolderName);
            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(categoriesPath))
            {
                result.CategoryFilesFound = await ValidateYamlFilesAsync<CategoryYamlModel>(
                    categoriesPath,
                    result,
                    "category",
                    cancellationToken);
            }

            // Check templates
            var templatesPath = Path.Combine(rootFolderPath, TemplatesFolderName);
            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(templatesPath))
            {
                result.TemplateFilesFound = await ValidateYamlFilesAsync<TemplateYamlModel>(
                    templatesPath,
                    result,
                    "template",
                    cancellationToken);
            }

            // Check actions (recursive)
            var actionsPath = Path.Combine(rootFolderPath, ActionsFolderName);
            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(actionsPath))
            {
                result.ActionFilesFound = await ValidateYamlFilesAsync<ActionYamlModel>(
                    actionsPath,
                    result,
                    "action",
                    cancellationToken,
                    SearchOption.AllDirectories);
            }

            // Check batches
            var batchesPath = Path.Combine(rootFolderPath, BatchesFolderName);
            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(batchesPath))
            {
                result.BatchFilesFound = await ValidateYamlFilesAsync<BatchYamlModel>(
                    batchesPath,
                    result,
                    "batch",
                    cancellationToken);
            }

            if (result.TotalFilesFound == 0)
            {
                result.Warnings.Add("No YAML files found in the folder structure.");
            }
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

    private async Task<int> ValidateYamlFilesAsync<T>(
        string folderPath,
        SyncValidationResult result,
        string entityType,
        CancellationToken cancellationToken,
        SearchOption searchOption = SearchOption.TopDirectoryOnly)
    {
        var files = Directory.GetFiles(folderPath, "*.yaml", searchOption);
        int validCount = 0;

        foreach (var filePath in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (!ValidateFileSize(filePath))
                {
                    result.Warnings.Add($"File too large: {Path.GetFileName(filePath)}");
                    continue;
                }

                var yaml = await File.ReadAllTextAsync(filePath, cancellationToken);
                var model = _yamlDeserializer.Deserialize<T>(yaml);

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

    private static void EnsureDirectoryExists(string path)
    {
        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
        }
    }

    private async Task WriteYamlFileAsync<T>(string filePath, T model, CancellationToken cancellationToken)
    {
        var yaml = _yamlSerializer.Serialize(model);
        await File.WriteAllTextAsync(filePath, yaml, cancellationToken);
    }

    private static bool ValidateFileSize(string filePath)
    {
        var fileInfo = new FileInfo(filePath);
        return fileInfo.Length <= MaxFileSizeBytes;
    }

    private static string SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Guid.NewGuid().ToString();
        }

        // SECURITY: Protect against path traversal attacks
        // Remove any path separators and parent directory references
        var sanitized = name
            .Replace("..", "")
            .Replace("/", "_")
            .Replace("\\", "_");

        // Remove invalid characters
        var invalidChars = Path.GetInvalidFileNameChars();
        sanitized = new string(sanitized
            .Select(c => invalidChars.Contains(c) ? '_' : c)
            .ToArray());

        // Replace multiple underscores with single
        sanitized = Regex.Replace(sanitized, @"_+", "_");

        // Trim and limit length
        sanitized = sanitized.Trim('_').Trim();
        if (sanitized.Length > 100)
        {
            sanitized = sanitized.Substring(0, 100);
        }

        // SECURITY: Final check - ensure no path traversal possible
        if (sanitized.Contains("..") || Path.IsPathRooted(sanitized))
        {
            return Guid.NewGuid().ToString();
        }

        // If empty after sanitization, use GUID
        return string.IsNullOrWhiteSpace(sanitized) ? Guid.NewGuid().ToString() : sanitized;
    }

    private static T? DeserializeJson<T>(string json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptionsHelper.CaseInsensitive);
        }
        catch (JsonException)
        {
            // Invalid JSON format - return null to indicate parse failure
            return null;
        }
    }

    private static string SerializeJson<T>(T obj)
    {
        return JsonSerializer.Serialize(obj, JsonOptionsHelper.CamelCaseForImport);
    }

    #endregion

    #region YAML Models

    /// <summary>
    /// YAML model for Category export/import
    /// </summary>
    private sealed class CategoryYamlModel
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

    /// <summary>
    /// YAML model for Template export/import
    /// </summary>
    private sealed class TemplateYamlModel
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Platform { get; set; } = "Windows";
        public string CommandPattern { get; set; } = string.Empty;
        public List<TemplateParameterYaml>? Parameters { get; set; }
    }

    private sealed class TemplateParameterYaml
    {
        public string Name { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public string Type { get; set; } = "string";
        public string? DefaultValue { get; set; }
        public bool Required { get; set; }
        public string? Description { get; set; }
    }

    /// <summary>
    /// YAML model for Action export/import
    /// </summary>
    private sealed class ActionYamlModel
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
        public List<ExampleYaml>? Examples { get; set; }
        public List<ExampleYaml>? WindowsExamples { get; set; }
        public List<ExampleYaml>? LinuxExamples { get; set; }
        public string? Notes { get; set; }
        public List<LinkYaml>? Links { get; set; }
        public bool IsUserCreated { get; set; }
    }

    private sealed class ExampleYaml
    {
        public string Command { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string? Platform { get; set; }
    }

    private sealed class LinkYaml
    {
        public string Title { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
    }

    /// <summary>
    /// YAML model for Batch export/import
    /// </summary>
    private sealed class BatchYamlModel
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string ExecutionMode { get; set; } = "StopOnError";
        public List<string>? Tags { get; set; }
        public List<BatchCommandYaml>? Commands { get; set; }
        public bool IsUserCreated { get; set; }
    }

    private sealed class BatchCommandYaml
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
