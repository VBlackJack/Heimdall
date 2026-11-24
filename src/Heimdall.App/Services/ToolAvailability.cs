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

using System.Collections.Frozen;
using Heimdall.Core.Configuration;
using Heimdall.Core.Logging;
using Heimdall.Core.Models;
using Heimdall.Core.Security;

namespace Heimdall.App.Services;

/// <summary>
/// Decides whether a tool may be used from the registry's pack data, the user's
/// <see cref="AppSettings.DisabledToolPacks"/> and the organisation's <see cref="ToolPackPolicy"/>.
/// </summary>
/// <remarks>
/// The policy beats the user's setting. <see cref="ToolPack.Core"/> and pack names this build does
/// not know are ignored in a hand-edited settings file. The effective set is computed once and
/// again on each settings change, never per call, because reading the current settings clones them.
/// The Recipes pack fails closed: it counts as off until <see cref="AppSettings.RecipesPackMigrated"/>
/// says the one-time step ran or the user chose, so a step that failed or was skipped (a settings
/// file of a newer schema is never written) cannot leave it on for the session.
/// </remarks>
public sealed class ToolAvailability : IToolAvailability
{
    private readonly ToolRegistry _registry;
    private readonly ToolPackPolicy _policy;
    private readonly Func<AppSettings?> _currentSettings;
    private readonly object _gate = new();
    private FrozenSet<ToolPack>? _enabled;

    /// <summary>Builds the service on the application's configuration manager.</summary>
    public ToolAvailability(ToolRegistry registry, ToolPackPolicy policy, ConfigManager configManager)
        : this(
            registry,
            policy,
            () => configManager.CurrentSettings,
            handler => configManager.SettingsChanged += handler)
    {
    }

    /// <param name="registry">The full catalogue, which carries each tool's pack.</param>
    /// <param name="policy">The organisation veto.</param>
    /// <param name="currentSettings">Reads the settings snapshot the application currently holds, or null before the first load.</param>
    /// <param name="subscribeToSettingsChanged">Registers the handler the settings source calls with each new snapshot.</param>
    internal ToolAvailability(
        ToolRegistry registry,
        ToolPackPolicy policy,
        Func<AppSettings?> currentSettings,
        Action<Action<AppSettings>> subscribeToSettingsChanged)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(currentSettings);
        ArgumentNullException.ThrowIfNull(subscribeToSettingsChanged);

        _registry = registry;
        _policy = policy;
        _currentSettings = currentSettings;

        // Settings already loaded: take the baseline now, so the first change is compared with what
        // was in force before it. Not yet loaded: nothing is disabled until they are.
        if (currentSettings() is { } loaded)
        {
            _enabled = ComputeEnabled(loaded);
        }

        subscribeToSettingsChanged(OnSettingsChanged);
    }

    /// <inheritdoc />
    public event Action? Changed;

    /// <inheritdoc />
    public ToolAvailabilityState GetState(string toolId)
    {
        ToolDescriptor? descriptor = _registry.GetById(toolId);
        if (descriptor is null)
        {
            return ToolAvailabilityState.Unknown;
        }

        if (descriptor.Pack is not { } pack || pack == ToolPack.Core)
        {
            return ToolAvailabilityState.Available;
        }

        if (_policy.IsForbidden(pack))
        {
            return ToolAvailabilityState.ForbiddenByPolicy;
        }

        return EnabledPacks().Contains(pack)
            ? ToolAvailabilityState.Available
            : ToolAvailabilityState.DisabledByUser;
    }

    /// <inheritdoc />
    public bool IsPackEnabled(ToolPack pack)
        => pack == ToolPack.Core || EnabledPacks().Contains(pack);

    /// <inheritdoc />
    public bool IsPackForbiddenByPolicy(ToolPack pack) => _policy.IsForbidden(pack);

    /// <summary>
    /// Turns the settings into the set of packs they leave enabled, policy applied. Case is
    /// ignored, and names that are not an optional pack of this build (including numbers, which
    /// <see cref="Enum.TryParse{TEnum}(string, bool, out TEnum)"/> would accept) are dropped. The
    /// Recipes pack is left out while the settings do not carry its marker, and before they exist.
    /// </summary>
    internal FrozenSet<ToolPack> ComputeEnabled(AppSettings? settings)
    {
        HashSet<ToolPack> disabled = [];
        if (settings?.DisabledToolPacks is { } disabledNames)
        {
            foreach (string? name in disabledNames)
            {
                if (TryParseOptionalPack(name, out ToolPack pack))
                {
                    disabled.Add(pack);
                }
            }
        }

        if (settings is not { RecipesPackMigrated: true })
        {
            disabled.Add(ToolPack.Recipes);
        }

        return Enum.GetValues<ToolPack>()
            .Where(pack => pack != ToolPack.Core && !disabled.Contains(pack) && !_policy.IsForbidden(pack))
            .ToFrozenSet();
    }

    /// <summary>
    /// Reads a settings entry as an optional pack: padding and case are ignored, and Core, numbers and
    /// unknown names are not packs. The Settings panel reads the list through this same rule.
    /// </summary>
    internal static bool TryParseOptionalPack(string? name, out ToolPack pack)
    {
        string? trimmed = name?.Trim();
        foreach (ToolPack candidate in Enum.GetValues<ToolPack>())
        {
            if (candidate != ToolPack.Core
                && string.Equals(candidate.ToString(), trimmed, StringComparison.OrdinalIgnoreCase))
            {
                pack = candidate;
                return true;
            }
        }

        pack = default;
        return false;
    }

    private FrozenSet<ToolPack> EnabledPacks()
    {
        lock (_gate)
        {
            if (_enabled is { } cached)
            {
                return cached;
            }

            // Not cached before the first load: the answer is "nothing disabled" and must be
            // asked for again once the settings exist.
            AppSettings? settings = _currentSettings();
            FrozenSet<ToolPack> computed = ComputeEnabled(settings);
            if (settings is not null)
            {
                _enabled = computed;
            }

            return computed;
        }
    }

    private void OnSettingsChanged(AppSettings settings)
    {
        // The event can arrive after a newer write was already applied, because the settings source
        // raises it once its own lock is released: the current snapshot, not the argument, is the
        // truth. The argument stands in only while no snapshot exists yet.
        FrozenSet<ToolPack> previous;
        FrozenSet<ToolPack> next;
        lock (_gate)
        {
            AppSettings current = _currentSettings() ?? settings;
            next = ComputeEnabled(current);
            previous = _enabled ?? ComputeEnabled(null);
            _enabled = next;
        }

        if (previous.SetEquals(next))
        {
            return;
        }

        Delegate[]? subscribers = Changed?.GetInvocationList();
        if (subscribers is null)
        {
            return;
        }

        foreach (Delegate subscriber in subscribers)
        {
            try
            {
                ((Action)subscriber)();
            }
            catch (Exception ex)
            {
                FileLogger.Error("Tool availability subscriber failed", ex);
            }
        }
    }
}
