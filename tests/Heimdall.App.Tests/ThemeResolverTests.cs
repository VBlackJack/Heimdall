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

using System.Reflection;
using System.Windows;
using Heimdall.App.Services;
using ThemeForge.Theme;

namespace Heimdall.App.Tests;

public sealed class ThemeResolverTests
{
    [Fact]
    public void CreateBridgeDictionary_FromCompiledResource_LoadsBridgeBrushes()
    {
        ResourceDictionary? bridge = null;
        Exception? failure = null;
        Thread thread = new(() =>
        {
            Application? application = null;
            bool createdApplication = false;
            try
            {
                application = Application.Current;
                if (application is null)
                {
                    application = new Application();
                    createdApplication = true;
                }

                bridge = HeimdallThemeService.CreateBridgeDictionary();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                if (createdApplication && application is not null)
                {
                    application.Shutdown();
                    application.Dispatcher.InvokeShutdown();
                    ResetApplicationSingletonForTest(application);
                }
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Theme bridge load timed out.");

        Assert.Null(failure);
        ResourceDictionary loadedBridge = Assert.IsType<ResourceDictionary>(bridge);
        Assert.True(loadedBridge.Contains("BackgroundBrush"));
        Assert.Equal(
            "pack://application:,,,/Heimdall;component/Themes/HeimdallThemeBridge.xaml",
            loadedBridge.Source.OriginalString);
    }

    private static void ResetApplicationSingletonForTest(Application application)
    {
        Assert.Same(application, Application.Current);
        BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        FieldInfo? appInstance = typeof(Application).GetField("_appInstance", flags);
        FieldInfo? appCreated = typeof(Application).GetField("_appCreatedInThisAppDomain", flags);
        FieldInfo? isShuttingDown = typeof(Application).GetField("_isShuttingDown", flags);
        Assert.NotNull(appInstance);
        Assert.NotNull(appCreated);
        Assert.NotNull(isShuttingDown);
        appInstance.SetValue(null, null);
        appCreated.SetValue(null, false);
        isShuttingDown.SetValue(null, false);
        Assert.Null(Application.Current);
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
    public void ResolveThemeId_WithInvalidOrLegacyName_DefaultsToDrakulAndPersists(
        string? persisted)
    {
        ThemeResolution result = HeimdallThemeService.ResolveThemeId(persisted);

        Assert.Equal(ThemeNames.Drakul, result.ThemeId);
        Assert.True(result.ShouldPersist);
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
