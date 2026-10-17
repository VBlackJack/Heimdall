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
using Heimdall.App.Tests.Views.EmbeddedRdp;

namespace Heimdall.App.Tests;

/// <summary>
/// Views that write their text or colours from code follow a language or a theme changed while
/// they are open, and let go of the singleton that tells them when they close.
/// </summary>
/// <remarks>
/// The subscriptions are pinned as statements of the method that makes them, and the matching
/// unsubscription as a statement of <c>Dispose</c>: <see cref="Heimdall.Core.Localization.LocalizationManager"/>
/// and <see cref="Heimdall.App.Services.HeimdallThemeService"/> outlive every tab, so a view that
/// subscribes and never unsubscribes is kept alive by them after it is closed.
/// </remarks>
public sealed class LiveLocaleAndThemeRefreshTests
{
    private const string ToolInitialize = "public void Initialize(ToolContext? context, LocalizationManager? localizer)";
    private const string Dispose = "public void Dispose()";
    private const string SubscribeLocale = "if (_localizer is not null) _localizer.LocaleChanged += OnLocaleChanged;";
    private const string UnsubscribeLocale = "if (_localizer is not null) _localizer.LocaleChanged -= OnLocaleChanged;";
    private const string SubscribeTheme = "if (_themeService is not null) _themeService.ThemeChanged += OnThemeChanged;";
    private const string UnsubscribeTheme = "if (_themeService is not null) _themeService.ThemeChanged -= OnThemeChanged;";

    /// <summary>The tool views that write labels from code.</summary>
    public static TheoryData<string> LocaleTools { get; } = new()
    {
        "DiagramEditorView.xaml.cs",
        "LogViewerView.xaml.cs",
        "NetworkCartographyView.xaml.cs",
        "PasswordAuditView.xaml.cs",
        "PasswordGeneratorView.xaml.cs",
        "PingToolView.xaml.cs",
        "PortScannerView.xaml.cs",
        "SecNumCloudAuditView.xaml.cs",
        "SshKeyAuditView.xaml.cs",
        "TlsAuditView.xaml.cs",
    };

    /// <summary>The tool views that paint results with brushes resolved from code.</summary>
    public static TheoryData<string> ThemeTools { get; } = new()
    {
        "SshKeyAuditView.xaml.cs",
        "TlsAuditView.xaml.cs",
    };

    [Theory]
    [MemberData(nameof(LocaleTools))]
    public void AToolFollowsTheLanguageAndLetsGoWhenClosed(string file)
    {
        string source = ToolSource(file);

        Assert.True(ViewSource.IsStatementOfTheMethodBody(Logic(source, ToolInitialize), SubscribeLocale), $"{file}: Initialize does not listen for a language change");
        Assert.True(ViewSource.IsStatementOfTheMethodBody(Logic(source, Dispose), UnsubscribeLocale), $"{file}: Dispose keeps listening for a language change");
    }

    [Theory]
    [MemberData(nameof(ThemeTools))]
    public void AToolFollowsTheThemeAndLetsGoWhenClosed(string file)
    {
        string source = ToolSource(file);

        Assert.True(ViewSource.IsStatementOfTheMethodBody(Logic(source, ToolInitialize), SubscribeTheme), $"{file}: Initialize does not listen for a theme change");
        Assert.True(ViewSource.IsStatementOfTheMethodBody(Logic(source, Dispose), UnsubscribeTheme), $"{file}: Dispose keeps listening for a theme change");
    }

    [Fact]
    public void TheDiffToolRebuildsItsBrushesOnAThemeChange()
    {
        string source = ToolSource("TextDiffView.xaml.cs");

        Assert.True(ViewSource.IsStatementOfTheMethodBody(Logic(source, ToolInitialize), DiffSubscribeTheme), "the diff tool does not listen for a theme change");
        Assert.True(ViewSource.IsStatementOfTheMethodBody(Logic(source, Dispose), DiffUnsubscribeTheme), "the diff tool keeps listening for a theme change");
    }

    private const string DiffSubscribeTheme = "if (_themeService is not null) { _themeService.ThemeChanged += OnThemeChanged; }";
    private const string DiffUnsubscribeTheme = "if (_themeService is not null) { _themeService.ThemeChanged -= OnThemeChanged; }";

    [Fact]
    public void TheMarkdownEditorTakesTheThemeFromOneDecision()
    {
        string source = ToolSource("MilkdownEditorControl.xaml.cs");

        Assert.True(ViewSource.IsStatementOfTheMethodBody(Logic(source, MilkdownInitialize), SubscribeTheme), "the editor does not listen for a theme change");
        Assert.True(ViewSource.IsStatementOfTheMethodBody(Logic(source, MilkdownOnThemeChanged), ApplyThemeStatement), "a theme change does not reach the editor");
    }

    private const string MilkdownInitialize = "public async Task InitializeAsync()";
    private const string MilkdownOnThemeChanged = "private void OnThemeChanged(string _)";
    private const string ApplyThemeStatement = "ApplyCurrentTheme();";

    /// <summary>
    /// The session views wrote an English fallback beside each key, so a missing key showed
    /// English in a French interface instead of failing visibly.
    /// </summary>
    [Theory]
    [InlineData("EmbeddedCitrixView.xaml.cs")]
    [InlineData("EmbeddedVncView.xaml.cs")]
    public void ASessionViewWritesNoEnglishFallbackBesideAKey(string file)
    {
        string source = File.ReadAllText(Path.Combine(ViewSource.RepoRoot(), "src", "Heimdall.App", "Views", file));

        Assert.DoesNotContain("] ?? \"", source, StringComparison.Ordinal);
    }

    private static string ToolSource(string file) =>
        File.ReadAllText(Path.Combine(ViewSource.RepoRoot(), "src", "Heimdall.App", "Views", "Tools", file));

    private static string Logic(string source, string signature) =>
        ViewSource.HandlerBody(ViewSource.WithoutCommentsAndLiterals(source), signature);
}
