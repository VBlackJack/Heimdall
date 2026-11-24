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

using System.IO;
using System.Text;
using Heimdall.App.Services;
using Heimdall.App.Services.Import;
using Heimdall.App.Services.PostConnect;
using Heimdall.App.Services.SessionSnapshot;
using Heimdall.App.ViewModels;
using Heimdall.App.ViewModels.CommandPalette;
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.App.ViewModels.Settings;
using Heimdall.Core.Configuration;
using Heimdall.Core.Import;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;
using Heimdall.Core.Recipes;
using Heimdall.Core.Security;
using Heimdall.Core.Ssh;

namespace Heimdall.App.Tests;

/// <summary>A resolver whose answer a test sets; it records the folders it was asked with.</summary>
internal sealed class FakeRecipeProgramLocator : IRecipeProgramLocator
{
    public const string DefaultPath = @"C:\Windows\System32\ping.exe";

    public RecipeProgramVerdict Answer { get; set; } = new(RecipeProgramRefusal.None, DefaultPath, string.Empty);

    public List<IReadOnlyList<string>> FolderCalls { get; } = [];

    public RecipeProgramVerdict Resolve(string? program, IReadOnlyList<string> allowedFolders)
    {
        FolderCalls.Add([.. allowedFolders]);
        return Answer;
    }

    public void ResolveTo(string path) => Answer = new RecipeProgramVerdict(RecipeProgramRefusal.None, path, string.Empty);

    public void Refuse(RecipeProgramRefusal refusal, string text = "tool.exe") => Answer = new RecipeProgramVerdict(refusal, null, text);
}

/// <summary>Pickers that answer from a script and record how often they were asked.</summary>
internal sealed class FakeRecipeFileDialogs : IRecipeFileDialogs
{
    public string? ImportPath { get; set; }

    public string? ExportPath { get; set; }

    public string? FolderPath { get; set; }

    public int ImportAsked { get; private set; }

    public int ExportAsked { get; private set; }

    public string? SuggestedExportName { get; private set; }

    public string? PickRecipeToImport()
    {
        ImportAsked++;
        return ImportPath;
    }

    public string? PickRecipeExportTarget(string suggestedFileName)
    {
        ExportAsked++;
        SuggestedExportName = suggestedFileName;
        return ExportPath;
    }

    public string? PickProgramFolder() => FolderPath;
}

/// <summary>A dialog service that records what the recipe screens show and answers confirmations from a script.</summary>
internal sealed class RecipeRecordingDialogs : IDialogService
{
    public bool ConfirmResult { get; set; }

    public List<ConfirmCall> Confirms { get; } = [];

    public List<(string Title, string Message)> Errors { get; } = [];

    public List<(string Title, string Message)> Infos { get; } = [];

    public Func<ConfirmCall, Task>? OnConfirm { get; set; }

    public sealed record ConfirmCall(string Title, string Message, string Severity, string? ConfirmLabel, string? DeclineLabel, bool Large = false);

    public Task<bool> ShowLargeConfirmAsync(string title, string message, string severity, string confirmLabel, string declineLabel)
        => Record(new ConfirmCall(title, message, severity, confirmLabel, declineLabel, Large: true));

    public Task<bool> ShowConfirmAsync(string title, string message, string severity = "info")
        => Record(new ConfirmCall(title, message, severity, null, null));

    public Task<bool> ShowConfirmAsync(string title, string message, string severity, string confirmLabel, string declineLabel)
        => Record(new ConfirmCall(title, message, severity, confirmLabel, declineLabel));

    private async Task<bool> Record(ConfirmCall call)
    {
        Confirms.Add(call);
        if (OnConfirm is not null)
        {
            await OnConfirm(call);
        }

        return ConfirmResult;
    }

    public void ShowError(string title, string message) => Errors.Add((title, message));

    public void ShowInfo(string title, string message) => Infos.Add((title, message));

    public void ShowWarning(string title, string message)
    {
    }

    public Task<bool?> ShowSaveDiscardCancelAsync(string title, string message) => Task.FromResult<bool?>(false);

    public Task<string?> ShowInputAsync(string title, string prompt, string? defaultValue = null) => Task.FromResult<string?>(null);

    public Task<string?> ShowPasswordInputAsync(string title, string prompt, CancellationToken cancellationToken = default)
        => Task.FromResult<string?>(null);

    public Task<ServerDialogResult?> ShowServerDialogAsync(ServerDialogViewModel? editVm = null) => Task.FromResult<ServerDialogResult?>(null);

    public Task<GatewayDialogResult?> ShowGatewayDialogAsync(GatewayDialogViewModel? editVm = null) => Task.FromResult<GatewayDialogResult?>(null);

    public Task<ScheduledTaskDialogResult?> ShowScheduledTaskDialogAsync(ScheduledTaskDialogViewModel? editVm = null)
        => Task.FromResult<ScheduledTaskDialogResult?>(null);

    public Task ShowPinDialogAsync(PinDialogViewModel viewModel) => Task.CompletedTask;

    public Task<PinSetupResult?> ShowPinSetupDialogAsync(PinSetupDialogViewModel viewModel) => Task.FromResult<PinSetupResult?>(null);

    public Task<SnapshotRestoreDialogResult?> ShowSnapshotRestoreDialogAsync(SnapshotRestoreDialogViewModel viewModel)
        => Task.FromResult<SnapshotRestoreDialogResult?>(null);

    public Task<RdpImportSelection?> ShowRdpImportDialogAsync(RdpImportDialogViewModel viewModel) => Task.FromResult<RdpImportSelection?>(null);

    public Task<ImportOutcome?> ShowImportOpenSshConfigAsync(OpenSshParseResult parseResult) => Task.FromResult<ImportOutcome?>(null);

    public Task<ImportOutcome?> ShowImportPuttySessionsAsync(PuttySessionParseResult parseResult) => Task.FromResult<ImportOutcome?>(null);

    public Task<KnownHostsImportOutcome?> ShowImportKnownHostsAsync(KnownHostsImportPreview preview)
        => Task.FromResult<KnownHostsImportOutcome?>(null);

    public Task ShowTrustedHostKeyDetailsAsync(TrustedHostKeyDetailsDialogViewModel viewModel) => Task.CompletedTask;

    public Task<ImportKnownHostsConflictResolution?> ShowImportKnownHostsConflictAsync(ImportKnownHostsConflictDialogViewModel viewModel)
        => Task.FromResult<ImportKnownHostsConflictResolution?>(null);

    public Task<CommandLibraryPickerResult?> ShowCommandLibraryPickerAsync(
        CommandLibraryPickerDialogViewModel viewModel,
        AutoPrefillContext? prefillContext = null,
        string? existingActionId = null,
        IReadOnlyDictionary<string, string>? existingValues = null)
        => Task.FromResult<CommandLibraryPickerResult?>(null);

    public Task<int?> ShowBulkEditPortAsync(int count, int? initialPort, CancellationToken cancellationToken) => Task.FromResult(initialPort);

    public Task<string?> ShowBulkEditUsernameAsync(int count, string? initialUsername, CancellationToken cancellationToken)
        => Task.FromResult(initialUsername);

    public Task<string?> ShowBulkEditPasswordAsync(int count, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
}

/// <summary>Recipe files for the import tests.</summary>
internal static class RecipeImportFiles
{
    /// <summary>The text of a recipe file; tokens are written as given (JSON strings are quoted here).</summary>
    public static string Json(
        string id = "acme.ping",
        string version = "1.0.0",
        string program = "ping.exe",
        string name = "Ping",
        string author = "Acme",
        string description = "Ping the selected host.",
        string argumentsJson = "[\"-n\", \"{Host}\"]",
        string inputsJson = "[]",
        string? labelsJson = null,
        string? extraProperties = null)
        => $$"""
            {
              "schemaVersion": 1,
              "id": "{{id}}",
              "version": "{{version}}",
              "author": "{{author}}",
              "name": "{{name}}",
              "description": "{{description}}",
              "program": "{{program}}",
              "arguments": {{argumentsJson}},
              "inputs": {{inputsJson}}{{OptionalProperty("labels", labelsJson)}}{{(extraProperties is null ? string.Empty : "," + extraProperties)}}
            }
            """;

    private static string OptionalProperty(string name, string? json)
        => json is null ? string.Empty : $",\"{name}\": {json}";

    public static byte[] Bytes(
        string id = "acme.ping",
        string version = "1.0.0",
        string program = "ping.exe",
        string name = "Ping",
        string author = "Acme",
        string description = "Ping the selected host.",
        string argumentsJson = "[\"-n\", \"{Host}\"]",
        string inputsJson = "[]",
        string? labelsJson = null,
        string? extraProperties = null)
        => Encoding.UTF8.GetBytes(Json(id, version, program, name, author, description, argumentsJson, inputsJson, labelsJson, extraProperties));

    /// <summary>Writes a recipe file in <paramref name="folder"/> and returns its path.</summary>
    public static string Write(string folder, byte[] bytes, string fileName = "recipe.heimdall-recipe.json")
    {
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, fileName);
        File.WriteAllBytes(path, bytes);
        return path;
    }
}

/// <summary>
/// The pieces of the recipe import over a real store in a temporary folder, a scripted program
/// resolver, scripted pickers and a recording dialog service, with the shipped English locale.
/// </summary>
internal sealed class RecipeImportHarness : IDisposable
{
    private RecipeImportHarness(string root, LocalizationManager localizer)
    {
        Root = root;
        Localizer = localizer;
        Store = new RecipeStore(Path.Combine(root, "data"));
        Config = new ConfigManager(AppContext.BaseDirectory, Path.Combine(root, "config"));
        Import = new RecipeImportService(Store, Locator, Availability);
        ViewModel = new RecipeSettingsViewModel(Store, Import, Config, Dialogs, localizer, Files, () => ReloadCount++);
    }

    public string Root { get; }

    public string Inbox => Path.Combine(Root, "inbox");

    public LocalizationManager Localizer { get; }

    public RecipeStore Store { get; }

    public ConfigManager Config { get; }

    public FakeRecipeProgramLocator Locator { get; } = new();

    public FakeToolAvailability Availability { get; } = new();

    public FakeRecipeFileDialogs Files { get; } = new();

    public RecipeRecordingDialogs Dialogs { get; } = new();

    public RecipeImportService Import { get; }

    public RecipeSettingsViewModel ViewModel { get; }

    public int ReloadCount { get; private set; }

    public static async Task<RecipeImportHarness> CreateAsync(string locale = "en")
    {
        string root = Path.Combine(Path.GetTempPath(), "heimdall-recipe-import-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        LocalizationManager localizer = new();
        await localizer.LoadAsync(Path.Combine(RecipeViewModelHarness.RepositoryRoot(), "locales"), locale);
        RecipeImportHarness harness = new(root, localizer);
        await harness.Config.InitializeAsync();
        return harness;
    }

    /// <summary>Writes a recipe file the picker will return, and returns its path.</summary>
    public string Offer(byte[] bytes, string fileName = "recipe.heimdall-recipe.json")
    {
        string path = RecipeImportFiles.Write(Inbox, bytes, fileName);
        Files.ImportPath = path;
        return path;
    }

    public void Dispose()
    {
        ViewModel.Dispose();
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup of a temp directory.
        }
    }
}
