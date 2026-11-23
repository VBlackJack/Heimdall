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
using System.Text.Json;
using Heimdall.App.Services;
using Heimdall.App.ViewModels;
using Heimdall.App.Views.Tools;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;
using Heimdall.Core.Security;

namespace Heimdall.App.Tests;

/// <summary>
/// A view offers an action that opens another tool only when that tool may be used: the pure
/// decision, and the predicate the session manager hands every tool through its context.
/// </summary>
public sealed class ToolOfferAvailabilityTests
{
    private const string OfferProbeId = "OFFERPROBE";

    /// <summary>A tool view that keeps the context it was initialised with.</summary>
    private sealed class CapturingToolView : IToolView
    {
        public ToolContext? Received { get; private set; }

        public void Initialize(ToolContext? context, LocalizationManager? localizer) => Received = context;

        public void Dispose()
        {
        }
    }

    [Fact]
    public void IsToolOffered_WithoutAPredicate_OffersEveryTool()
    {
        Assert.True(ToolContextMenuHelper.IsToolOffered(null, "PING"));
        Assert.True(ToolContextMenuHelper.IsToolOffered(null, "DIAGRAM"));
        Assert.True(ToolContextMenuHelper.IsToolOffered(null, "NOSUCHTOOL"));
    }

    [Fact]
    public void IsToolOffered_APredicateThatRefusesOneId_HidesThatIdOnly()
    {
        Func<string, bool> refusesPortScan = id => !string.Equals(id, "PORTSCAN", StringComparison.Ordinal);

        Assert.False(ToolContextMenuHelper.IsToolOffered(refusesPortScan, "PORTSCAN"));
        Assert.True(ToolContextMenuHelper.IsToolOffered(refusesPortScan, "PING"));
        Assert.True(ToolContextMenuHelper.IsToolOffered(refusesPortScan, "CERT"));
        Assert.True(ToolContextMenuHelper.IsToolOffered(refusesPortScan, "DIAGRAM"));
    }

    [Fact]
    public void ToolContext_BuiltWithoutAPredicate_LeavesItNull()
    {
        Assert.Null(new ToolContext().IsToolAvailable);
    }

    [Fact]
    public void CreateToolControl_HandsTheToolAPredicateThatFollowsTheAvailabilityService()
    {
        string rootPath = Path.Combine(Path.GetTempPath(), "heimdall-offer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootPath);
        try
        {
            CapturingToolView view = new();
            ToolRegistry registry = new();
            registry.RegisterToolForTests(ToolRegistryPackTests.Probe(OfferProbeId, pack: null), () => view);
            AppSettings settings = new() { DisabledToolPacks = [nameof(ToolPack.Audit)] };
            Action<AppSettings>? publish = null;
            ToolAvailability availability = new(
                registry,
                new ToolPackPolicy(static _ => null),
                () => settings,
                handler => publish = handler);
            EmbeddedSessionManager sessionManager = new(
                new LocalizationManager(), null!, null!, null!, registry, null!, null!, null!, null!, new ConfigManager(rootPath), availability);

            sessionManager.CreateToolControl(new SessionTabViewModel(), OfferProbeId, null);

            Func<string, bool> predicate = Assert.IsType<Func<string, bool>>(view.Received?.IsToolAvailable);
            Assert.False(predicate("PORTSCAN"));
            Assert.True(predicate("DNS"));
            Assert.True(predicate("DIAGRAM"));

            // The same predicate sees a later change, it is not a snapshot.
            settings = new AppSettings { DisabledToolPacks = [nameof(ToolPack.Administration)] };
            Assert.NotNull(publish);
            publish!(settings);

            Assert.True(predicate("PORTSCAN"));
            Assert.False(predicate("DIAGRAM"));
        }
        finally
        {
            try
            {
                Directory.Delete(rootPath, recursive: true);
            }
            catch (IOException)
            {
                // Test cleanup.
            }
        }
    }
}

/// <summary>The accessible-name sentence of an unavailable saved tool entry exists in every catalogue.</summary>
public sealed class ToolEntryLocaleKeyTests
{
    private const string UnavailableNameKey = "SessionTreeServerAccessibleNameToolUnavailable";

    private static Dictionary<string, string> Load(string language)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "locales", language + ".json");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))!;
    }

    [Theory]
    [InlineData("en")]
    [InlineData("fr")]
    [InlineData("es")]
    public void TheUnavailableNameSentenceExistsAndTakesTheNameAndTheReason(string language)
    {
        Dictionary<string, string> catalogue = Load(language);

        Assert.True(catalogue.TryGetValue(UnavailableNameKey, out string? text), $"{language} lacks {UnavailableNameKey}");
        Assert.Contains("{0}", text);
        Assert.Contains("{1}", text);
    }

    [Fact]
    public void FrenchAndSpanishAreTranslatedNotCopiedFromEnglish()
    {
        string english = Load("en")[UnavailableNameKey];

        Assert.NotEqual(english, Load("fr")[UnavailableNameKey]);
        Assert.NotEqual(english, Load("es")[UnavailableNameKey]);
    }
}
