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
using Heimdall.App.Services;
using Heimdall.App.ViewModels.Tools;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;
using Heimdall.Core.Recipes;

namespace Heimdall.App.Tests;

/// <summary>A runner that starts nothing: it answers from a script and records what it was asked.</summary>
internal sealed class FakeRecipeRunner : IRecipeRunner
{
    public List<RunCall> Calls { get; } = [];

    /// <summary>What a run answers; the default is a success with no output.</summary>
    public Func<RunCall, Task<RecipeRunResult>> Script { get; set; } =
        _ => Task.FromResult(Result(RecipeRunStatus.Succeeded, exitCode: 0));

    public Task<RecipeRunResult> RunAsync(
        Recipe recipe,
        string programPath,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        RunCall call = new(
            recipe,
            programPath,
            [.. arguments],
            workingDirectory,
            cancellationToken,
            Directory.Exists(workingDirectory) && !Directory.EnumerateFileSystemEntries(workingDirectory).Any());
        Calls.Add(call);
        return Script(call);
    }

    public static RecipeRunResult Result(
        RecipeRunStatus status,
        int? exitCode = null,
        string output = "",
        string errorOutput = "",
        bool truncated = false,
        RecipeRunRefusal refusal = RecipeRunRefusal.None)
        => new(status, exitCode, output, errorOutput, truncated, null, refusal);

    public sealed record RunCall(
        Recipe Recipe,
        string ProgramPath,
        IReadOnlyList<string> Arguments,
        string WorkingDirectory,
        CancellationToken Token,
        bool FolderWasEmptyAtStart);
}

/// <summary>A resolver whose answer a test sets; it records what it was asked.</summary>
internal sealed class FakeRecipeProgramResolver : IRecipeProgramResolver
{
    public RecipeProgramComparison Answer { get; set; } = RecipeProgramComparison.Same;

    public List<(string? Program, IReadOnlyList<string> Folders, string Stored)> Calls { get; } = [];

    public RecipeProgramComparison CompareWithStored(string? program, IReadOnlyList<string> allowedFolders, string storedPath)
    {
        Calls.Add((program, [.. allowedFolders], storedPath));
        return Answer;
    }
}

/// <summary>An availability service whose answer a test sets, and whose event a test raises.</summary>
internal sealed class FakeToolAvailability : IToolAvailability
{
    public ToolAvailabilityState State { get; set; } = ToolAvailabilityState.Available;

    /// <summary>Whether the pack is on; it is off while the tool is unregistered, which is when the state is unknown.</summary>
    public bool PackEnabled { get; set; } = true;

    public bool PackForbidden { get; set; }

    public event Action? Changed;

    public ToolAvailabilityState GetState(string toolId) => State;

    public bool IsPackEnabled(ToolPack pack) => PackEnabled && !PackForbidden;

    public bool IsPackForbiddenByPolicy(ToolPack pack) => PackForbidden;

    public void RaiseChanged() => Changed?.Invoke();
}

/// <summary>A recipe store held in memory.</summary>
internal sealed class FakeRecipeStore : IRecipeStore
{
    public List<LoadedRecipe> Recipes { get; } = [];

    public int LoadCount { get; private set; }

    public RecipeLoadResult LoadAll()
    {
        LoadCount++;
        return new RecipeLoadResult([.. Recipes], []);
    }

    public IReadOnlyList<RecipeStoreEntry> List() => [.. Recipes.Select(item => item.Entry)];

    public RecipeStoreResult Add(byte[] bytes, string resolvedProgramPath) => throw new NotSupportedException();

    public RecipeStoreResult Replace(byte[] bytes, string resolvedProgramPath) => throw new NotSupportedException();

    public RecipeStoreResult Remove(string id) => throw new NotSupportedException();

    public byte[]? ReadOriginalBytes(string id) => null;
}

/// <summary>Builds recipes for the tests of the recipe view and its surfaces.</summary>
internal static class RecipeBuilders
{
    public const string StoredPath = @"C:\Windows\System32\ping.exe";

    public static RecipeInput Input(
        string name,
        RecipeInputType type,
        string? label = null,
        bool required = false,
        string? defaultValue = null,
        IReadOnlyList<string>? choices = null,
        IReadOnlyList<string>? whenTrue = null)
        => new(name, type, label ?? "Label " + name, required, defaultValue, null, null, choices, whenTrue);

    public static Recipe Recipe(
        string id = "acme.probe",
        string name = "Probe",
        string author = "Acme",
        IEnumerable<RecipeInput>? inputs = null,
        IEnumerable<RecipeArgument>? arguments = null,
        RecipeOutputFormat output = RecipeOutputFormat.Text,
        RecipeStderrMode stderr = RecipeStderrMode.Separate,
        int timeoutSeconds = 30,
        IReadOnlyDictionary<string, RecipeLabels>? labels = null)
        => new(
            SchemaVersion: 1,
            Id: id,
            Version: "1.0.0",
            Author: author,
            Name: name,
            Description: "Probes the thing.",
            Labels: labels ?? new Dictionary<string, RecipeLabels>(),
            Program: "ping.exe",
            Arguments: [.. arguments ?? [RecipeArgument.Bare("-n")]],
            Inputs: [.. inputs ?? []],
            Output: output,
            OutputEncoding: RecipeOutputEncoding.Utf8,
            Stderr: stderr,
            SuccessExitCodes: [0],
            TimeoutSeconds: timeoutSeconds,
            Icon: null);

    public static LoadedRecipe Loaded(Recipe recipe, string storedPath = StoredPath)
        => new(recipe, new RecipeStoreEntry(recipe.Id, RecipeStore.FileNameFor(recipe.Id), new string('a', 64), storedPath, DateTimeOffset.UnixEpoch));
}

/// <summary>A view model over fakes, a real run-folder service in a temporary folder, and the shipped English locale.</summary>
internal sealed class RecipeViewModelHarness : IDisposable
{
    private readonly string _root;

    private RecipeViewModelHarness(string root, LocalizationManager localizer)
    {
        _root = root;
        Localizer = localizer;
        WorkFolders = new RecipeWorkFolders(root);
        Services = new RecipeToolServices(Runner, Resolver, WorkFolders, Availability, () => AllowedFolders);
    }

    public FakeRecipeRunner Runner { get; } = new();

    public FakeRecipeProgramResolver Resolver { get; } = new();

    public FakeToolAvailability Availability { get; } = new();

    public RecipeWorkFolders WorkFolders { get; }

    public RecipeToolServices Services { get; }

    public LocalizationManager Localizer { get; }

    public List<string> AllowedFolders { get; } = [];

    /// <summary>The folder under which each run gets its own folder.</summary>
    public string RunsRoot => Path.Combine(_root, "recipe-runs");

    public static async Task<RecipeViewModelHarness> CreateAsync(string locale = "en")
    {
        string root = Path.Combine(Path.GetTempPath(), "heimdall-recipe-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        LocalizationManager localizer = new();
        await localizer.LoadAsync(Path.Combine(RepositoryRoot(), "locales"), locale);
        return new RecipeViewModelHarness(root, localizer);
    }

    public RecipeToolViewModel Create(Recipe recipe, ToolContext? context = null)
    {
        RecipeToolViewModel viewModel = new(recipe, RecipeBuilders.StoredPath, Services);
        viewModel.Initialize(context, Localizer);
        return viewModel;
    }

    /// <summary>The run folders that exist now.</summary>
    public string[] RunFolders()
        => Directory.Exists(RunsRoot) ? Directory.GetDirectories(RunsRoot) : [];

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A temporary folder that stays is not a test failure.
        }
    }

    public static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Heimdall.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException($"No Heimdall.slnx above {AppContext.BaseDirectory}");
    }
}
