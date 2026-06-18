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
using System.Text.RegularExpressions;
using TwinShell.Core.Interfaces;

namespace TwinShell.Infrastructure.Services;

/// <summary>
/// Shared filesystem workflow for TwinShell sync services.
/// </summary>
public abstract class SyncServiceBase : ISyncService
{
    protected const string ActionsFolderName = "actions";
    protected const string BatchesFolderName = "batches";
    protected const string TemplatesFolderName = "templates";
    protected const string CategoriesFolderName = "categories";

    private const long MaxFileSizeBytes = 100 * 1024; // 100 KB

    public abstract Task<SyncExportResult> ExportDataToYamlAsync(
        string rootFolderPath,
        CancellationToken cancellationToken = default);

    public abstract Task<SyncImportResult> ImportDataFromYamlAsync(
        string rootFolderPath,
        CancellationToken cancellationToken = default);

    public abstract Task<SyncValidationResult> ValidateFolderAsync(
        string rootFolderPath,
        CancellationToken cancellationToken = default);

    protected async Task RunImportPipelineAsync(
        string rootFolderPath,
        SyncImportResult result,
        CancellationToken cancellationToken)
    {
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
    }

    protected async Task RunValidationPipelineAsync(
        string rootFolderPath,
        SyncValidationResult result,
        string emptyFolderWarning,
        CancellationToken cancellationToken)
    {
        var categoriesPath = Path.Combine(rootFolderPath, CategoriesFolderName);
        cancellationToken.ThrowIfCancellationRequested();
        if (Directory.Exists(categoriesPath))
        {
            result.CategoryFilesFound = await ValidateCategoriesAsync(categoriesPath, result, cancellationToken);
        }

        var templatesPath = Path.Combine(rootFolderPath, TemplatesFolderName);
        cancellationToken.ThrowIfCancellationRequested();
        if (Directory.Exists(templatesPath))
        {
            result.TemplateFilesFound = await ValidateTemplatesAsync(templatesPath, result, cancellationToken);
        }

        var actionsPath = Path.Combine(rootFolderPath, ActionsFolderName);
        cancellationToken.ThrowIfCancellationRequested();
        if (Directory.Exists(actionsPath))
        {
            result.ActionFilesFound = await ValidateActionsAsync(actionsPath, result, cancellationToken);
        }

        var batchesPath = Path.Combine(rootFolderPath, BatchesFolderName);
        cancellationToken.ThrowIfCancellationRequested();
        if (Directory.Exists(batchesPath))
        {
            result.BatchFilesFound = await ValidateBatchesAsync(batchesPath, result, cancellationToken);
        }

        if (result.TotalFilesFound == 0)
        {
            result.Warnings.Add(emptyFolderWarning);
        }
    }

    protected abstract Task ImportCategoriesAsync(
        string folderPath,
        SyncImportResult result,
        CancellationToken cancellationToken);

    protected abstract Task ImportTemplatesAsync(
        string folderPath,
        SyncImportResult result,
        CancellationToken cancellationToken);

    protected abstract Task ImportActionsAsync(
        string folderPath,
        SyncImportResult result,
        CancellationToken cancellationToken);

    protected abstract Task ImportBatchesAsync(
        string folderPath,
        SyncImportResult result,
        CancellationToken cancellationToken);

    protected abstract Task<int> ValidateCategoriesAsync(
        string folderPath,
        SyncValidationResult result,
        CancellationToken cancellationToken);

    protected abstract Task<int> ValidateTemplatesAsync(
        string folderPath,
        SyncValidationResult result,
        CancellationToken cancellationToken);

    protected abstract Task<int> ValidateActionsAsync(
        string folderPath,
        SyncValidationResult result,
        CancellationToken cancellationToken);

    protected abstract Task<int> ValidateBatchesAsync(
        string folderPath,
        SyncValidationResult result,
        CancellationToken cancellationToken);

    protected static void EnsureDirectoryExists(string path)
    {
        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
        }
    }

    protected static bool ValidateFileSize(string filePath)
    {
        var fileInfo = new FileInfo(filePath);
        return fileInfo.Length <= MaxFileSizeBytes;
    }

    protected static string SanitizeFileNameCore(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        string sanitized = name
            .Replace("..", "")
            .Replace("/", "_")
            .Replace("\\", "_");

        char[] invalidChars = Path.GetInvalidFileNameChars();
        sanitized = new string(sanitized
            .Select(c => invalidChars.Contains(c) ? '_' : c)
            .ToArray());

        sanitized = Regex.Replace(sanitized, @"_+", "_");

        sanitized = sanitized.Trim('_').Trim();
        if (sanitized.Length > 100)
        {
            sanitized = sanitized.Substring(0, 100);
        }

        if (sanitized.Contains("..") || Path.IsPathRooted(sanitized))
        {
            return string.Empty;
        }

        return sanitized;
    }
}
