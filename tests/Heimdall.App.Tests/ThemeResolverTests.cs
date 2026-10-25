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

using System.Collections;
using System.IO;
using System.Resources;
using System.Windows;
using Heimdall.App.Services;
using ThemeForge.Theme;

namespace Heimdall.App.Tests;

public sealed class ThemeResolverTests
{
    [Fact]
    public void CreateBridgeDictionary_UsesAbsoluteApplicationPackPath()
    {
        const string ExpectedSource =
            "pack://application:,,,/Heimdall;component/Themes/HeimdallThemeBridge.xaml";
        ResourceDictionary expected = new();
        string? observedSource = null;

        ResourceDictionary actual = HeimdallThemeService.CreateBridgeDictionary(source =>
        {
            observedSource = source;
            return expected;
        });

        Assert.Same(expected, actual);
        Assert.Equal(ExpectedSource, observedSource);
    }

    [Fact]
    public void ThemeBridge_IsPresentInCompiledApplicationResources()
    {
        using Stream? resourceStream = typeof(HeimdallThemeService).Assembly
            .GetManifestResourceStream("Heimdall.g.resources");
        Assert.NotNull(resourceStream);

        using ResourceReader reader = new(resourceStream);
        IDictionaryEnumerator resources = reader.GetEnumerator();
        bool bridgeFound = false;
        while (resources.MoveNext())
        {
            if (string.Equals(
                    resources.Key as string,
                    "themes/heimdallthemebridge.baml",
                    StringComparison.OrdinalIgnoreCase))
            {
                bridgeFound = true;
                break;
            }
        }

        Assert.True(bridgeFound, "The compiled theme bridge BAML resource was not found.");
    }

    public static IEnumerable<object[]> ThemeForgeIds()
    {
        return ThemeNames.All.Select(themeName => new object[] { themeName });
    }

    [Theory]
    [MemberData(nameof(ThemeForgeIds))]
    public void ResolveThemeId_WithCanonicalThemeForgeId_ReturnsSameId(string themeName)
    {
        ThemeResolution result = HeimdallThemeService.ResolveThemeId(themeName);

        Assert.Equal(themeName, result.ThemeId);
        Assert.False(result.ShouldPersist);
    }

    [Theory]
    [InlineData("drakul", ThemeNames.Drakul)]
    [InlineData("PARCHMENT", ThemeNames.Parchment)]
    [InlineData("wHiTbY", ThemeNames.Whitby)]
    [InlineData(" Drakul ", ThemeNames.Drakul)]
    public void ResolveThemeId_WithNonCanonicalThemeForgeId_ReturnsCanonicalIdAndPersists(
        string persisted,
        string expectedTheme)
    {
        ThemeResolution result = HeimdallThemeService.ResolveThemeId(persisted);

        Assert.Equal(expectedTheme, result.ThemeId);
        Assert.True(result.ShouldPersist);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Dark")]
    [InlineData("Light")]
    [InlineData("DraculaPro")]
    [InlineData("Blade")]
    [InlineData("Buffy")]
    [InlineData("NotATheme")]
    public void ResolveThemeId_WithInvalidOrLegacyName_DefaultsToMagellanAndPersists(
        string? persisted)
    {
        ThemeResolution result = HeimdallThemeService.ResolveThemeId(persisted);

        Assert.Equal(ThemeNames.Magellan, result.ThemeId);
        Assert.True(result.ShouldPersist);
    }

    /// <summary>
    /// Magellan is the default theme, and the four places that state a default agree on it: the
    /// engine's fallback, the settings type, the bundled factory file the markers and the reset
    /// read, and the panel before anything is loaded.
    /// </summary>
    [Fact]
    public void TheDefaultThemeIsMagellanEverywhereADefaultIsStated()
    {
        string factoryFile = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "config", "settings.default.json"));

        Assert.Equal(ThemeNames.Magellan, HeimdallThemeService.DefaultTheme);
        Assert.Equal(ThemeNames.Magellan, Heimdall.Core.Configuration.AppSettings.FactoryDefaultTheme);
        Assert.Equal(ThemeNames.Magellan, new Heimdall.Core.Configuration.AppSettings().DefaultTheme);
        Assert.Equal(
            ThemeNames.Magellan,
            Heimdall.App.ViewModels.SettingsViewModel.ParseFactoryDefaults(factoryFile).DefaultTheme);
    }

    /// <summary>The former default stays a valid explicit choice and is not rewritten.</summary>
    [Fact]
    public void ResolveThemeId_WithTheFormerDefault_KeepsItWithoutPersisting()
    {
        ThemeResolution result = HeimdallThemeService.ResolveThemeId(ThemeNames.Drakul);

        Assert.Equal(ThemeNames.Drakul, result.ThemeId);
        Assert.False(result.ShouldPersist);
    }

    [Theory]
    [InlineData(ThemeNames.Striga)]
    [InlineData(ThemeNames.Carmilla)]
    public void ResolveThemeId_WithCollidingName_TreatsItAsThemeForgeId(string themeName)
    {
        ThemeResolution result = HeimdallThemeService.ResolveThemeId(themeName);

        Assert.Equal(themeName, result.ThemeId);
        Assert.False(result.ShouldPersist);
    }
}
