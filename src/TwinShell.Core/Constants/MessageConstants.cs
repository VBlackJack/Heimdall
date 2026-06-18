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

namespace TwinShell.Core.Constants;

/// <summary>
/// Message keys for localization and error messages.
/// These keys should be used with ILocalizationService to get localized messages.
/// </summary>
public static class MessageKeys
{
    // Common messages
    public const string Ready = "Common.Ready";
    public const string Loading = "Common.Loading";
    public const string Error = "Common.Error";
    public const string Success = "Common.Success";
    public const string Warning = "Common.Warning";
    public const string Confirmation = "Common.Confirmation";
    public const string CommonError = Error; // Alias for consistency
    public const string CommonErrorProcessing = "Common.Error.Processing";

    // Validation messages
    public const string ParameterRequired = "Validation.ParameterRequired";
    public const string ParameterMustBeInteger = "Validation.ParameterMustBeInteger";
    public const string ParameterMustBeBoolean = "Validation.ParameterMustBeBoolean";
    public const string ValidationErrors = "Validation.Errors";
    public const string NoCommandTemplate = "Validation.NoCommandTemplate";
    public const string NoValidCommand = "Validation.NoValidCommand";

    // Additional validation messages (aliases for consistency)
    public const string ValidationParameterRequired = ParameterRequired;
    public const string ValidationParameterMustBeInteger = ParameterMustBeInteger;
    public const string ValidationParameterMustBeBoolean = ParameterMustBeBoolean;
    public const string ValidationNoCommandTemplate = NoCommandTemplate;
    public const string ValidationNoValidCommand = NoValidCommand;

    // New validation messages
    public const string ValidationParameterMaxLength = "Validation.ParameterMaxLength";
    public const string ValidationParameterDangerousCharacters = "Validation.ParameterDangerousCharacters";
    public const string ValidationParameterInvalidHostname = "Validation.ParameterInvalidHostname";
    public const string ValidationParameterInvalidIPAddress = "Validation.ParameterInvalidIPAddress";
    public const string ValidationParameterInvalidPath = "Validation.ParameterInvalidPath";

    // Execution messages
    public const string ExecutionError = "Execution.Error";
    public const string ExecutionSuccess = "Execution.Success";
    public const string ExecutionCancelled = "Execution.Cancelled";
    public const string ExecutionTimeout = "Execution.Timeout";

    // Favorites messages
    public const string FavoriteAdded = "Favorites.Added";
    public const string FavoriteRemoved = "Favorites.Removed";
    public const string FavoritesLimitReached = "Favorites.LimitReached";
    public const string FavoritesLimitReachedMessage = "Favorites.LimitReachedMessage";
    public const string FavoritesToggleFailed = "Favorites.ToggleFailed";
    public const string FavoritesUpdateError = "Favorites.UpdateError";

    // Configuration messages
    public const string ConfigExportSuccess = "Config.ExportSuccess";
    public const string ConfigExportError = "Config.ExportError";
    public const string ConfigImportSuccess = "Config.ImportSuccess";
    public const string ConfigImportError = "Config.ImportError";
    public const string ConfigValidationError = "Config.ValidationError";
    public const string ConfigImportConfirmation = "Config.ImportConfirmation";
    public const string ConfigImportConfirmationMessage = "Config.ImportConfirmationMessage";

    // Additional configuration message aliases for consistency
    public const string ConfigExportSuccessMessage = ConfigExportSuccess;
    public const string ConfigExportErrorMessage = ConfigExportError;
    public const string ConfigImportSuccessMessage = ConfigImportSuccess;
    public const string ConfigImportErrorMessage = ConfigImportError;
    public const string ConfigValidationErrorMessage = ConfigValidationError;
    public const string ConfigImportErrorGeneric = "Config.ImportError.Generic";
    public const string ConfigExportErrorGeneric = "Config.ExportError.Generic";

    // Help messages
    public const string HelpTitle = "Help.Title";
    public const string HelpContent = "Help.Content";
    public const string HelpKeyboardShortcutsTitle = "Help.KeyboardShortcuts.Title";
    public const string HelpKeyboardShortcutsContent = "Help.KeyboardShortcuts.Content";

    // Status messages
    public const string ActionsLoaded = "Status.ActionsLoaded";
    public const string Refreshing = "Status.Refreshing";
    public const string StatusActionsLoaded = ActionsLoaded; // Alias for consistency
    public const string StatusRefreshing = Refreshing; // Alias for consistency

    // Action Editor messages
    public const string ActionEditorNewParameter = "ActionEditor.NewParameter";
    public const string ActionEditorTitleRequired = "ActionEditor.Validation.TitleRequired";
    public const string ActionEditorTitleMaxLength = "ActionEditor.Validation.TitleMaxLength";
    public const string ActionEditorCategoryRequired = "ActionEditor.Validation.CategoryRequired";
    public const string ActionEditorCategoryMaxLength = "ActionEditor.Validation.CategoryMaxLength";
    public const string ActionEditorDescriptionMaxLength = "ActionEditor.Validation.DescriptionMaxLength";
    public const string ActionEditorNotesMaxLength = "ActionEditor.Validation.NotesMaxLength";
    public const string ActionEditorWindowsCommandRequired = "ActionEditor.Validation.WindowsCommandRequired";
    public const string ActionEditorWindowsCommandNameRequired = "ActionEditor.Validation.WindowsCommandNameRequired";
    public const string ActionEditorLinuxCommandRequired = "ActionEditor.Validation.LinuxCommandRequired";
    public const string ActionEditorLinuxCommandNameRequired = "ActionEditor.Validation.LinuxCommandNameRequired";
    public const string ActionEditorWindowsParameterNameRequired = "ActionEditor.Validation.WindowsParameterNameRequired";
    public const string ActionEditorWindowsParameterNameUnique = "ActionEditor.Validation.WindowsParameterNameUnique";
    public const string ActionEditorLinuxParameterNameRequired = "ActionEditor.Validation.LinuxParameterNameRequired";
    public const string ActionEditorLinuxParameterNameUnique = "ActionEditor.Validation.LinuxParameterNameUnique";
    public const string ActionEditorSaveError = "ActionEditor.SaveError";
    public const string ActionEditorExampleDescription = "ActionEditor.ExampleDescription";

    // Settings messages
    public const string SettingsSavedSuccess = "Settings.SavedSuccess";
    public const string SettingsSaveFailed = "Settings.SaveFailed";
    public const string SettingsResetConfirmation = "Settings.ResetConfirmation";
    public const string SettingsResetSuccess = "Settings.ResetSuccess";
    public const string SettingsResetTitle = "Settings.ResetTitle";

    // Execution messages (extended)
    public const string ExecutionReady = "Execution.Ready";
    public const string ExecutionExecuting = "Execution.Executing";
    public const string ExecutionCancellingMessage = "Execution.CancellingMessage";
    public const string ExecutionNoCommand = "Execution.NoCommand";
    public const string ExecutionNoCommandTitle = "Execution.NoCommandTitle";
    public const string ExecutionDangerousWarning = "Execution.DangerousWarning";
    public const string ExecutionDangerousTitle = "Execution.DangerousTitle";
    public const string ExecutionCancelledByUser = "Execution.CancelledByUser";
    public const string ExecutionSuccessStatus = "Execution.SuccessStatus";
    public const string ExecutionFailedStatus = "Execution.FailedStatus";
    public const string ExecutionTimedOutMessage = "Execution.TimedOutMessage";
    public const string ExecutionErrorOccurred = "Execution.ErrorOccurred";

    // Category management messages
    public const string CategoryNameRequired = "Category.NameRequired";
    public const string CategoryAlreadyExists = "Category.AlreadyExists";
    public const string CategoryReadyToUse = "Category.ReadyToUse";
    public const string CategoryRegisteredTitle = "Category.RegisteredTitle";
    public const string CategoryNameEmpty = "Category.NameEmpty";
    public const string CategoryRenamedSuccess = "Category.RenamedSuccess";
    public const string CategoryRenameFailed = "Category.RenameFailed";
    public const string CategoryDeleteConfirmation = "Category.DeleteConfirmation";
    public const string CategoryDeleteConfirmTitle = "Category.DeleteConfirmTitle";
    public const string CategoryDeletedSuccess = "Category.DeletedSuccess";
    public const string CategoryProcessingError = "Category.ProcessingError";
    public const string CategorySaveError = "Category.SaveError";
    public const string CategoryDeleteError = "Category.DeleteError";

    // Git Sync messages
    public const string GitSyncNotConfigured = "GitSync.NotConfigured";
    public const string GitSyncCheckingRepository = "GitSync.CheckingRepository";
    public const string GitSyncRepositoryInitialized = "GitSync.RepositoryInitialized";
    public const string GitSyncInvalidDirectory = "GitSync.InvalidDirectory";
    public const string GitSyncCloning = "GitSync.Cloning";
    public const string GitSyncCloneSuccess = "GitSync.CloneSuccess";
    public const string GitSyncCloneFailed = "GitSync.CloneFailed";
    public const string GitSyncInitFailed = "GitSync.InitFailed";
    public const string GitSyncFetching = "GitSync.Fetching";
    public const string GitSyncMerging = "GitSync.Merging";
    public const string GitSyncImporting = "GitSync.Importing";
    public const string GitSyncImportComplete = "GitSync.ImportComplete";
    public const string GitSyncImportFailed = "GitSync.ImportFailed";
    public const string GitSyncPullFailed = "GitSync.PullFailed";
    public const string GitSyncSyncFailed = "GitSync.SyncFailed";
    public const string GitSyncExporting = "GitSync.Exporting";
    public const string GitSyncExportFailed = "GitSync.ExportFailed";
    public const string GitSyncStaging = "GitSync.Staging";
    public const string GitSyncCommitting = "GitSync.Committing";
    public const string GitSyncNoChanges = "GitSync.NoChanges";
    public const string GitSyncPushing = "GitSync.Pushing";
    public const string GitSyncPushSuccess = "GitSync.PushSuccess";
    public const string GitSyncPushRejected = "GitSync.PushRejected";
    public const string GitSyncPushFailed = "GitSync.PushFailed";
    public const string GitSyncTestingConnection = "GitSync.TestingConnection";
    public const string GitSyncConnectionSuccess = "GitSync.ConnectionSuccess";
    public const string GitSyncConnectionFailed = "GitSync.ConnectionFailed";
    public const string GitSyncRepositoryNotInitialized = "GitSync.RepositoryNotInitialized";
    public const string GitSyncRetrying = "GitSync.Retrying";
    public const string GitSyncConflictDetected = "GitSync.ConflictDetected";
    public const string GitSyncFailedToClone = "GitSync.FailedToClone";
    public const string GitSyncFailedToInitialize = "GitSync.FailedToInitialize";
    public const string GitSyncPullSucceededImportFailed = "GitSync.PullSucceededImportFailed";
    public const string GitSyncFailedToPull = "GitSync.FailedToPull";
    public const string GitSyncFailedToSync = "GitSync.FailedToSync";
    public const string GitSyncInvalidRepositoryPath = "GitSync.InvalidRepositoryPath";
    public const string GitSyncInvalidRepositoryPathDetails = "GitSync.InvalidRepositoryPathDetails";
    public const string GitSyncFailedToConnect = "GitSync.FailedToConnect";
    public const string GitSyncConnectionTestFailed = "GitSync.ConnectionTestFailed";
    public const string GitSyncExportSucceededPushFailed = "GitSync.ExportSucceededPushFailed";
    public const string GitSyncFailedToExportAndPush = "GitSync.FailedToExportAndPush";

    // Clipboard messages
    public const string ClipboardCommandCopied = "Clipboard.CommandCopied";

    // History messages
    public const string HistoryClearAllConfirmation = "History.ClearAllConfirmation";
    public const string HistoryClearAllTitle = "History.ClearAllTitle";
    public const string HistoryClearedSuccess = "History.ClearedSuccess";
    public const string HistoryClearError = "History.ClearError";
    public const string HistoryDeletedSuccess = "History.DeletedSuccess";
    public const string HistoryDeleteError = "History.DeleteError";

    // Accessibility messages
    public const string AccessibilityReducedMotionEnabled = "Accessibility.ReducedMotion.Enabled";
    public const string AccessibilityReducedMotionDisabled = "Accessibility.ReducedMotion.Disabled";

    // Sync messages (for JsonSyncService and YamlSyncService)
    public const string SyncExportFailed = "Sync.ExportFailed";
    public const string SyncImportFailed = "Sync.ImportFailed";
    public const string SyncRollbackFailed = "Sync.RollbackFailed";
    public const string SyncChangesRolledBack = "Sync.ChangesRolledBack";
    public const string SyncInvalidCategoryFile = "Sync.InvalidCategoryFile";
    public const string SyncInvalidTemplateFile = "Sync.InvalidTemplateFile";
    public const string SyncInvalidActionFile = "Sync.InvalidActionFile";
    public const string SyncInvalidBatchFile = "Sync.InvalidBatchFile";
    public const string SyncInvalidFile = "Sync.InvalidFile";
    public const string SyncValidationFailed = "Sync.ValidationFailed";
    public const string SyncFileTooLarge = "Sync.FileTooLarge";
    public const string SyncFileImportFailed = "Sync.FileImportFailed";
    public const string SyncMergeConflict = "Sync.MergeConflict";
}
