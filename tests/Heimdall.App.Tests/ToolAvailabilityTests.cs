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
using Heimdall.App.Services;
using Heimdall.Core.Configuration;
using Heimdall.Core.Models;
using Heimdall.Core.Security;

namespace Heimdall.App.Tests;

/// <summary>
/// The availability decision: registry pack, user setting and organisation policy combined, and
/// the change notification that tells listings when to rebuild.
/// </summary>
public sealed class ToolAvailabilityTests
{
    // One real tool per optional pack, so the tests name tools the way a user would meet them.
    private const string EssentialsTool = "HASH";
    private const string NetworkTool = "PING";
    private const string AdministrationTool = "HOSTS";
    private const string AuditTool = "PORTSCAN";
    private const string CoreTool = "CMDLIB";

    private sealed class SettingsSource
    {
        public AppSettings? Current { get; set; }

        public Action<AppSettings>? Handler { get; private set; }

        public void Subscribe(Action<AppSettings> handler) => Handler += handler;

        public void Publish(AppSettings settings)
        {
            Current = settings;
            Handler?.Invoke(settings);
        }
    }

    private static ToolPackPolicy NoPolicy() => new(_ => null);

    private static ToolPackPolicy Forbidding(params ToolPack[] packs)
    {
        HashSet<string> names = packs.Select(ToolPackPolicy.ValueNameFor).ToHashSet(StringComparer.Ordinal);
        return new ToolPackPolicy(name => names.Contains(name) ? ToolPackPolicy.ForbiddenValue : ToolPackPolicy.NoRestrictionValue);
    }

    private static (ToolAvailability Availability, SettingsSource Source) Create(
        ToolPackPolicy? policy = null,
        AppSettings? settings = null,
        ToolRegistry? registry = null)
    {
        SettingsSource source = new() { Current = settings };
        ToolAvailability availability = new(
            registry ?? new ToolRegistry(),
            policy ?? NoPolicy(),
            () => source.Current,
            source.Subscribe);
        return (availability, source);
    }

    private static AppSettings Disabling(params string[] names) => new() { DisabledToolPacks = [.. names] };

    [Fact]
    public void WithNothingDisabledEveryBuiltInToolIsAvailable()
    {
        ToolRegistry registry = new();
        (ToolAvailability availability, _) = Create(settings: new AppSettings(), registry: registry);

        Assert.All(registry.All, descriptor => Assert.Equal(ToolAvailabilityState.Available, availability.GetState(descriptor.Id)));
    }

    [Fact]
    public void BeforeTheFirstSettingsLoadNothingIsDisabled()
    {
        (ToolAvailability availability, _) = Create(settings: null);

        Assert.Equal(ToolAvailabilityState.Available, availability.GetState(AuditTool));
        Assert.True(availability.IsPackEnabled(ToolPack.Audit));
    }

    [Fact]
    public void AnIdNotInTheRegistryIsUnknown()
    {
        (ToolAvailability availability, _) = Create(settings: new AppSettings());

        Assert.Equal(ToolAvailabilityState.Unknown, availability.GetState("NOSUCHTOOL"));
        Assert.Equal(ToolAvailabilityState.Unknown, availability.GetState(string.Empty));
    }

    [Fact]
    public void AToolIdWithItsConnectionTypePrefixIsResolved()
    {
        (ToolAvailability availability, _) = Create(settings: Disabling(nameof(ToolPack.Audit)));

        Assert.Equal(ToolAvailabilityState.DisabledByUser, availability.GetState("TOOL:" + AuditTool));
        Assert.Equal(ToolAvailabilityState.Available, availability.GetState("TOOL:" + EssentialsTool));
    }

    [Fact]
    public void AKnownToolWithNoPackIsAvailableWhateverIsDisabledOrForbidden()
    {
        ToolRegistry registry = new();
        registry.RegisterToolForTests(ToolRegistryPackTests.Probe("PROBENOPACK", pack: null), () => throw new InvalidOperationException("never built"));
        (ToolAvailability availability, _) = Create(
            policy: Forbidding(ToolPack.Essentials, ToolPack.NetworkDiagnostics, ToolPack.Administration, ToolPack.Audit),
            settings: Disabling(
                nameof(ToolPack.Essentials),
                nameof(ToolPack.NetworkDiagnostics),
                nameof(ToolPack.Administration),
                nameof(ToolPack.Audit)),
            registry: registry);

        Assert.Equal(ToolAvailabilityState.Available, availability.GetState("PROBENOPACK"));
    }

    [Fact]
    public void AToolInADisabledPackIsDisabledByTheUserAndOthersAreUntouched()
    {
        (ToolAvailability availability, _) = Create(settings: Disabling(nameof(ToolPack.Audit)));

        Assert.Equal(ToolAvailabilityState.DisabledByUser, availability.GetState(AuditTool));
        Assert.Equal(ToolAvailabilityState.Available, availability.GetState(EssentialsTool));
        Assert.Equal(ToolAvailabilityState.Available, availability.GetState(NetworkTool));
        Assert.Equal(ToolAvailabilityState.Available, availability.GetState(AdministrationTool));
        Assert.Equal(ToolAvailabilityState.Available, availability.GetState(CoreTool));
        Assert.False(availability.IsPackEnabled(ToolPack.Audit));
        Assert.True(availability.IsPackEnabled(ToolPack.Essentials));
    }

    [Theory]
    [InlineData("audit")]
    [InlineData("AUDIT")]
    [InlineData("  Audit  ")]
    public void ThePackNameInTheSettingsIsReadWithoutRegardToCaseOrPadding(string name)
    {
        (ToolAvailability availability, _) = Create(settings: Disabling(name));

        Assert.Equal(ToolAvailabilityState.DisabledByUser, availability.GetState(AuditTool));
    }

    [Fact]
    public void TheCorePackCannotBeDisabledBySettings()
    {
        (ToolAvailability availability, _) = Create(settings: Disabling(nameof(ToolPack.Core), "core", "CORE"));

        Assert.Equal(ToolAvailabilityState.Available, availability.GetState(CoreTool));
        Assert.Equal(ToolAvailabilityState.Available, availability.GetState("NOTES"));
        Assert.True(availability.IsPackEnabled(ToolPack.Core));
    }

    [Fact]
    public void TheCorePackCannotBeForbiddenByPolicy()
    {
        ToolPackPolicy everythingForbidden = new(_ => ToolPackPolicy.ForbiddenValue);
        (ToolAvailability availability, _) = Create(policy: everythingForbidden, settings: new AppSettings());

        Assert.Equal(ToolAvailabilityState.Available, availability.GetState(CoreTool));
        Assert.Equal(ToolAvailabilityState.Available, availability.GetState("NOTES"));
        Assert.True(availability.IsPackEnabled(ToolPack.Core));
        Assert.False(availability.IsPackForbiddenByPolicy(ToolPack.Core));
    }

    [Fact]
    public void UnknownPackNamesInTheSettingsAreIgnored()
    {
        ToolRegistry registry = new();
        (ToolAvailability availability, _) = Create(
            settings: Disabling("Nonsense", string.Empty, "  ", "Essentials2", "1", "2", "4", "-1", null!),
            registry: registry);

        Assert.All(registry.All, descriptor => Assert.Equal(ToolAvailabilityState.Available, availability.GetState(descriptor.Id)));
        Assert.True(availability.IsPackEnabled(ToolPack.Essentials));
        Assert.True(availability.IsPackEnabled(ToolPack.Audit));
    }

    [Fact]
    public void ThePolicyBeatsTheUsersSetting()
    {
        // The user left the pack on, and the policy still wins.
        (ToolAvailability userOn, _) = Create(policy: Forbidding(ToolPack.Audit), settings: new AppSettings());
        // The user turned it off too, and the reason reported is still the policy.
        (ToolAvailability userOff, _) = Create(policy: Forbidding(ToolPack.Audit), settings: Disabling(nameof(ToolPack.Audit)));

        foreach (ToolAvailability availability in new[] { userOn, userOff })
        {
            Assert.Equal(ToolAvailabilityState.ForbiddenByPolicy, availability.GetState(AuditTool));
            Assert.True(availability.IsPackForbiddenByPolicy(ToolPack.Audit));
            Assert.False(availability.IsPackEnabled(ToolPack.Audit));
            Assert.Equal(ToolAvailabilityState.Available, availability.GetState(EssentialsTool));
            Assert.False(availability.IsPackForbiddenByPolicy(ToolPack.Essentials));
        }
    }

    [Fact]
    public void ThePolicyIsReportedEvenBeforeTheSettingsAreLoaded()
    {
        (ToolAvailability availability, _) = Create(policy: Forbidding(ToolPack.Audit), settings: null);

        Assert.Equal(ToolAvailabilityState.ForbiddenByPolicy, availability.GetState(AuditTool));
    }

    [Fact]
    public void ASettingsChangeIsSeenByTheNextQueryWithoutAnyPriorRead()
    {
        (ToolAvailability availability, SettingsSource source) = Create(settings: new AppSettings());

        source.Publish(Disabling(nameof(ToolPack.Audit)));

        Assert.Equal(ToolAvailabilityState.DisabledByUser, availability.GetState(AuditTool));
    }

    [Fact]
    public void ChangedIsRaisedWhenAPackIsSwitchedOffAndAgainWhenItComesBack()
    {
        (ToolAvailability availability, SettingsSource source) = Create(settings: new AppSettings());
        Assert.Equal(ToolAvailabilityState.Available, availability.GetState(AuditTool));
        int raised = 0;
        availability.Changed += () => raised++;

        source.Publish(Disabling(nameof(ToolPack.Audit)));
        Assert.Equal(1, raised);
        Assert.Equal(ToolAvailabilityState.DisabledByUser, availability.GetState(AuditTool));

        source.Publish(new AppSettings());
        Assert.Equal(2, raised);
        Assert.Equal(ToolAvailabilityState.Available, availability.GetState(AuditTool));
    }

    [Fact]
    public void ChangedIsRaisedByTheFirstChangeEvenWhenNoStateWasReadBefore()
    {
        (ToolAvailability availability, SettingsSource source) = Create(settings: new AppSettings());
        int raised = 0;
        availability.Changed += () => raised++;

        source.Publish(Disabling(nameof(ToolPack.Audit)));

        Assert.Equal(1, raised);
    }

    [Fact]
    public void SettingsLoadedAfterAnEarlyQueryAreNotMissed()
    {
        (ToolAvailability availability, SettingsSource source) = Create(settings: null);
        Assert.Equal(ToolAvailabilityState.Available, availability.GetState(AuditTool));

        source.Current = Disabling(nameof(ToolPack.Audit));

        Assert.Equal(ToolAvailabilityState.DisabledByUser, availability.GetState(AuditTool));
    }

    [Fact]
    public void AnOlderEventArrivingLate_DoesNotOverwriteTheNewerSnapshot()
    {
        (ToolAvailability availability, SettingsSource source) = Create(settings: new AppSettings());
        Assert.Equal(ToolAvailabilityState.Available, availability.GetState(AuditTool));
        int raised = 0;
        availability.Changed += () => raised++;

        // The settings source already holds the newer write (Audit off) when the event of the
        // older write (nothing off) is delivered.
        source.Current = Disabling(nameof(ToolPack.Audit));
        source.Handler!(new AppSettings());

        Assert.Equal(ToolAvailabilityState.DisabledByUser, availability.GetState(AuditTool));
        Assert.Equal(1, raised);

        // The newer event then arrives: nothing differs any more.
        source.Handler!(Disabling(nameof(ToolPack.Audit)));
        Assert.Equal(1, raised);
    }

    [Fact]
    public void AnEventBeforeAnySnapshotExists_IsTakenFromItsArgument()
    {
        (ToolAvailability availability, SettingsSource source) = Create(settings: null);

        source.Handler!(Disabling(nameof(ToolPack.Audit)));

        Assert.Equal(ToolAvailabilityState.DisabledByUser, availability.GetState(AuditTool));
    }

    [Fact]
    public void ChangedIsNotRaisedByAnUnrelatedSettingsWrite()
    {
        (ToolAvailability availability, SettingsSource source) = Create(settings: Disabling(nameof(ToolPack.Audit)));
        Assert.Equal(ToolAvailabilityState.DisabledByUser, availability.GetState(AuditTool));
        int raised = 0;
        availability.Changed += () => raised++;

        source.Publish(new AppSettings { DefaultTheme = "Other", DisabledToolPacks = [nameof(ToolPack.Audit)] });
        source.Publish(new AppSettings { DefaultLocale = "fr", DisabledToolPacks = [nameof(ToolPack.Audit)] });

        Assert.Equal(0, raised);
    }

    [Fact]
    public void ChangedIsNotRaisedWhenTheListChangesButTheEffectiveSetDoesNot()
    {
        (ToolAvailability availability, SettingsSource source) = Create(settings: Disabling(nameof(ToolPack.Audit)));
        Assert.False(availability.IsPackEnabled(ToolPack.Audit));
        int raised = 0;
        availability.Changed += () => raised++;

        // Spelling, order, duplicates, the permanent pack and an unknown name change the list only.
        source.Publish(Disabling("audit", nameof(ToolPack.Audit), nameof(ToolPack.Core), "Nonsense"));

        Assert.Equal(0, raised);
        Assert.False(availability.IsPackEnabled(ToolPack.Audit));
    }

    [Fact]
    public void ChangedIsNotRaisedForAPackThePolicyAlreadyForbids()
    {
        (ToolAvailability availability, SettingsSource source) = Create(policy: Forbidding(ToolPack.Audit), settings: new AppSettings());
        Assert.Equal(ToolAvailabilityState.ForbiddenByPolicy, availability.GetState(AuditTool));
        int raised = 0;
        availability.Changed += () => raised++;

        source.Publish(Disabling(nameof(ToolPack.Audit)));
        source.Publish(new AppSettings());

        Assert.Equal(0, raised);
        Assert.Equal(ToolAvailabilityState.ForbiddenByPolicy, availability.GetState(AuditTool));
    }

    [Fact]
    public void AFailingSubscriberDoesNotStopTheOthersFromBeingTold()
    {
        (ToolAvailability availability, SettingsSource source) = Create(settings: new AppSettings());
        Assert.True(availability.IsPackEnabled(ToolPack.Audit));
        int reached = 0;
        availability.Changed += () => throw new InvalidOperationException("subscriber failure for the test");
        availability.Changed += () => reached++;

        source.Publish(Disabling(nameof(ToolPack.Audit)));

        Assert.Equal(1, reached);
    }

    [Fact]
    public async Task TheServiceBuiltOnARealConfigurationManagerListensToItsSettingsChanges()
    {
        string root = Path.Combine(Path.GetTempPath(), "heimdall-tool-availability-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            ConfigManager configManager = new(root);
            ToolAvailability availability = new(new ToolRegistry(), NoPolicy(), configManager);
            int raised = 0;
            availability.Changed += () => raised++;

            await configManager.SaveSettingsAsync(Disabling(nameof(ToolPack.Audit)));

            Assert.Equal(1, raised);
            Assert.Equal(ToolAvailabilityState.DisabledByUser, availability.GetState(AuditTool));

            await configManager.SaveSettingsAsync(new AppSettings { DefaultTheme = "Other", DisabledToolPacks = [nameof(ToolPack.Audit)] });

            Assert.Equal(1, raised);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup of a temp directory.
            }
        }
    }
}
