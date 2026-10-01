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
using System.Xml.Linq;
using Heimdall.App.Tests.Views.EmbeddedRdp;
using Heimdall.App.ViewModels.Settings;
using Heimdall.Core.Configuration;

namespace Heimdall.App.Tests;

/// <summary>
/// The security posture decision: which state of each security-relevant setting is risky, and
/// where its "Go to setting" link lands.
/// </summary>
public sealed class SecurityPostureTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>Every choice at its safe value, with the vault on and an idle lock set.</summary>
    private static readonly SecurityPostureInputs Safe = new(
        RdpNla: true,
        RdpStrictServerAuthentication: true,
        TftpSharing: false,
        SessionTranscripts: false,
        PowerShellExecutionPolicy: "Default",
        VaultEnabled: true,
        AutoLockIdleMinutes: 15,
        DisconnectOnLock: true,
        RequireCredentialGuard: true,
        RequireWindowsHelloOnConnect: true,
        UpdateChecks: true,
        KnownHostsSync: false);

    /// <summary>Each risky choice, applied alone to the safe inputs, and the line that must flag it.</summary>
    public static TheoryData<string, SecurityPostureKey> RiskyChoices => new()
    {
        { "NLA off", SecurityPostureKey.RdpNla },
        { "TFTP on", SecurityPostureKey.TftpSharing },
        { "transcripts on", SecurityPostureKey.SessionTranscripts },
        { "policy Bypass", SecurityPostureKey.PowerShellExecutionPolicy },
        { "policy Unrestricted", SecurityPostureKey.PowerShellExecutionPolicy },
        { "no idle lock with the vault", SecurityPostureKey.AutoLock },
        { "update checks off", SecurityPostureKey.UpdateChecks },
        { "known_hosts sync on", SecurityPostureKey.KnownHostsSync },
    };

    [Fact]
    public void TheSafeInputsFlagNothing()
    {
        IReadOnlyList<SecurityPostureItem> items = SecurityPosture.Evaluate(Safe);

        Assert.Equal(Enum.GetValues<SecurityPostureKey>().Length, items.Count);
        Assert.Equal(0, SecurityPosture.CountRisky(items));
    }

    [Theory]
    [MemberData(nameof(RiskyChoices))]
    public void EachRiskyChoiceFlagsItsOwnLineAndNoOther(string choice, SecurityPostureKey key)
    {
        IReadOnlyList<SecurityPostureItem> items = SecurityPosture.Evaluate(Apply(choice));

        SecurityPostureItem flagged = Assert.Single(items, item => item.IsRisky);
        Assert.Equal(key, flagged.Key);
        Assert.Equal(1, SecurityPosture.CountRisky(items));
    }

    /// <summary>
    /// The choices that are hardening a user opts into are reported off without being flagged:
    /// strict server authentication off is the Windows default, which warns and asks.
    /// </summary>
    [Fact]
    public void TheOptInHardeningIsReportedOffButNotFlagged()
    {
        SecurityPostureInputs inputs = Safe with
        {
            RdpStrictServerAuthentication = false,
            DisconnectOnLock = false,
            RequireCredentialGuard = false,
            RequireWindowsHelloOnConnect = false,
            PowerShellExecutionPolicy = "RemoteSigned",
        };

        IReadOnlyList<SecurityPostureItem> items = SecurityPosture.Evaluate(inputs);

        Assert.Equal(0, SecurityPosture.CountRisky(items));
        Assert.Equal(SecurityPostureState.Off, Item(items, SecurityPostureKey.RdpStrictServerAuthentication).State);
        Assert.Equal(SecurityPostureState.NotRequired, Item(items, SecurityPostureKey.CredentialGuard).State);
        Assert.Equal("RemoteSigned", Item(items, SecurityPostureKey.PowerShellExecutionPolicy).Detail);
    }

    /// <summary>
    /// Without the vault there is nothing to lock: no idle lock is not a risk then, and the lock
    /// lines point at the control that turns the vault on, since their own boxes are disabled.
    /// </summary>
    [Fact]
    public void WithoutTheVaultTheLockLinesNeedTheVaultAndPointAtIt()
    {
        IReadOnlyList<SecurityPostureItem> items = SecurityPosture.Evaluate(
            Safe with { VaultEnabled = false, AutoLockIdleMinutes = 0 });

        SecurityPostureItem autoLock = Item(items, SecurityPostureKey.AutoLock);
        Assert.False(autoLock.IsRisky);
        Assert.Equal(SecurityPostureState.RequiresVault, autoLock.State);
        Assert.Equal("Mw_SettingsVaultEnableBtn", autoLock.TargetSettingId);
        Assert.Equal("Mw_SettingsVaultEnableBtn", Item(items, SecurityPostureKey.DisconnectOnLock).TargetSettingId);
        Assert.Equal(SecurityPostureState.Disabled, Item(items, SecurityPostureKey.Vault).State);
        Assert.Equal(0, SecurityPosture.CountRisky(items));
    }

    [Fact]
    public void AnIdleLockReportsItsMinutes()
    {
        SecurityPostureItem autoLock = Item(SecurityPosture.Evaluate(Safe), SecurityPostureKey.AutoLock);

        Assert.Equal(SecurityPostureState.AfterMinutes, autoLock.State);
        Assert.Equal("15", autoLock.Detail);
        Assert.Equal("Mw_SettingsAutoLockMinutes", autoLock.TargetSettingId);
    }

    /// <summary>The overall count adds up every flagged line.</summary>
    [Fact]
    public void TheOverallCountAddsEveryRiskyLine()
    {
        SecurityPostureInputs everything = Safe with
        {
            RdpNla = false,
            TftpSharing = true,
            SessionTranscripts = true,
            PowerShellExecutionPolicy = "Bypass",
            AutoLockIdleMinutes = 0,
            UpdateChecks = false,
            KnownHostsSync = true,
        };

        Assert.Equal(7, SecurityPosture.CountRisky(SecurityPosture.Evaluate(everything)));
    }

    /// <summary>
    /// The factory defaults flag nothing: a new install must not open on a card asking for
    /// attention it cannot explain.
    /// </summary>
    [Fact]
    public void TheFactoryDefaultsFlagNothing()
    {
        AppSettings defaults = new();
        SecurityPostureInputs inputs = new(
            defaults.RdpDefaultNla,
            defaults.RdpDefaultStrictServerAuthentication,
            defaults.FileShareEnableTftp,
            defaults.SessionLoggingEnabled,
            defaults.PowerShellExecutionPolicy,
            defaults.VaultEnabled,
            defaults.AutoLockIdleMinutes,
            defaults.DisconnectOnLock,
            defaults.RequireCredentialGuard,
            defaults.RequireWindowsHelloOnConnect,
            defaults.UpdateCheckEnabled,
            defaults.SyncKnownHostsAtStartup);

        Assert.Equal(0, SecurityPosture.CountRisky(SecurityPosture.Evaluate(inputs)));
    }

    /// <summary>
    /// Every line, in every state, names a target from the declared list, so the markup guard
    /// below covers every place a link can land.
    /// </summary>
    [Fact]
    public void EveryTargetALineCanNameIsDeclared()
    {
        HashSet<string> named = [];
        foreach (string choice in RiskyChoices.Select(row => (string)row[0]))
        {
            named.UnionWith(SecurityPosture.Evaluate(Apply(choice)).Select(item => item.TargetSettingId));
        }

        named.UnionWith(SecurityPosture.Evaluate(Safe).Select(item => item.TargetSettingId));
        named.UnionWith(SecurityPosture.Evaluate(Safe with { VaultEnabled = false }).Select(item => item.TargetSettingId));

        Assert.Subset(SecurityPosture.AllTargetSettingIds.ToHashSet(), named);
    }

    /// <summary>
    /// Every target exists on the Settings tab as a control that can be shown and focused, and
    /// every line has its row on the card.
    /// </summary>
    [Fact]
    public void EveryJumpTargetAndEveryLineExistsInTheMarkup()
    {
        List<string> problems = InspectMarkup(
            SettingsRoot(),
            SecurityPosture.AllTargetSettingIds,
            Enum.GetNames<SecurityPostureKey>());

        Assert.True(SecurityPosture.AllTargetSettingIds.Count >= 12, "the targets were not read");
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    /// <summary>The positive control: a missing target, a target that cannot take focus, and a missing row.</summary>
    [Fact]
    public void TheMarkupCheckRefusesAMissingTargetATextTargetAndAMissingRow()
    {
        XElement sample = XElement.Parse(
            """
            <StackPanel xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                        xmlns:controls="clr-namespace:Heimdall.App.Controls">
                <CheckBox x:Name="Present"/>
                <TextBlock x:Name="JustText"/>
                <controls:SecurityPostureLineView DataContext="{Binding Settings.SecurityPosture[One]}"/>
            </StackPanel>
            """);

        List<string> problems = InspectMarkup(sample, ["Present", "Missing", "JustText"], ["One", "Two"]);

        Assert.Equal(3, problems.Count);
        Assert.Contains(problems, problem => problem.StartsWith("Missing:", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.StartsWith("JustText:", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.StartsWith("Two:", StringComparison.Ordinal));
    }

    private static readonly HashSet<string> FocusableKinds = new(StringComparer.Ordinal)
    {
        "CheckBox", "ComboBox", "TextBox", "Button",
    };

    private const string LineBindingPrefix = "{Binding Settings.SecurityPosture[";

    private static List<string> InspectMarkup(XElement root, IEnumerable<string> targets, IEnumerable<string> keys)
    {
        List<string> problems = [];
        Dictionary<string, XElement> named = root.DescendantsAndSelf()
            .Where(element => element.Attribute(Xaml + "Name") is not null)
            .ToDictionary(element => element.Attribute(Xaml + "Name")!.Value, StringComparer.Ordinal);

        foreach (string target in targets)
        {
            if (!named.TryGetValue(target, out XElement? element))
            {
                problems.Add($"{target}: no element of that name on the Settings tab");
            }
            else if (!FocusableKinds.Contains(element.Name.LocalName))
            {
                problems.Add($"{target}: a {element.Name.LocalName} cannot take focus");
            }
        }

        HashSet<string> rows = root.Descendants()
            .Where(element => element.Name.LocalName == "SecurityPostureLineView")
            .Select(element => element.Attribute("DataContext")?.Value ?? string.Empty)
            .Where(value => value.StartsWith(LineBindingPrefix, StringComparison.Ordinal))
            .Select(value => value[LineBindingPrefix.Length..].TrimEnd('}').TrimEnd(']'))
            .ToHashSet(StringComparer.Ordinal);

        foreach (string key in keys.Where(key => !rows.Contains(key)))
        {
            problems.Add($"{key}: no row on the card");
        }

        return problems;
    }

    private static SecurityPostureInputs Apply(string choice) => choice switch
    {
        "NLA off" => Safe with { RdpNla = false },
        "TFTP on" => Safe with { TftpSharing = true },
        "transcripts on" => Safe with { SessionTranscripts = true },
        "policy Bypass" => Safe with { PowerShellExecutionPolicy = "Bypass" },
        "policy Unrestricted" => Safe with { PowerShellExecutionPolicy = "Unrestricted" },
        "no idle lock with the vault" => Safe with { AutoLockIdleMinutes = 0 },
        "update checks off" => Safe with { UpdateChecks = false },
        "known_hosts sync on" => Safe with { KnownHostsSync = true },
        _ => throw new ArgumentOutOfRangeException(nameof(choice), choice, null),
    };

    private static SecurityPostureItem Item(IEnumerable<SecurityPostureItem> items, SecurityPostureKey key)
        => items.Single(item => item.Key == key);

    private static XElement SettingsRoot()
    {
        string path = Path.Combine(ViewSource.RepoRoot(), "src", "Heimdall.App", "MainWindow.xaml");
        Assert.True(File.Exists(path), $"View not found: {path}");
        return XDocument.Load(path)
            .Descendants()
            .Single(element => element.Attribute(Xaml + "Name")?.Value == "Mw_SettingsRoot");
    }
}
