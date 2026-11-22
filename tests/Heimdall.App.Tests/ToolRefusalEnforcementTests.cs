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
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;
using Heimdall.Core.Security;
using Heimdall.Core.StateMachine;
using Heimdall.Ssh;

namespace Heimdall.App.Tests;

/// <summary>
/// A tool whose pack is switched off, or forbidden by policy, is refused at every gate: the
/// shell's open path, the split path and the control factory.
/// </summary>
public sealed partial class SessionCoordinatorPreMountTests
{
    private const string AvailableToolId = "HASH";
    private const string OtherAvailableToolId = "JWT";
    private const string BackstopProbeId = "BACKSTOPPROBE";

    /// <summary>An availability that answers per tool and Available for any tool not named.</summary>
    private sealed class ScriptedToolAvailability : IToolAvailability
    {
        private readonly Dictionary<string, ToolAvailabilityState> _states = new(StringComparer.OrdinalIgnoreCase);

        public event Action? Changed
        {
            add { }
            remove { }
        }

        public void Set(string toolId, ToolAvailabilityState state) => _states[toolId] = state;

        public ToolAvailabilityState GetState(string toolId)
            => _states.TryGetValue(toolId, out ToolAvailabilityState state) ? state : ToolAvailabilityState.Available;

        public bool IsPackEnabled(ToolPack pack) => true;

        public bool IsPackForbiddenByPolicy(ToolPack pack) => false;
    }

    private static async Task<LocalizationManager> LoadEnglishAsync()
    {
        LocalizationManager localizer = new();
        await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "en");
        return localizer;
    }

    private static string LabelOf(string toolId, LocalizationManager localizer)
        => new ToolRegistry().GetById(toolId)!.ResolveLabel(localizer.GetString);

    private static string CreateTempRoot(string prefix)
    {
        string rootPath = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootPath);
        return rootPath;
    }

    private static void DeleteTempRoot(string rootPath)
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

    [Theory]
    [InlineData(ToolAvailabilityState.DisabledByUser, ToolUnavailableException.DisabledByUserMessageKey)]
    [InlineData(ToolAvailabilityState.ForbiddenByPolicy, ToolUnavailableException.ForbiddenByPolicyMessageKey)]
    public async Task OpenToolTabAsync_RefusedTool_AddsNoSessionRecordsNoRecentAndNamesTheReason(
        ToolAvailabilityState state,
        string expectedKey)
    {
        ScriptedToolAvailability availability = new();
        availability.Set(AvailableToolId, state);
        using TestHarness harness = TestHarness.Create(toolAvailability: availability);
        LocalizationManager localizer = await LoadEnglishAsync();

        ToolOpenOutcome outcome = await harness.Main.OpenToolTabAsync(AvailableToolId, "Hash", null);

        Assert.Equal(ToolOpenOutcome.Refused, outcome);
        Assert.Empty(harness.Main.Connection.ActiveSessions);
        Assert.Equal(0, harness.EmbeddedSessionManager.CreateToolControlCalls);
        Assert.Empty(harness.Main.RecentToolIds);
        Assert.Equal(localizer.Format(expectedKey, LabelOf(AvailableToolId, localizer)), harness.Main.StatusText);
        Assert.NotEqual(expectedKey, harness.Main.StatusText);
    }

    [Fact]
    public async Task OpenToolTabAsync_DisabledToolWithAnOpenTab_DoesNotFocusIt()
    {
        ScriptedToolAvailability availability = new();
        using TestHarness harness = TestHarness.Create(toolAvailability: availability);
        await harness.Main.OpenToolTabAsync(AvailableToolId, "Hash", null);
        await harness.Main.OpenToolTabAsync(OtherAvailableToolId, "Jwt", null);
        SessionTabViewModel focused = Assert.IsType<SessionTabViewModel>(harness.Main.Connection.ActiveSession);
        Assert.Equal("TOOL:" + OtherAvailableToolId, focused.ConnectionType);
        availability.Set(AvailableToolId, ToolAvailabilityState.DisabledByUser);

        ToolOpenOutcome outcome = await harness.Main.OpenToolTabAsync(AvailableToolId, "Hash", null);

        Assert.Equal(ToolOpenOutcome.Refused, outcome);
        Assert.Same(focused, harness.Main.Connection.ActiveSession);
        Assert.Equal(2, harness.Main.Connection.ActiveSessions.Count);
        Assert.Equal(2, harness.EmbeddedSessionManager.CreateToolControlCalls);
    }

    [Fact]
    public async Task OpenToolTabAsync_AvailableTool_OpensAndIsRecordedAsRecent()
    {
        using TestHarness harness = TestHarness.Create(toolAvailability: new ScriptedToolAvailability());

        ToolOpenOutcome outcome = await harness.Main.OpenToolTabAsync(AvailableToolId, "Hash", null);

        Assert.Equal(ToolOpenOutcome.Opened, outcome);
        Assert.Single(harness.Main.Connection.ActiveSessions);
        Assert.Equal([AvailableToolId], harness.Main.RecentToolIds);
    }

    [Fact]
    public async Task OpenToolTabAsync_AvailableToolAlreadyOpen_FocusesItAndMovesItToTheFrontOfTheRecents()
    {
        using TestHarness harness = TestHarness.Create(toolAvailability: new ScriptedToolAvailability());
        await harness.Main.OpenToolTabAsync(AvailableToolId, "Hash", null);
        await harness.Main.OpenToolTabAsync(OtherAvailableToolId, "Jwt", null);

        ToolOpenOutcome outcome = await harness.Main.OpenToolTabAsync(AvailableToolId, "Hash", null);

        Assert.Equal(ToolOpenOutcome.FocusedExisting, outcome);
        Assert.Equal(2, harness.Main.Connection.ActiveSessions.Count);
        Assert.Equal([AvailableToolId, OtherAvailableToolId], harness.Main.RecentToolIds);
    }

    [Fact]
    public async Task OpenToolTabAsync_UnknownTool_StillOpensItsPlaceholder()
    {
        ScriptedToolAvailability availability = new();
        availability.Set("NOSUCHTOOL", ToolAvailabilityState.Unknown);
        using TestHarness harness = TestHarness.Create(toolAvailability: availability);

        ToolOpenOutcome outcome = await harness.Main.OpenToolTabAsync("NOSUCHTOOL", "Nothing", null);

        Assert.Equal(ToolOpenOutcome.Opened, outcome);
        Assert.Single(harness.Main.Connection.ActiveSessions);
    }

    [Theory]
    [InlineData(ToolAvailabilityState.DisabledByUser, ToolUnavailableException.DisabledByUserMessageKey)]
    [InlineData(ToolAvailabilityState.ForbiddenByPolicy, ToolUnavailableException.ForbiddenByPolicyMessageKey)]
    public async Task SplitSessionWithTool_RefusedTool_AddsNoPaneSetsTheStatusAndReportsNoSplit(
        ToolAvailabilityState state,
        string expectedKey)
    {
        ScriptedToolAvailability availability = new();
        availability.Set(AvailableToolId, state);
        string rootPath = CreateTempRoot("heimdall-split-refusal-");
        try
        {
            LocalizationManager localizer = await LoadEnglishAsync();
            FakeEmbeddedSessionManager sessionManager = new();
            using TunnelManager tunnelManager = new();
            SplitService splitService = new(
                new ConfigManager(rootPath),
                localizer,
                new ConnectionStateMachine(),
                tunnelManager,
                sessionManager,
                connectionService: null!,
                new ToolRegistry(),
                dialogService: null!,
                new PaneCloseArbiter(),
                availability);
            string? status = null;
            splitService.SetStatusText = text => status = text;
            SessionTabViewModel session = new();
            SessionPaneModel root = new() { ConnectionType = "SSH" };
            session.RootContent = root;

            bool split = splitService.SplitSessionWithTool(session, AvailableToolId, SplitOrientation.Vertical);

            Assert.False(split);
            Assert.Same(root, session.RootContent);
            Assert.Equal(0, sessionManager.CreateToolControlCalls);
            Assert.Equal(localizer.Format(expectedKey, LabelOf(AvailableToolId, localizer)), status);
        }
        finally
        {
            DeleteTempRoot(rootPath);
        }
    }

    [Fact]
    public void SplitSessionWithTool_AvailableTool_ReportsTheSplit()
    {
        string rootPath = CreateTempRoot("heimdall-split-ok-");
        try
        {
            FakeEmbeddedSessionManager sessionManager = new();
            using TunnelManager tunnelManager = new();
            SplitService splitService = new(
                new ConfigManager(rootPath),
                new LocalizationManager(),
                new ConnectionStateMachine(),
                tunnelManager,
                sessionManager,
                connectionService: null!,
                new ToolRegistry(),
                dialogService: null!,
                new PaneCloseArbiter(),
                new ScriptedToolAvailability());
            SessionTabViewModel session = new();
            session.RootContent = new SessionPaneModel { ConnectionType = "SSH" };

            bool split = splitService.SplitSessionWithTool(session, AvailableToolId, SplitOrientation.Vertical);

            Assert.True(split);
            Assert.IsType<SplitContainerModel>(session.RootContent);
            Assert.Equal(1, sessionManager.CreateToolControlCalls);
        }
        finally
        {
            DeleteTempRoot(rootPath);
        }
    }

    [Theory]
    [InlineData(true, ToolAvailabilityState.DisabledByUser)]
    [InlineData(false, ToolAvailabilityState.ForbiddenByPolicy)]
    public void CreateToolControl_ToolOfAnOffPack_ThrowsTheDedicatedExceptionAndBuildsNoView(
        bool disabledBySettings,
        ToolAvailabilityState expectedState)
    {
        string rootPath = CreateTempRoot("heimdall-backstop-");
        try
        {
            int factoryCalls = 0;
            ToolRegistry registry = new();
            registry.RegisterToolForTests(
                ToolRegistryPackTests.Probe(BackstopProbeId, ToolPack.Audit),
                () =>
                {
                    factoryCalls++;
                    throw new InvalidOperationException("the factory must not run");
                });
            AppSettings settings = disabledBySettings
                ? new AppSettings { DisabledToolPacks = [nameof(ToolPack.Audit)] }
                : new AppSettings();
            ToolPackPolicy policy = disabledBySettings
                ? new ToolPackPolicy(static _ => null)
                : new ToolPackPolicy(static name => name == ToolPackPolicy.ValueNameFor(ToolPack.Audit) ? ToolPackPolicy.ForbiddenValue : null);
            ToolAvailability availability = new(registry, policy, () => settings, static _ => { });
            EmbeddedSessionManager sessionManager = new(
                new LocalizationManager(), null!, null!, null!, registry, null!, null!, null!, null!, new ConfigManager(rootPath), availability);

            ToolUnavailableException thrown = Assert.Throws<ToolUnavailableException>(
                () => sessionManager.CreateToolControl(new SessionTabViewModel(), BackstopProbeId, null));

            Assert.Equal(BackstopProbeId, thrown.ToolId);
            Assert.Equal(expectedState, thrown.State);
            Assert.Equal(0, factoryCalls);
        }
        finally
        {
            DeleteTempRoot(rootPath);
        }
    }

    [Fact]
    public void ToolUnavailableException_OnlyDisabledAndForbiddenAreRefusals()
    {
        Assert.True(ToolUnavailableException.IsRefusal(ToolAvailabilityState.DisabledByUser));
        Assert.True(ToolUnavailableException.IsRefusal(ToolAvailabilityState.ForbiddenByPolicy));
        Assert.False(ToolUnavailableException.IsRefusal(ToolAvailabilityState.Available));
        Assert.False(ToolUnavailableException.IsRefusal(ToolAvailabilityState.Unknown));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ToolUnavailableException.MessageKeyFor(ToolAvailabilityState.Unknown));
    }
}

/// <summary>The texts of a refusal exist in every shipped catalogue.</summary>
public sealed class ToolRefusalLocaleKeyTests
{
    private const string PrivilegeLaunchPolicyKey = "ToolPrivLaunchStatusForbiddenByPolicy";

    private static readonly string[] RefusalKeys =
    [
        ToolUnavailableException.DisabledByUserMessageKey,
        ToolUnavailableException.ForbiddenByPolicyMessageKey,
    ];

    private static Dictionary<string, string> Load(string language)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "locales", language + ".json");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))!;
    }

    [Theory]
    [InlineData("en")]
    [InlineData("fr")]
    [InlineData("es")]
    public void EveryRefusalTextExistsAndTakesTheToolName(string language)
    {
        Dictionary<string, string> catalogue = Load(language);

        foreach (string key in RefusalKeys)
        {
            Assert.True(catalogue.TryGetValue(key, out string? text), $"{language} lacks {key}");
            Assert.False(string.IsNullOrWhiteSpace(text));
            Assert.Contains("{0}", text);
        }
    }

    [Theory]
    [InlineData("en")]
    [InlineData("fr")]
    [InlineData("es")]
    public void ThePrivilegeLaunchPolicyTextExists(string language)
    {
        Dictionary<string, string> catalogue = Load(language);

        Assert.True(catalogue.TryGetValue(PrivilegeLaunchPolicyKey, out string? text), $"{language} lacks {PrivilegeLaunchPolicyKey}");
        Assert.False(string.IsNullOrWhiteSpace(text));
    }

    [Fact]
    public void FrenchAndSpanishTextsAreTranslatedNotCopiedFromEnglish()
    {
        Dictionary<string, string> english = Load("en");
        foreach (string language in new[] { "fr", "es" })
        {
            Dictionary<string, string> other = Load(language);
            foreach (string key in RefusalKeys.Append(PrivilegeLaunchPolicyKey))
            {
                Assert.NotEqual(english[key], other[key]);
            }
        }
    }
}
