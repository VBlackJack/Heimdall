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

using System.Windows;
using Heimdall.App.Theming;
using Heimdall.Core.Configuration;
using Heimdall.Core.Logging;
using ThemeForgeAccentTint = ThemeForge.Theme.AccentTint;
using ThemeForgeAccentTints = ThemeForge.Theme.AccentTints;
using ThemeForgeChangedEventArgs = ThemeForge.Theme.ThemeChangedEventArgs;
using ThemeForgeIThemeService = ThemeForge.Theme.IThemeService;
using ThemeForgeNames = ThemeForge.Theme.ThemeNames;
using ThemeForgeThemeService = ThemeForge.Theme.ThemeService;

namespace Heimdall.App.Services;

internal readonly record struct ThemeResolution(string ThemeId, bool ShouldPersist);

internal readonly record struct AccentTintResolution(string AccentTintId, bool ShouldPersist);

/// <summary>
/// Heimdall compatibility wrapper around the ThemeForge theme engine.
/// </summary>
public sealed class HeimdallThemeService
{
    private const string DefaultTheme = ThemeForgeNames.Drakul;
    private const string DefaultAccentTint = nameof(ThemeForgeAccentTint.Default);
    private const string BridgeDictionaryPath = "Themes/HeimdallThemeBridge.xaml";
    private const string BridgeDictionaryPackPath =
        "pack://application:,,,/Heimdall;component/Themes/HeimdallThemeBridge.xaml";

    private static readonly Dictionary<string, string> ThemeForgeIds =
        ThemeForgeNames.All.ToDictionary(
            themeName => themeName,
            themeName => themeName,
            StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, ThemeForgeAccentTint> AccentTintIds =
        ThemeForgeAccentTints.All.ToDictionary(
            accentTint => accentTint.ToString(),
            accentTint => accentTint,
            StringComparer.OrdinalIgnoreCase);

    private readonly IConfigManager _configManager;
    private readonly object _themeServiceLock = new();
    private ThemeForgeIThemeService? _themeService;
    private string _currentTheme = DefaultTheme;
    private string _currentAccentTint = DefaultAccentTint;

    public HeimdallThemeService(IConfigManager configManager)
    {
        _configManager = configManager;
    }

    /// <summary>Canonical ThemeForge id currently applied to the application.</summary>
    public string CurrentTheme
    {
        get
        {
            string current = _themeService?.CurrentTheme ?? string.Empty;
            return string.IsNullOrWhiteSpace(current) ? _currentTheme : current;
        }
    }

    /// <summary>
    /// Monotonically increasing ThemeForge revision counter after successful swaps.
    /// </summary>
    public int ThemeRevision => _themeService?.ThemeRevision ?? 0;

    /// <summary>
    /// Raised after ThemeForge applies a theme, translated to the legacy event shape.
    /// </summary>
    public event Action<string>? ThemeChanged;

    /// <summary>
    /// Enumerates the ThemeForge theme ids recognized by Heimdall.
    /// </summary>
    public static IReadOnlyCollection<string> AvailableThemes => ThemeForgeNames.All;

    /// <summary>
    /// Enumerates the ThemeForge accent tint ids recognized by Heimdall.
    /// </summary>
    public static IReadOnlyCollection<string> AvailableAccentTints { get; } =
        ThemeForgeAccentTints.All
            .Select(accentTint => accentTint.ToString())
            .ToArray();

    /// <summary>Canonical ThemeForge accent tint currently applied to the application.</summary>
    public string CurrentAccentTint => _themeService?.CurrentAccentTint.ToString() ?? _currentAccentTint;

    /// <summary>
    /// Resolves a persisted setting to a ThemeForge id and delegates the swap.
    /// </summary>
    public void ApplyTheme(string? themeName)
    {
        ThemeForgeIThemeService? themeService = GetThemeService();
        if (themeService is null)
        {
            return;
        }

        ThemeResolution resolution = ResolveThemeId(themeName);
        if (resolution.ShouldPersist)
        {
            PersistTheme(resolution.ThemeId);
        }

        themeService.ApplyTheme(resolution.ThemeId);

        Application? app = Application.Current;
        if (app is null)
        {
            return;
        }

        RefreshHeimdallBridge(app);

        foreach (Window window in app.Windows)
        {
            WindowThemeHelper.ApplyCurrentTheme(window);
        }
    }

    /// <summary>
    /// Resolves a persisted accent tint setting and delegates it to ThemeForge.
    /// </summary>
    public void ApplyAccentTint(string? accentName)
    {
        ThemeForgeIThemeService? themeService = GetThemeService();
        if (themeService is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(themeService.CurrentTheme))
        {
            FileLogger.Warn(
                "[HeimdallThemeService] Ignored accent tint before a theme was applied.");
            return;
        }

        AccentTintResolution resolution = ResolveAccentTint(accentName);
        if (resolution.ShouldPersist)
        {
            PersistAccentTint(resolution.AccentTintId);
        }

        ThemeForgeAccentTint accentTint = AccentTintIds[resolution.AccentTintId];
        themeService.ApplyAccentTint(accentTint);
        _currentAccentTint = resolution.AccentTintId;
    }

    internal static ThemeResolution ResolveThemeId(string? persisted)
    {
        if (string.IsNullOrWhiteSpace(persisted))
        {
            return new ThemeResolution(DefaultTheme, ShouldPersist: true);
        }

        string trimmed = persisted.Trim();
        if (!ThemeForgeIds.TryGetValue(trimmed, out string? canonical))
        {
            return new ThemeResolution(DefaultTheme, ShouldPersist: true);
        }

        bool shouldPersist = !string.Equals(persisted, canonical, StringComparison.Ordinal);
        return new ThemeResolution(canonical, shouldPersist);
    }

    internal static AccentTintResolution ResolveAccentTint(string? persisted)
    {
        if (string.IsNullOrWhiteSpace(persisted))
        {
            return new AccentTintResolution(DefaultAccentTint, ShouldPersist: true);
        }

        string trimmed = persisted.Trim();
        if (!AccentTintIds.TryGetValue(trimmed, out ThemeForgeAccentTint accentTint))
        {
            return new AccentTintResolution(DefaultAccentTint, ShouldPersist: true);
        }

        string canonical = accentTint.ToString();
        bool shouldPersist = !string.Equals(persisted, canonical, StringComparison.Ordinal);
        return new AccentTintResolution(canonical, shouldPersist);
    }

    private ThemeForgeIThemeService? GetThemeService()
    {
        if (_themeService is not null)
        {
            return _themeService;
        }

        Application? app = Application.Current;
        if (app is null)
        {
            return null;
        }

        lock (_themeServiceLock)
        {
            if (_themeService is not null)
            {
                return _themeService;
            }

            ThemeForgeIThemeService service = new ThemeForgeThemeService(app, ThemeForgeNames.All);
            service.ThemeChanged += OnThemeForgeThemeChanged;
            _themeService = service;
            return _themeService;
        }
    }

    internal static ResourceDictionary CreateBridgeDictionary()
    {
        return CreateBridgeDictionary(static source => new ResourceDictionary
        {
            Source = new Uri(source, UriKind.Absolute),
        });
    }

    internal static ResourceDictionary CreateBridgeDictionary(
        Func<string, ResourceDictionary> dictionaryFactory)
    {
        ArgumentNullException.ThrowIfNull(dictionaryFactory);
        return dictionaryFactory(BridgeDictionaryPackPath);
    }

    private static void RefreshHeimdallBridge(Application app)
    {
        IList<ResourceDictionary> merged = app.Resources.MergedDictionaries;

        for (int i = 0; i < merged.Count; i++)
        {
            Uri? source = merged[i].Source;
            if (source is null || !IsHeimdallBridgeSource(source))
            {
                continue;
            }

            ResourceDictionary replacement = CreateBridgeDictionary();
            merged[i] = replacement;
            return;
        }
    }

    private static bool IsHeimdallBridgeSource(Uri source)
    {
        string original = source.OriginalString.Replace('\\', '/');
        return original.EndsWith(BridgeDictionaryPath, StringComparison.OrdinalIgnoreCase);
    }

    private void PersistTheme(string themeId)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await _configManager.MergeSettingAsync(settings => settings.DefaultTheme = themeId);
                FileLogger.Info(
                    $"[HeimdallThemeService] Persisted ThemeForge theme '{themeId}'");
            }
            catch (Exception ex)
            {
                FileLogger.Warn(
                    $"[HeimdallThemeService] Failed to persist theme: {ex.Message}");
            }
        });
    }

    private void PersistAccentTint(string accentTintId)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await _configManager.MergeSettingAsync(settings => settings.AccentTint = accentTintId);
                FileLogger.Info(
                    $"[HeimdallThemeService] Persisted ThemeForge accent tint '{accentTintId}'");
            }
            catch (Exception ex)
            {
                FileLogger.Warn(
                    $"[HeimdallThemeService] Failed to persist accent tint: {ex.Message}");
            }
        });
    }

    private void OnThemeForgeThemeChanged(object? sender, ThemeForgeChangedEventArgs args)
    {
        _currentTheme = args.CurrentTheme;
        _currentAccentTint = _themeService?.CurrentAccentTint.ToString() ?? _currentAccentTint;
        ThemeChanged?.Invoke(args.CurrentTheme);
    }
}
