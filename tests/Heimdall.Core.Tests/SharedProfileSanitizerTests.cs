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
using System.Text.Json;
using Heimdall.Core.Configuration;
using Heimdall.Core.Models;

namespace Heimdall.Core.Tests;

/// <summary>
/// The allowlist of what a shared session file may decide, held by reflection over every public
/// settable property of the DTOs.
/// </summary>
/// <remarks>
/// <para>Every property is set to a value that is not its default, the object is sanitized, and
/// every property outside the allowlist must come back at the default of a new object. A property
/// added to a DTO is in neither list below, so <see cref="EveryProfileProperty_IsDecided"/> fails
/// until someone decides whether a shared file may set it.</para>
/// </remarks>
public sealed class SharedProfileSanitizerTests
{
    private static readonly string SharedProfileId = SharedSessionIds.PrefixFor("teamfile1") + "profile";
    private static readonly string SharedGatewayId = SharedSessionIds.PrefixFor("teamfile1") + "gateway";

    /// <summary>
    /// Every <see cref="ServerProfileDto"/> property a shared file may NOT set, decided one by one:
    /// local execution, external clients, secrets, key paths, forwarding, local tunnel ports,
    /// display and redirection preferences, other protocols, and the execution trust flag.
    /// </summary>
    private static readonly string[] ResetProfileProperties =
    [
        nameof(ServerProfileDto.ExtensionData), nameof(ServerProfileDto.LocalPort),
        nameof(ServerProfileDto.RdpPasswordEncrypted), nameof(ServerProfileDto.RdpDomain),
        nameof(ServerProfileDto.ProjectId), nameof(ServerProfileDto.SessionLoggingOverride),
        nameof(ServerProfileDto.VaultEntryName), nameof(ServerProfileDto.WinRmPort),
        nameof(ServerProfileDto.AllowCredentialPrompt), nameof(ServerProfileDto.WinRmUsername),
        nameof(ServerProfileDto.WinRmPasswordEncrypted), nameof(ServerProfileDto.WinRmUseSsl),
        nameof(ServerProfileDto.WinRmSkipCertificateCheck), nameof(ServerProfileDto.WinRmIdentityMode),
        nameof(ServerProfileDto.SshMode), nameof(ServerProfileDto.SshAgentForwarding),
        nameof(ServerProfileDto.SshKeyPath), nameof(ServerProfileDto.SshPasswordEncrypted),
        nameof(ServerProfileDto.SshKeyPassphraseEncrypted), nameof(ServerProfileDto.SshCompression),
        nameof(ServerProfileDto.SshX11Forwarding), nameof(ServerProfileDto.SocksProxyPort),
        nameof(ServerProfileDto.RemoteBindPort), nameof(ServerProfileDto.RemoteLocalPort),
        nameof(ServerProfileDto.PostConnectCommand), nameof(ServerProfileDto.PostConnectDelayMs),
        nameof(ServerProfileDto.RdpAntiIdle), nameof(ServerProfileDto.RdpAspectRatio),
        // Obsolete setter-only migration shims: named as text, and checked through the
        // RdpFixedWidth and RdpFixedHeight they write to.
        "RdpDefaultResolutionWidth", "RdpDefaultResolutionHeight",
        nameof(ServerProfileDto.RdpResolutionMode), nameof(ServerProfileDto.RdpFixedWidth),
        nameof(ServerProfileDto.RdpFixedHeight), nameof(ServerProfileDto.RdpInitialSmartSizing),
        nameof(ServerProfileDto.RdpResizeEnableDelayMs), nameof(ServerProfileDto.TunnelsPanelExpanded),
        nameof(ServerProfileDto.IsFavorite), nameof(ServerProfileDto.SortOrder),
        nameof(ServerProfileDto.RdpMode), nameof(ServerProfileDto.RdpUseGlobalDefaults),
        nameof(ServerProfileDto.RdpRedirectClipboard), nameof(ServerProfileDto.RdpRedirectDrives),
        nameof(ServerProfileDto.RdpRedirectPrinters), nameof(ServerProfileDto.RdpRedirectComPorts),
        nameof(ServerProfileDto.RdpRedirectSmartCards), nameof(ServerProfileDto.RdpRedirectWebcam),
        nameof(ServerProfileDto.RdpRedirectUsb), nameof(ServerProfileDto.RdpAudioMode),
        nameof(ServerProfileDto.RdpAudioCapture), nameof(ServerProfileDto.RdpMultiMonitor),
        nameof(ServerProfileDto.RdpSelectedMonitorIndices), nameof(ServerProfileDto.RdpDynamicResolution),
        nameof(ServerProfileDto.RdpNla), nameof(ServerProfileDto.RdpStrictServerAuthentication),
        nameof(ServerProfileDto.RdpColorDepth), nameof(ServerProfileDto.RdpBitmapCaching),
        nameof(ServerProfileDto.RdpCompression), nameof(ServerProfileDto.RdpHardwareAcceleration),
        nameof(ServerProfileDto.RdpAutoReconnect), nameof(ServerProfileDto.RdpAdminMode),
        nameof(ServerProfileDto.RdpFullScreen), nameof(ServerProfileDto.RdpPerformanceFlags),
        nameof(ServerProfileDto.RdpDisableUdp), nameof(ServerProfileDto.RdpGateway),
        nameof(ServerProfileDto.Environment), nameof(ServerProfileDto.MacAddress),
        nameof(ServerProfileDto.LocalShellExecutable), nameof(ServerProfileDto.LocalShellArguments),
        nameof(ServerProfileDto.LocalShellWorkingDirectory), nameof(ServerProfileDto.LocalShellElevated),
        nameof(ServerProfileDto.ElevationMode), nameof(ServerProfileDto.ExecutionConfirmed),
        nameof(ServerProfileDto.CitrixStoreFrontUrl), nameof(ServerProfileDto.CitrixAppName),
        nameof(ServerProfileDto.CitrixIcaFilePath), nameof(ServerProfileDto.CitrixSeamlessMode),
        nameof(ServerProfileDto.CitrixUseSso), nameof(ServerProfileDto.CitrixLaunchCommandLine),
        nameof(ServerProfileDto.FtpPort), nameof(ServerProfileDto.FtpUsername),
        nameof(ServerProfileDto.FtpPasswordEncrypted), nameof(ServerProfileDto.VncPort),
        nameof(ServerProfileDto.VncPassword), nameof(ServerProfileDto.FtpPassiveMode),
        nameof(ServerProfileDto.FtpUseSsl), nameof(ServerProfileDto.VncViewOnly),
        nameof(ServerProfileDto.TelnetPort), nameof(ServerProfileDto.TelnetUsername),
        nameof(ServerProfileDto.TelnetPasswordEncrypted), nameof(ServerProfileDto.WebPort),
        nameof(ServerProfileDto.WebUseHttp), nameof(ServerProfileDto.WebPath),
        nameof(ServerProfileDto.WebUsername), nameof(ServerProfileDto.WebPasswordEncrypted),
        nameof(ServerProfileDto.WebOpenExternally), nameof(ServerProfileDto.WebExternalProgram),
        nameof(ServerProfileDto.WebExternalArguments),
    ];

    /// <summary>Every <see cref="SshGatewayDto"/> property a shared file may NOT set.</summary>
    private static readonly string[] ResetGatewayProperties =
    [
        nameof(SshGatewayDto.ExtensionData), nameof(SshGatewayDto.KeyPath),
        nameof(SshGatewayDto.SshPasswordEncrypted), nameof(SshGatewayDto.SshKeyPassphraseEncrypted),
        nameof(SshGatewayDto.IsDefault), nameof(SshGatewayDto.HostKeyFingerprint),
    ];

    [Fact]
    public void EveryProfileProperty_IsDecided()
    {
        AssertEveryPropertyDecided(typeof(ServerProfileDto), SharedProfileSanitizer.KeptProfileProperties, ResetProfileProperties);
    }

    [Fact]
    public void EveryGatewayProperty_IsDecided()
    {
        AssertEveryPropertyDecided(typeof(SshGatewayDto), SharedProfileSanitizer.KeptGatewayProperties, ResetGatewayProperties);
    }

    [Fact]
    public void Sanitize_Profile_ResetsEveryPropertyOutsideTheAllowlist()
    {
        ServerProfileDto poisoned = Poison<ServerProfileDto>();
        poisoned.Id = SharedProfileId;
        poisoned.SshGatewayId = SharedGatewayId;
        poisoned.ConnectionType = "SSH";

        ServerProfileDto sanitized = Assert.IsType<ServerProfileDto>(SharedProfileSanitizer.Sanitize(poisoned));

        AssertOnlyAllowlistSurvives(sanitized, new ServerProfileDto(), SharedProfileSanitizer.KeptProfileProperties);
        Assert.False(sanitized.HasWinRmPortField);
        Assert.False(sanitized.HasSshKeyPassphraseEncryptedField);
        Assert.False(sanitized.HasRdpResolutionModeField);
        Assert.False(sanitized.UsesLegacySshCredentialMapping);
    }

    [Fact]
    public void Sanitize_Profile_KeepsTheAllowlistedValues()
    {
        ServerProfileDto poisoned = Poison<ServerProfileDto>();
        poisoned.Id = SharedProfileId;
        poisoned.SshGatewayId = SharedGatewayId;
        poisoned.ConnectionType = "ssh";

        ServerProfileDto sanitized = SharedProfileSanitizer.Sanitize(poisoned)!;

        Assert.Equal(SharedProfileId, sanitized.Id);
        Assert.Equal(poisoned.DisplayName, sanitized.DisplayName);
        Assert.Equal(poisoned.Group, sanitized.Group);
        Assert.Equal("SSH", sanitized.ConnectionType);
        Assert.Equal(poisoned.RemoteServer, sanitized.RemoteServer);
        Assert.Equal(poisoned.RemotePort, sanitized.RemotePort);
        Assert.Equal(poisoned.SshPort, sanitized.SshPort);
        Assert.Equal(poisoned.SshUsername, sanitized.SshUsername);
        Assert.Equal(poisoned.RdpUsername, sanitized.RdpUsername);
        Assert.Equal(SharedGatewayId, sanitized.SshGatewayId);
        Assert.Equal(poisoned.UseDirectConnection, sanitized.UseDirectConnection);
        Assert.Equal(poisoned.Tags, sanitized.Tags);
        Assert.Equal(poisoned.Origin, sanitized.Origin);
        Assert.Equal(poisoned.PostConnectSteps.Select(step => step.Input), sanitized.PostConnectSteps.Select(step => step.Input));
    }

    [Fact]
    public void Sanitize_PostConnectSteps_DropTheLocalCommandLibraryReference()
    {
        ServerProfileDto poisoned = Poison<ServerProfileDto>();
        poisoned.Id = SharedProfileId;
        poisoned.SshGatewayId = null;
        poisoned.ConnectionType = "SSH";
        poisoned.PostConnectSteps =
        [
            new PostConnectStep { Input = "whoami", CommandLibraryId = "local-entry", CommandLibraryParams = new() { ["x"] = "y" } },
            new PostConnectStep { Input = "   ", CommandLibraryId = "library-only" },
        ];

        PostConnectStep step = Assert.Single(SharedProfileSanitizer.Sanitize(poisoned)!.PostConnectSteps);
        Assert.Equal("whoami", step.Input);
        Assert.Null(step.CommandLibraryId);
        Assert.Null(step.CommandLibraryParams);
    }

    [Theory]
    [InlineData("local-profile", "SSH", null, SharedProfileRefusal.ForeignId)]
    [InlineData("SHARED", "SSH", null, SharedProfileRefusal.ForeignId)]
    [InlineData(null, "LOCAL", null, SharedProfileRefusal.UnsupportedProtocol)]
    [InlineData(null, "WEB", null, SharedProfileRefusal.UnsupportedProtocol)]
    [InlineData(null, "SFTP", null, SharedProfileRefusal.UnsupportedProtocol)]
    [InlineData(null, "SSH", "local-gateway", SharedProfileRefusal.ForeignGateway)]
    public void Sanitize_Profile_RefusesAProfileThatCannotBeShared_AndSaysWhy(
        string? id,
        string connectionType,
        string? gatewayId,
        SharedProfileRefusal expected)
    {
        var source = new ServerProfileDto
        {
            Id = id ?? SharedProfileId,
            ConnectionType = connectionType,
            RemoteServer = "srv-x.example.test",
            SshGatewayId = gatewayId,
        };

        Assert.Null(SharedProfileSanitizer.Sanitize(source));
        Assert.Equal(expected, SharedProfileSanitizer.RefusalOf(source));
    }

    // A shared file is read without review: a host the inventory would refuse is refused here
    // too. A leading dash would pass for an option of Plink or of an external tool, and a space
    // would split it into two arguments.
    [Theory]
    [InlineData("-proxycmd=calc.exe")]
    [InlineData("srv-a01.example.test -oProxyCommand=calc")]
    [InlineData("srv a01")]
    [InlineData("")]
    public void Sanitize_Profile_RefusesAHostTheInventoryWouldRefuse(string host)
    {
        var source = new ServerProfileDto { Id = SharedProfileId, ConnectionType = "SSH", RemoteServer = host };

        Assert.Null(SharedProfileSanitizer.Sanitize(source));
        Assert.Equal(SharedProfileRefusal.InvalidHost, SharedProfileSanitizer.RefusalOf(source));
    }

    [Theory]
    [InlineData("srv-a01")]
    [InlineData("srv-a01.example.test")]
    [InlineData("192.0.2.10")]
    [InlineData("2001:db8::10")]
    public void Sanitize_Profile_KeepsAHostnameOrAnAddress(string host)
    {
        var source = new ServerProfileDto { Id = SharedProfileId, ConnectionType = "SSH", RemoteServer = host };

        Assert.Equal(host, SharedProfileSanitizer.Sanitize(source)?.RemoteServer);
        Assert.Null(SharedProfileSanitizer.RefusalOf(source));
    }

    [Theory]
    [InlineData("-proxycmd=calc.exe", false)]
    [InlineData("gw one.example.test", false)]
    [InlineData("gw1.example.test", true)]
    public void Sanitize_Gateway_RefusesAHostTheInventoryWouldRefuse(string host, bool kept)
    {
        SshGatewayDto? sanitized = SharedProfileSanitizer.Sanitize(new SshGatewayDto { Id = SharedGatewayId, Host = host });

        Assert.Equal(kept, sanitized is not null);
    }

    [Fact]
    public void Sanitize_Gateway_ResetsEveryPropertyOutsideTheAllowlist()
    {
        SshGatewayDto poisoned = Poison<SshGatewayDto>();
        poisoned.Id = SharedGatewayId;
        poisoned.ParentGatewayId = SharedSessionIds.PrefixFor("teamfile1") + "parent";

        SshGatewayDto sanitized = Assert.IsType<SshGatewayDto>(SharedProfileSanitizer.Sanitize(poisoned));

        AssertOnlyAllowlistSurvives(sanitized, new SshGatewayDto(), SharedProfileSanitizer.KeptGatewayProperties);
        Assert.False(sanitized.HasSshKeyPassphraseEncryptedField);
        Assert.Equal(poisoned.Host, sanitized.Host);
        Assert.Equal(poisoned.ParentGatewayId, sanitized.ParentGatewayId);
    }

    [Fact]
    public void Sanitize_Gateway_RefusesALocalIdentifier_AndDropsALocalParent()
    {
        Assert.Null(SharedProfileSanitizer.Sanitize(new SshGatewayDto { Id = "local-gateway", Host = "gw.example.test" }));

        SshGatewayDto sanitized = SharedProfileSanitizer.Sanitize(new SshGatewayDto
        {
            Id = SharedGatewayId,
            Host = "gw.example.test",
            ParentGatewayId = "local-gateway",
        })!;
        Assert.Null(sanitized.ParentGatewayId);
    }

    private static void AssertEveryPropertyDecided(Type type, IReadOnlySet<string> kept, string[] reset)
    {
        HashSet<string> settable = SettableProperties(type).Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
        HashSet<string> decided = [.. kept, .. reset];

        Assert.Empty(kept.Intersect(reset));
        List<string> undecided = settable.Except(decided).Order(StringComparer.Ordinal).ToList();
        List<string> stale = decided.Except(settable).Order(StringComparer.Ordinal).ToList();
        Assert.True(
            undecided.Count == 0,
            $"{type.Name} has properties nobody decided a shared file may or may not set: "
                + $"{string.Join(", ", undecided)}. Add each to the sanitizer's allowlist or to the reset list here.");
        Assert.True(stale.Count == 0, $"Decided properties that no longer exist on {type.Name}: {string.Join(", ", stale)}");
    }

    private static void AssertOnlyAllowlistSurvives<T>(T sanitized, T fresh, IReadOnlySet<string> kept)
    {
        List<string> survivors = [];
        foreach (PropertyInfo property in SettableProperties(typeof(T)))
        {
            // A setter-only shim cannot be read back; what it sets is a readable property checked here.
            if (kept.Contains(property.Name) || property.GetMethod is not { IsPublic: true })
            {
                continue;
            }

            if (!string.Equals(Json(property.GetValue(sanitized)), Json(property.GetValue(fresh)), StringComparison.Ordinal))
            {
                survivors.Add(property.Name);
            }
        }

        Assert.True(survivors.Count == 0, $"Survived sanitizing with a non-default value: {string.Join(", ", survivors)}");
    }

    private static T Poison<T>()
        where T : new()
    {
        T target = new();
        foreach (PropertyInfo property in SettableProperties(typeof(T)))
        {
            object? current = property.GetMethod is { IsPublic: true } ? property.GetValue(target) : null;
            property.SetValue(target, NonDefaultValue(property, current));
        }

        return target;
    }

    /// <summary>A value that is not <paramref name="current"/>, for every property type the DTOs use.</summary>
    /// <remarks>
    /// An unknown type throws: a property of a new type must be taught here, or it would be set to
    /// its default and pass the reset check without ever having been poisoned.
    /// </remarks>
    private static object? NonDefaultValue(PropertyInfo property, object? current)
    {
        Type type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        if (type == typeof(string))
        {
            return "poison-" + property.Name;
        }

        if (type == typeof(bool))
        {
            return current is not true;
        }

        if (type == typeof(int))
        {
            return (current as int? ?? 0) + 7;
        }

        if (type.IsEnum)
        {
            return Enum.GetValues(type).Cast<object>().First(value => !value.Equals(current));
        }

        if (type == typeof(int[]))
        {
            return new[] { 3 };
        }

        if (type == typeof(List<PostConnectStep>))
        {
            return new List<PostConnectStep> { new() { Input = "poison-step", CommandLibraryId = "poison-library" } };
        }

        if (type == typeof(Dictionary<string, JsonElement>))
        {
            return new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["poison"] = JsonDocument.Parse("\"value\"").RootElement.Clone(),
            };
        }

        throw new InvalidOperationException($"Teach the poisoner the type of {property.Name}: {property.PropertyType}.");
    }

    private static IEnumerable<PropertyInfo> SettableProperties(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.SetMethod is { IsPublic: true } && property.GetIndexParameters().Length == 0);

    private static string Json(object? value) => JsonSerializer.Serialize(value);
}
