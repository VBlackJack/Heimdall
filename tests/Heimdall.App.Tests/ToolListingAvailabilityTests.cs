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
using Heimdall.App.Services;
using Heimdall.App.Tests.TestHelpers;
using Heimdall.App.ViewModels;
using Heimdall.App.ViewModels.CommandPalette;
using Heimdall.App.ViewModels.Sidebar;
using Heimdall.Core.Configuration;
using Heimdall.Core.Models;
using Heimdall.Core.Security;

namespace Heimdall.App.Tests;

/// <summary>
/// Every listing surface shows the same projection of the catalogue: a tool of a pack the user
/// turned off, or the organisation forbids, is not listed, a detected external tool always is, and
/// the stored favorite and recent ids survive so the tool returns with its pack.
/// </summary>
/// <remarks>
/// Covered here: the shared projection, the sidebar tree, the favorites and recents resolution
/// used by the Tools tab, the palette (initial list, "tools", fuzzy ranking, explicit argument).
/// Not covered: the Tools tab card rendering and the tool picker, which build WPF elements, and
/// the marshalling of the change notification in the main window.
/// </remarks>
public sealed partial class SessionCoordinatorPreMountTests
{
    private const string ListedEssentialsToolId = "HASH";
    private const string ListedNetworkToolId = "PING";
    private const string AuditToolId = "PORTSCAN";
    private const string AuditToolQuery = "portscan";
    private const string AuditToolArgumentQuery = "portscan 10.0.0.1";
    private const string NetworkToolArgumentQuery = "ping 8.8.8.8";
    private const string DetectedToolId = "EXT:SYSINTERNALS:PSEXEC";
    private const string AllToolsQuery = "tools";
    private const string PaletteToolItemPrefix = "tool-";
    private const string PaletteArgumentSeparator = "|";
    private const string PaletteResetQuery = "zzzz";

    /// <summary>A settings source the availability service reads and subscribes to.</summary>
    private sealed class ListingSettingsSource
    {
        public AppSettings Current { get; private set; } = new();

        public Action<AppSettings>? Handler { get; private set; }

        public void Subscribe(Action<AppSettings> handler) => Handler += handler;

        public void Publish(params string[] disabledPacks)
        {
            Current = new AppSettings { DisabledToolPacks = [.. disabledPacks] };
            Handler?.Invoke(Current);
        }
    }

    private static ToolAvailability CreateRealAvailability(
        ToolRegistry registry,
        ListingSettingsSource source,
        params ToolPack[] forbiddenByPolicy)
    {
        HashSet<string> forbidden = forbiddenByPolicy.Select(ToolPackPolicy.ValueNameFor).ToHashSet(StringComparer.Ordinal);
        ToolPackPolicy policy = new(
            name => forbidden.Contains(name) ? ToolPackPolicy.ForbiddenValue : ToolPackPolicy.NoRestrictionValue);
        return new ToolAvailability(registry, policy, () => source.Current, source.Subscribe);
    }

    /// <summary>Builds the availability with the Audit pack off, by the user's switch or by policy.</summary>
    private static ToolAvailability CreateWithAuditUnavailable(
        ToolRegistry registry,
        ListingSettingsSource source,
        bool byPolicy)
    {
        if (byPolicy)
        {
            return CreateRealAvailability(registry, source, ToolPack.Audit);
        }

        ToolAvailability availability = CreateRealAvailability(registry, source);
        source.Publish(nameof(ToolPack.Audit));
        return availability;
    }

    private static void RegisterDetectedTool(ToolRegistry registry)
        => registry.RegisterExternalTools(
        [
            new ExternalToolInfo
            {
                Id = "PSEXEC",
                Name = "PsExec",
                ExecutablePath = @"C:\Tools\Sysinternals\PsExec.exe",
                ProviderName = "Sysinternals",
            },
        ]);

    private static void SetSettings(MainViewModel main, AppSettings settings)
        => typeof(MainViewModel)
            .GetField("_currentSettings", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(main, settings);

    private static List<string> ToolIdsOf(IEnumerable<ToolDescriptor> descriptors)
        => descriptors.Select(descriptor => descriptor.Id).ToList();

    private static List<string> SidebarToolIds(IEnumerable<SidebarToolCategoryViewModel> categories)
        => categories.SelectMany(category => category.Tools).Select(tool => tool.Id).ToList();

    private static List<ToolDescriptor> ResolveListed(
        ToolAvailability availability,
        ToolRegistry registry,
        IEnumerable<string> storedIds)
        => ToolsTabPopulationService.ResolveListedTools(availability.ListedTools(registry.All).ToList(), storedIds);

    private static string PaletteItemId(string toolId) => PaletteToolItemPrefix + toolId.ToLowerInvariant();

    private static List<string> PaletteResultIds(CommandPaletteViewModel palette, string query)
    {
        // The search runs when the text changes: park it on another value so asking for the same
        // query again, after a change of availability, searches again.
        palette.SearchText = PaletteResetQuery;
        palette.SearchText = query;
        return palette.Results.Select(item => item.Id).ToList();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheProjection_OmitsAnUnavailablePackAndKeepsTheRestAndDetectedTools(bool byPolicy)
    {
        ToolRegistry registry = new();
        RegisterDetectedTool(registry);
        ToolAvailability availability = CreateWithAuditUnavailable(registry, new ListingSettingsSource(), byPolicy);

        List<string> listed = ToolIdsOf(availability.ListedTools(registry.All));

        Assert.DoesNotContain(AuditToolId, listed);
        Assert.Contains(ListedEssentialsToolId, listed);
        Assert.Contains(DetectedToolId, listed);
        Assert.Equal(registry.All.Count(descriptor => descriptor.Pack != ToolPack.Audit), listed.Count);
    }

    [Fact]
    public void TheToolsTabTotal_FollowsTheProjection()
    {
        ToolRegistry registry = new();
        ListingSettingsSource source = new();
        ToolAvailability availability = CreateRealAvailability(registry, source);
        int allCount = availability.ListedTools(registry.All).Count();
        int auditCount = registry.All.Count(descriptor => descriptor.Pack == ToolPack.Audit);
        Assert.True(auditCount > 0, "The registry has no Audit tool; the comparison would prove nothing.");

        source.Publish(nameof(ToolPack.Audit));

        Assert.Equal(allCount - auditCount, availability.ListedTools(registry.All).Count());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheSidebarTree_OmitsTheToolsOfAnUnavailablePackAndKeepsDetectedTools(bool byPolicy)
    {
        ToolRegistry registry = new();
        RegisterDetectedTool(registry);
        ToolAvailability availability = CreateWithAuditUnavailable(registry, new ListingSettingsSource(), byPolicy);
        using TestHarness harness = TestHarness.Create(toolAvailability: availability);
        SetSettings(harness.Main, new AppSettings { FavoriteToolIds = [AuditToolId, ListedEssentialsToolId] });

        List<string> ids = SidebarToolIds(new ToolsTabPopulationService(registry).BuildSidebarToolsData(harness.Main));

        Assert.DoesNotContain(AuditToolId, ids);
        Assert.Contains(ListedEssentialsToolId, ids);
        Assert.Contains(DetectedToolId, ids);
    }

    [Fact]
    public void ADisabledFavorite_IsHiddenKeepsItsStoredIdAndComesBackWithItsPack()
    {
        ToolRegistry registry = new();
        ListingSettingsSource source = new();
        ToolAvailability availability = CreateRealAvailability(registry, source);
        using TestHarness harness = TestHarness.Create(toolAvailability: availability);
        SetSettings(harness.Main, new AppSettings { FavoriteToolIds = [AuditToolId, ListedEssentialsToolId] });
        ToolsTabPopulationService service = new(registry);

        source.Publish(nameof(ToolPack.Audit));
        Assert.Equal(
            [ListedEssentialsToolId],
            service.BuildSidebarToolsData(harness.Main).First().Tools.Select(tool => tool.Id).ToArray());
        Assert.Equal([AuditToolId, ListedEssentialsToolId], harness.Main.FavoriteToolIds);
        Assert.Equal([ListedEssentialsToolId], ToolIdsOf(ResolveListed(availability, registry, harness.Main.FavoriteToolIds)));

        source.Publish();
        Assert.Equal(
            [ListedEssentialsToolId, AuditToolId],
            service.BuildSidebarToolsData(harness.Main).First().Tools.Select(tool => tool.Id).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(
            [AuditToolId, ListedEssentialsToolId],
            ToolIdsOf(ResolveListed(availability, registry, harness.Main.FavoriteToolIds)));
    }

    [Fact]
    public void AFavoritesSectionWhoseToolsAreAllDisabled_IsEmpty()
    {
        ToolRegistry registry = new();
        ToolAvailability availability = CreateWithAuditUnavailable(registry, new ListingSettingsSource(), byPolicy: false);
        using TestHarness harness = TestHarness.Create(toolAvailability: availability);
        SetSettings(harness.Main, new AppSettings { FavoriteToolIds = [AuditToolId] });

        SidebarToolCategoryViewModel favorites = new ToolsTabPopulationService(registry)
            .BuildSidebarToolsData(harness.Main)
            .First();

        Assert.Empty(favorites.Tools);
        Assert.Equal(0, favorites.VisibleCount);
        Assert.Empty(ResolveListed(availability, registry, harness.Main.FavoriteToolIds));
    }

    [Fact]
    public void ARecentOfADisabledTool_IsHidden()
    {
        ToolRegistry registry = new();
        ListingSettingsSource source = new();
        ToolAvailability availability = CreateRealAvailability(registry, source);
        using TestHarness harness = TestHarness.Create(toolAvailability: availability);
        harness.Main.TrackRecentTool(AuditToolId);
        harness.Main.TrackRecentTool(ListedEssentialsToolId);

        source.Publish(nameof(ToolPack.Audit));

        Assert.Equal([ListedEssentialsToolId], ToolIdsOf(ResolveListed(availability, registry, harness.Main.RecentToolIds)));
    }

    [Fact]
    public void TheSidebarTree_ComesBackAfterThePackIsEnabledAgainAndChangedIsRaised()
    {
        ToolRegistry registry = new();
        ListingSettingsSource source = new();
        ToolAvailability availability = CreateRealAvailability(registry, source);
        int changed = 0;
        availability.Changed += () => changed++;
        using TestHarness harness = TestHarness.Create(toolAvailability: availability);
        using SidebarViewModel sidebar = new(
            harness.Main,
            harness.Main.GetLocalizer(),
            null!,
            new ToolsTabPopulationService(registry),
            new FakeToolContextProvider(),
            new FakeUiDispatcher(checkAccess: true));
        sidebar.SetActiveTab(true);
        Assert.Contains(AuditToolId, SidebarToolIds(sidebar.ToolsCategories));

        source.Publish(nameof(ToolPack.Audit));
        Assert.Equal(1, changed);
        sidebar.OnExternalToolsChanged();
        Assert.DoesNotContain(AuditToolId, SidebarToolIds(sidebar.ToolsCategories));

        source.Publish();
        Assert.Equal(2, changed);
        sidebar.OnExternalToolsChanged();
        Assert.Contains(AuditToolId, SidebarToolIds(sidebar.ToolsCategories));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ThePalette_DoesNotOfferAToolOfAnUnavailablePackButStillOffersOthersAndDetectedTools(bool byPolicy)
    {
        ToolRegistry registry = new();
        RegisterDetectedTool(registry);
        ToolAvailability availability = CreateWithAuditUnavailable(registry, new ListingSettingsSource(), byPolicy);
        using TestHarness harness = TestHarness.Create(toolAvailability: availability);
        RegisterDetectedTool(harness.Main.ToolRegistry);
        CommandPaletteViewModel palette = harness.Main.CommandPalette;
        string auditItemId = PaletteItemId(AuditToolId);

        List<string> everyTool = PaletteResultIds(palette, AllToolsQuery);
        Assert.DoesNotContain(auditItemId, everyTool);
        Assert.Contains(PaletteItemId(ListedEssentialsToolId), everyTool);
        Assert.Contains(PaletteItemId(DetectedToolId), everyTool);
        Assert.DoesNotContain(PaletteResultIds(palette, AuditToolQuery), id => id.StartsWith(auditItemId, StringComparison.Ordinal));
        Assert.DoesNotContain(PaletteResultIds(palette, AuditToolArgumentQuery), id => id.StartsWith(auditItemId, StringComparison.Ordinal));
    }

    [Fact]
    public void ThePalette_DoesNotParseAnArgumentForADisabledToolAndDoesAgainOnceEnabled()
    {
        ToolRegistry registry = new();
        ListingSettingsSource source = new();
        ToolAvailability availability = CreateRealAvailability(registry, source);
        using TestHarness harness = TestHarness.Create(toolAvailability: availability);
        CommandPaletteViewModel palette = harness.Main.CommandPalette;
        string withArgumentPrefix = PaletteItemId(ListedNetworkToolId) + PaletteArgumentSeparator;

        Assert.Contains(PaletteResultIds(palette, NetworkToolArgumentQuery), id => id.StartsWith(withArgumentPrefix, StringComparison.Ordinal));

        source.Publish(nameof(ToolPack.NetworkDiagnostics));
        Assert.DoesNotContain(PaletteResultIds(palette, NetworkToolArgumentQuery), id => id.StartsWith(PaletteItemId(ListedNetworkToolId), StringComparison.Ordinal));

        source.Publish();
        Assert.Contains(PaletteResultIds(palette, NetworkToolArgumentQuery), id => id.StartsWith(withArgumentPrefix, StringComparison.Ordinal));
    }

    [Fact]
    public void ThePalettesRecentToolRows_OmitADisabledTool()
    {
        ToolRegistry registry = new();
        ListingSettingsSource source = new();
        ToolAvailability availability = CreateRealAvailability(registry, source);
        using TestHarness harness = TestHarness.Create(toolAvailability: availability);
        harness.Main.TrackRecentTool(AuditToolId);
        harness.Main.TrackRecentTool(ListedEssentialsToolId);
        CommandPaletteViewModel palette = harness.Main.CommandPalette;

        Assert.Contains(PaletteItemId(AuditToolId), PaletteResultIds(palette, string.Empty));

        source.Publish(nameof(ToolPack.Audit));
        List<string> afterDisable = PaletteResultIds(palette, string.Empty);
        Assert.DoesNotContain(PaletteItemId(AuditToolId), afterDisable);
        Assert.Contains(PaletteItemId(ListedEssentialsToolId), afterDisable);
    }
}
