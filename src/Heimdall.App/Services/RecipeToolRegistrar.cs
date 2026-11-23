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

using Heimdall.App.ViewModels.Tools;
using Heimdall.App.Views.Tools;
using Heimdall.Core.Logging;
using Heimdall.Core.Models;
using Heimdall.Core.Recipes;

namespace Heimdall.App.Services;

/// <summary>Builds the view of an imported recipe; the one place a recipe view is put together.</summary>
public sealed class RecipeToolViewFactory
{
    private readonly RecipeToolServices _services;

    /// <summary>Creates a factory whose views run recipes with the given services.</summary>
    /// <param name="services">What running a recipe needs.</param>
    public RecipeToolViewFactory(RecipeToolServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
    }

    /// <summary>Creates the view of a recipe; must be called on the UI thread, like every tool view.</summary>
    /// <param name="loaded">The recipe and the program path stored at its import.</param>
    public IToolView Create(LoadedRecipe loaded)
    {
        ArgumentNullException.ThrowIfNull(loaded);
        return new RecipeToolView(new RecipeToolViewModel(loaded.Recipe, loaded.Entry.ResolvedProgramPath, loaded.Entry.Sha256, _services));
    }
}

/// <summary>
/// Keeps the recipe tools of the registry in step with the Recipes pack: they are loaded and
/// registered at start when the pack is on and not forbidden, removed when it is turned off or
/// forbidden, and loaded and registered again when it is turned on while the application runs.
/// </summary>
/// <remarks>
/// The availability service raises its event on a pool thread; the registry is safe to call from
/// any thread and raises its own event for the window to marshal. Nothing here touches the UI: a
/// recipe view is only built, on the UI thread, when a recipe tool is opened.
/// </remarks>
public sealed class RecipeToolRegistrar : IDisposable
{
    private readonly ToolRegistry _registry;
    private readonly IToolAvailability _availability;
    private readonly IRecipeStore _store;
    private readonly Func<LoadedRecipe, IToolView> _createView;
    private readonly IRecipeWorkFolders? _workFolders;
    private readonly object _gate = new();
    private bool _started;
    private bool _listed;
    private bool _disposed;

    /// <summary>Creates the registrar.</summary>
    /// <param name="registry">The tool registry whose recipe overlay is kept up to date.</param>
    /// <param name="availability">Says whether the Recipes pack is on, and announces changes.</param>
    /// <param name="store">Reads the imported recipes.</param>
    /// <param name="createView">Builds the view of a recipe when its tool is opened.</param>
    /// <param name="workFolders">Cleans the run folders a crash left behind at start; optional.</param>
    public RecipeToolRegistrar(
        ToolRegistry registry,
        IToolAvailability availability,
        IRecipeStore store,
        Func<LoadedRecipe, IToolView> createView,
        IRecipeWorkFolders? workFolders = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _availability = availability ?? throw new ArgumentNullException(nameof(availability));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _createView = createView ?? throw new ArgumentNullException(nameof(createView));
        _workFolders = workFolders;
    }

    /// <summary>True while recipe tools are registered by this registrar.</summary>
    public bool IsListed
    {
        get
        {
            lock (_gate)
            {
                return _listed;
            }
        }
    }

    /// <summary>
    /// Cleans stale run folders, registers the recipe tools when the pack is on, and starts
    /// following the pack. Calling it again does nothing: there is one subscription.
    /// </summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_started || _disposed)
            {
                return;
            }

            _started = true;
            _availability.Changed += OnAvailabilityChanged;
        }

        _workFolders?.CleanStale();
        Synchronize(force: true);
    }

    /// <summary>Reads the store again and registers what it holds, when the pack is on; for after an import or a removal.</summary>
    public void Reload() => Synchronize(force: true);

    /// <summary>Stops following the pack. The recipe tools stay as they are.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_started)
            {
                _availability.Changed -= OnAvailabilityChanged;
            }
        }
    }

    private void OnAvailabilityChanged() => Synchronize(force: false);

    private void Synchronize(bool force)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                // False for a pack the user turned off and for one the organisation forbids.
                if (!_availability.IsPackEnabled(ToolPack.Recipes))
                {
                    if (_listed)
                    {
                        _registry.RegisterRecipeTools([], _ => throw new InvalidOperationException("No recipe tool is registered."));
                        _listed = false;
                        FileLogger.Info("[Recipes] The Recipes pack is off: its tools were removed");
                    }

                    return;
                }

                if (_listed && !force)
                {
                    return;
                }

                RecipeLoadResult result = _store.LoadAll();
                FileLogger.Info($"[Recipes] Loaded {result.Recipes.Count} recipe(s), skipped {result.Issues.Count}");
                foreach (RecipeLoadIssue issue in result.Issues)
                {
                    FileLogger.Warn($"[Recipes] Recipe skipped: {issue.Code} ({issue.FileName})");
                }

                Dictionary<string, LoadedRecipe> byId = new(StringComparer.Ordinal);
                foreach (LoadedRecipe loaded in result.Recipes)
                {
                    byId[loaded.Recipe.Id] = loaded;
                }

                _registry.RegisterRecipeTools(
                    [.. result.Recipes.Select(loaded => loaded.Recipe)],
                    recipe => _createView(byId[recipe.Id]));
                _listed = true;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A failure to read the store must not take the application, or the settings change, down.
                FileLogger.Error("[Recipes] The recipe tools could not be registered", ex);
            }
        }
    }
}
