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

using System.Data;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Heimdall.App.Services;
using Heimdall.Core.Localization;
using Heimdall.Core.Logging;
using Heimdall.Core.Models;
using Heimdall.Core.Recipes;

namespace Heimdall.App.ViewModels.Tools;

/// <summary>What a recipe view needs to run its recipe; every part can be replaced by a fake.</summary>
/// <param name="Runner">Starts the program.</param>
/// <param name="Resolver">Compares the program with the one stored at import.</param>
/// <param name="WorkFolders">Gives each run its own empty folder.</param>
/// <param name="Availability">Says whether the tool may be used right now.</param>
/// <param name="AllowedFolders">Reads the folders the user allowed in Settings, each time a run starts.</param>
public sealed record RecipeToolServices(
    IRecipeRunner Runner,
    IRecipeProgramResolver Resolver,
    IRecipeWorkFolders WorkFolders,
    IToolAvailability Availability,
    Func<IReadOnlyList<string>> AllowedFolders);

/// <summary>
/// The recipe tool: input fields built from the recipe, the session target it reads, one run at a
/// time, and the result of the run in words, text or a grid. It never builds a command line: the
/// values go through <see cref="RecipeExpander"/> and the program through the resolver, and a run
/// that fails either check stops with a reason, before anything is started.
/// </summary>
public sealed partial class RecipeToolViewModel : ObservableObject, IDisposable
{
    private readonly Recipe _recipe;
    private readonly string _storedProgramPath;
    private readonly RecipeToolServices _services;
    private readonly RecipeSessionUse _sessionUse;
    private readonly string _toolId;
    private readonly List<RecipeFieldViewModel> _fields;
    private LocalizationManager? _localizer;
    private string? _sessionHost;
    private int? _sessionPort;
    private CancellationTokenSource? _runCancellation;
    private Task? _runnerTask;
    private string? _workFolder;
    private RecipeRunResult? _lastResult;
    private bool _disposed;

    [ObservableProperty]
    private string _title;

    [ObservableProperty]
    private string _description;

    [ObservableProperty]
    private string _provenance = string.Empty;

    [ObservableProperty]
    private string _sessionTargetText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    private bool _isRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private bool _isStatusFailure;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGeneralError))]
    private string _generalError = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTruncation))]
    private string _truncationText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowText))]
    private string _outputText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowGrid))]
    [NotifyPropertyChangedFor(nameof(ShowText))]
    [NotifyPropertyChangedFor(nameof(OutputView))]
    private DataTable? _outputTable;

    [ObservableProperty]
    private string _rowCountText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasErrorOutput))]
    private string _errorOutputText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOutput))]
    private string _copyText = string.Empty;

    /// <summary>Creates the view model of a recipe.</summary>
    /// <param name="recipe">A recipe that passed the validator.</param>
    /// <param name="storedProgramPath">The path the program resolved to when the recipe was imported.</param>
    /// <param name="services">What running a recipe needs.</param>
    public RecipeToolViewModel(Recipe recipe, string storedProgramPath, RecipeToolServices services)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        ArgumentNullException.ThrowIfNull(storedProgramPath);
        ArgumentNullException.ThrowIfNull(services);

        _recipe = recipe;
        _storedProgramPath = storedProgramPath;
        _services = services;
        _sessionUse = RecipeSessionUse.Of(recipe);
        _toolId = ToolDescriptor.RecipeIdPrefix + recipe.Id;
        _title = recipe.Name;
        _description = recipe.Description;
        _fields = [.. recipe.Inputs.Select(input => new RecipeFieldViewModel(input, input.Label))];
    }

    /// <summary>The input fields, in the order of the recipe.</summary>
    public IReadOnlyList<RecipeFieldViewModel> Fields => _fields;

    /// <summary>True when the recipe reads the host or the port of a session.</summary>
    public bool UsesSession => _sessionUse.UsesSession;

    /// <summary>True when no run is under way.</summary>
    public bool IsIdle => !IsRunning;

    /// <summary>True when the result has a line saying how the run ended.</summary>
    public bool HasStatus => StatusText.Length > 0;

    /// <summary>True when a message explains why the run did not start.</summary>
    public bool HasGeneralError => GeneralError.Length > 0;

    /// <summary>True when the output was cut.</summary>
    public bool HasTruncation => TruncationText.Length > 0;

    /// <summary>True when the output is shown as a grid.</summary>
    public bool ShowGrid => OutputTable is not null;

    /// <summary>True when the output is shown as text.</summary>
    public bool ShowText => OutputTable is null && OutputText.Length > 0;

    /// <summary>The rows of the grid, or null when the output is text.</summary>
    public DataView? OutputView => OutputTable?.DefaultView;

    /// <summary>True when standard error is shown on its own.</summary>
    public bool HasErrorOutput => ErrorOutputText.Length > 0;

    /// <summary>True when there is output to copy.</summary>
    public bool HasOutput => CopyText.Length > 0;

    /// <summary>The program the recipe runs, as resolved when it was imported.</summary>
    public string StoredProgramPath => _storedProgramPath;

    /// <summary>
    /// Takes the session target from the context the tool was opened with, the way a network tool
    /// does, and the localizer; texts are written in the language of the user from here on.
    /// </summary>
    /// <param name="context">The context the tool was opened with, or null.</param>
    /// <param name="localizer">The localizer of the application, or null in a test without one.</param>
    public void Initialize(ToolContext? context, LocalizationManager? localizer)
    {
        if (!ReferenceEquals(_localizer, localizer))
        {
            if (_localizer is not null)
            {
                _localizer.LocaleChanged -= OnLocaleChanged;
            }

            _localizer = localizer;
            if (_localizer is not null)
            {
                _localizer.LocaleChanged += OnLocaleChanged;
            }
        }

        string? host = context?.TargetHost?.Trim();
        _sessionHost = string.IsNullOrEmpty(host) ? null : host;
        _sessionPort = context?.TargetPort is > 0 ? context.TargetPort : null;

        RefreshTexts();
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        if (_disposed || IsRunning)
        {
            return;
        }

        IsRunning = true;
        SetFieldsLocked(true);
        ClearResult();

        string? folder = null;
        try
        {
            if (!CheckAvailable() || !CheckSession() || ExpandValues() is not { } expansion || !CheckProgram())
            {
                return;
            }

            folder = CreateWorkFolder();
            if (folder is null)
            {
                return;
            }

            _workFolder = folder;
            _runCancellation = new CancellationTokenSource();
            Task<RecipeRunResult> runnerTask = _services.Runner.RunAsync(
                _recipe,
                _storedProgramPath,
                expansion.Arguments,
                folder,
                _runCancellation.Token);
            _runnerTask = runnerTask;

            RecipeRunResult result = await runnerTask.ConfigureAwait(true);
            if (!_disposed)
            {
                Present(result);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The runner reports every expected failure as data; this is a defect, and only its type is logged.
            FileLogger.Warn($"[Recipes] A run of '{_recipe.Id}' failed unexpectedly ({ex.GetType().Name})");
            if (!_disposed)
            {
                GeneralError = T("RecipeRunUnexpected");
            }
        }
        finally
        {
            ReleaseRun(folder);
            if (!_disposed)
            {
                SetFieldsLocked(false);
                IsRunning = false;
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop() => CancelRun();

    private bool CanRun() => !_disposed && !IsRunning;

    private bool CanStop() => !_disposed && IsRunning;

    /// <summary>
    /// Cancels a run under way and waits, within a bound, for the program to be killed, then removes
    /// the working folder. What the closing of the tab calls.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_localizer is not null)
        {
            _localizer.LocaleChanged -= OnLocaleChanged;
        }

        CancelRun();
        try
        {
            // The runner never needs this thread to finish, so waiting here cannot deadlock.
            _runnerTask?.Wait(TimeSpan.FromMilliseconds(RecipeLimits.KillWaitMilliseconds + RecipeLimits.DrainGraceMilliseconds));
        }
        catch (AggregateException)
        {
            // A failed run has nothing left to wait for.
        }

        if (_workFolder is { } folder)
        {
            _services.WorkFolders.Delete(folder);
            _workFolder = null;
        }

        GC.SuppressFinalize(this);
    }

    private void CancelRun()
    {
        try
        {
            _runCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The run already ended and released its token.
        }
    }

    private void ReleaseRun(string? folder)
    {
        if (folder is not null)
        {
            _services.WorkFolders.Delete(folder);
        }

        _workFolder = null;
        _runnerTask = null;
        _runCancellation?.Dispose();
        _runCancellation = null;
    }

    private bool CheckAvailable()
    {
        ToolAvailabilityState state = _services.Availability.GetState(_toolId);
        if (state == ToolAvailabilityState.Unknown)
        {
            // Turning the pack off takes the recipe tools out of the registry, so the id is unknown
            // for that reason too: the pack, not the recipe, is what to name when it is the cause.
            if (_services.Availability.IsPackForbiddenByPolicy(ToolPack.Recipes))
            {
                state = ToolAvailabilityState.ForbiddenByPolicy;
            }
            else if (!_services.Availability.IsPackEnabled(ToolPack.Recipes))
            {
                state = ToolAvailabilityState.DisabledByUser;
            }
        }

        if (state == ToolAvailabilityState.Available)
        {
            return true;
        }

        GeneralError = ToolUnavailableException.IsRefusal(state)
            ? T(ToolUnavailableException.MessageKeyFor(state), _recipe.Name)
            : T("RecipeRunToolRemoved");
        return false;
    }

    private bool CheckSession()
    {
        bool missing = (_sessionUse.UsesHost && _sessionHost is null) || (_sessionUse.UsesPort && _sessionPort is null);
        if (missing)
        {
            GeneralError = T("RecipeRunNoSession");
        }

        return !missing;
    }

    private RecipeExpansion? ExpandValues()
    {
        Dictionary<string, string?> values = new(StringComparer.Ordinal);
        foreach (RecipeFieldViewModel field in _fields)
        {
            field.Error = string.Empty;
            values[field.Name] = field.Value;
        }

        RecipeExpansion expansion = RecipeExpander.Expand(
            _recipe,
            values,
            _sessionUse.UsesHost ? _sessionHost : null,
            _sessionUse.UsesPort ? _sessionPort?.ToString(CultureInfo.InvariantCulture) : null);
        if (expansion.Succeeded)
        {
            return expansion;
        }

        ShowRefusals(expansion.Refusals);
        return null;
    }

    private void ShowRefusals(IReadOnlyList<RecipeRefusal> refusals)
    {
        foreach (RecipeRefusal refusal in refusals)
        {
            string words = RecipeRunText.Describe(Localizer, refusal.Reason);
            RecipeFieldViewModel? field = _fields.FirstOrDefault(
                candidate => string.Equals(candidate.Name, refusal.Subject, StringComparison.Ordinal));

            if (field is not null)
            {
                if (!field.HasError)
                {
                    field.Error = words;
                }
            }
            else if (!HasGeneralError)
            {
                bool session = refusal.Subject is RecipeFormat.HostPlaceholder or RecipeFormat.PortPlaceholder;
                GeneralError = T(session ? "RecipeRunSessionValueRefused" : "RecipeRunValuesRefused", words);
            }
        }
    }

    private bool CheckProgram()
    {
        RecipeProgramComparison comparison = _services.Resolver.CompareWithStored(
            _recipe.Program,
            _services.AllowedFolders(),
            _storedProgramPath);
        if (comparison == RecipeProgramComparison.Same)
        {
            return true;
        }

        GeneralError = RecipeRunText.Describe(Localizer, comparison, _storedProgramPath);
        return false;
    }

    private string? CreateWorkFolder()
    {
        try
        {
            return _services.WorkFolders.Create();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            FileLogger.Warn($"[Recipes] The working folder of '{_recipe.Id}' could not be created ({ex.GetType().Name})");
            GeneralError = T("RecipeRunWorkFolderFailed");
            return null;
        }
    }

    private void ClearResult()
    {
        _lastResult = null;
        GeneralError = string.Empty;
        StatusText = string.Empty;
        IsStatusFailure = false;
        TruncationText = string.Empty;
        OutputText = string.Empty;
        OutputTable = null;
        RowCountText = string.Empty;
        ErrorOutputText = string.Empty;
        CopyText = string.Empty;
        foreach (RecipeFieldViewModel field in _fields)
        {
            field.Error = string.Empty;
        }
    }

    private void Present(RecipeRunResult result)
    {
        _lastResult = result;
        FileLogger.Info($"[Recipes] Run of '{_recipe.Id}' ended: {result.Status}");

        StatusText = RecipeRunText.Describe(Localizer, result, _recipe.TimeoutSeconds);
        IsStatusFailure = !result.IsSuccess;
        TruncationText = result.Truncated ? T("RecipeOutputTruncated") : string.Empty;
        ErrorOutputText = result.ErrorOutput.TrimEnd();

        TabularOutput? tabular = _recipe.Output switch
        {
            RecipeOutputFormat.Csv => TabularOutput.FromCsv(result.Output),
            RecipeOutputFormat.Json => TabularOutput.FromJson(result.Output),
            _ => null,
        };

        if (tabular?.Table is { } table)
        {
            OutputTable = table;
            OutputText = string.Empty;
            RowCountText = Localizer.FormatCount(table.Rows.Count, "RecipeOutputRowsOne", "RecipeOutputRows", table.Rows.Count);
            CopyText = TabularOutput.ToTabSeparated(table);
            return;
        }

        OutputTable = null;
        OutputText = (tabular?.Text ?? result.Output).TrimEnd();
        RowCountText = string.Empty;
        CopyText = OutputText;
    }

    private void SetFieldsLocked(bool locked)
    {
        foreach (RecipeFieldViewModel field in _fields)
        {
            field.IsLocked = locked;
        }
    }

    private void OnLocaleChanged(string locale) => RefreshTexts();

    private void RefreshTexts()
    {
        LocalizationManager localizer = Localizer;
        RecipeTexts texts = RecipeToolText.Resolve(_recipe, localizer.CurrentLocale);

        Title = texts.Name;
        Description = texts.Description;
        Provenance = RecipeToolText.Provenance(_recipe.Author, localizer.GetString);
        foreach (RecipeFieldViewModel field in _fields)
        {
            field.Label = texts.FieldLabels.TryGetValue(field.Name, out string? label) ? label : field.Input.Label;
        }

        SessionTargetText = DescribeSessionTarget();
        if (_lastResult is { } result)
        {
            StatusText = RecipeRunText.Describe(localizer, result, _recipe.TimeoutSeconds);
        }
    }

    private string DescribeSessionTarget()
    {
        if (!_sessionUse.UsesSession)
        {
            return string.Empty;
        }

        bool hostMissing = _sessionUse.UsesHost && _sessionHost is null;
        bool portMissing = _sessionUse.UsesPort && _sessionPort is null;
        if (hostMissing || portMissing)
        {
            return T("RecipeSessionTargetNone");
        }

        string? port = _sessionPort?.ToString(CultureInfo.InvariantCulture);
        if (_sessionUse is { UsesHost: true, UsesPort: true })
        {
            return T("RecipeSessionTargetHostPort", _sessionHost!, port!);
        }

        return _sessionUse.UsesHost
            ? T("RecipeSessionTargetHost", _sessionHost!)
            : T("RecipeSessionTargetPort", port!);
    }

    // The tests of a view model run without an application localizer: an empty one answers every key with itself.
    private LocalizationManager Localizer => _localizer ??= new LocalizationManager();

    private string T(string key, params object[] arguments)
        => arguments.Length == 0 ? Localizer[key] : Localizer.Format(key, arguments);
}
