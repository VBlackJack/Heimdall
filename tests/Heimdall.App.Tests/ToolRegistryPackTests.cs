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

using Heimdall.App.Services;
using Heimdall.Core.Configuration;
using Heimdall.Core.Models;

namespace Heimdall.App.Tests;

/// <summary>
/// Which pack each built-in tool belongs to. The id sets are written out in full on purpose: a
/// tool that moves from one pack to another must change this file in the same commit.
/// </summary>
public sealed class ToolRegistryPackTests
{
    private static readonly Dictionary<ToolPack, string[]> ExpectedIds = new()
    {
        [ToolPack.Core] = ["CMDLIB", "NOTES"],
        [ToolPack.Essentials] =
        [
            "BASE64", "URLENC", "JSON", "REGEX", "DIFF", "TEXTCASE", "CHMOD", "DATETIME", "UUID", "ULID",
            "CRONTAB", "SSHCONFIG", "SUBNET", "IPCONV", "HTTP", "NETCALC", "HASH", "HMAC", "PASSWORD",
            "SSHKEY", "CERTGEN", "JWT", "TOTP", "PWDAUDIT", "SSHAUDIT", "CVELOOKUP",
        ],
        [ToolPack.NetworkDiagnostics] =
        [
            "PING", "TCPPING", "DNS", "DNSBATCH", "CERT", "WHOIS", "HTTPHEADERS", "TCPTRACE", "NETIF",
            "ROUTES", "OPENPORTS", "WIFI", "WOL", "ARPMON", "DNSSEC",
        ],
        [ToolPack.Administration] = ["LOGVIEW", "HOSTS", "CRONJOB", "SERVICES", "DIAGRAM"],
        [ToolPack.Audit] =
        [
            "PORTSCAN", "BANNER", "SNMPWALK", "FWTEST", "NETMAP", "SSHSERVERAUDIT", "TLSAUDIT", "SMBENUM", "DEFAULTCREDS",
            "SECNUMCLOUD", "PRIVLAUNCH",
        ],
        [ToolPack.Recipes] = [],
    };

    private static string[] IdsOf(ToolRegistry registry, ToolPack pack)
        => registry.All
            .Where(descriptor => descriptor.Pack == pack)
            .Select(descriptor => descriptor.Id)
            .Order(StringComparer.Ordinal)
            .ToArray();

    [Fact]
    public void EveryBuiltInToolNamesItsPack()
    {
        ToolRegistry registry = new();

        Assert.Equal(59, registry.All.Count);
        Assert.All(registry.All, descriptor => Assert.NotNull(descriptor.Pack));
    }

    [Theory]
    [InlineData(ToolPack.Core, 2)]
    [InlineData(ToolPack.Essentials, 26)]
    [InlineData(ToolPack.NetworkDiagnostics, 15)]
    [InlineData(ToolPack.Administration, 5)]
    [InlineData(ToolPack.Audit, 11)]
    public void EachPackHasTheAgreedNumberOfTools(ToolPack pack, int expectedCount)
        => Assert.Equal(expectedCount, IdsOf(new ToolRegistry(), pack).Length);

    [Theory]
    [InlineData(ToolPack.Core)]
    [InlineData(ToolPack.Essentials)]
    [InlineData(ToolPack.NetworkDiagnostics)]
    [InlineData(ToolPack.Administration)]
    [InlineData(ToolPack.Audit)]
    public void EachPackHoldsExactlyTheAgreedTools(ToolPack pack)
        => Assert.Equal(
            ExpectedIds[pack].Order(StringComparer.Ordinal).ToArray(),
            IdsOf(new ToolRegistry(), pack));

    [Fact]
    public void TheExpectedSetsCoverEveryPackOfTheEnum()
        => Assert.Equal(
            Enum.GetValues<ToolPack>().Order(),
            ExpectedIds.Keys.Order());

    [Fact]
    public void ADetectedExternalToolHasNoPack()
    {
        ToolRegistry registry = new();
        registry.RegisterExternalTools(
        [
            new ExternalToolInfo
            {
                Id = "PSEXEC",
                Name = "PsExec",
                ExecutablePath = @"C:\Tools\psexec.exe",
                ProviderName = "Sysinternals",
            },
        ]);

        ToolDescriptor external = Assert.Single(registry.All, descriptor => descriptor.ExternalProviderName is not null);

        Assert.Null(external.Pack);
    }

    [Fact]
    public void ATestProbeKeepsTheDescriptorItWasGiven()
    {
        ToolRegistry registry = new();
        ToolDescriptor noPack = Probe("PROBENOPACK", pack: null);
        ToolDescriptor inAudit = Probe("PROBEAUDIT", pack: ToolPack.Audit);

        registry.RegisterToolForTests(noPack, () => throw new InvalidOperationException("never built"));
        registry.RegisterToolForTests(inAudit, () => throw new InvalidOperationException("never built"));

        Assert.Null(registry.GetById("PROBENOPACK")!.Pack);
        Assert.Equal(ToolPack.Audit, registry.GetById("PROBEAUDIT")!.Pack);
    }

    internal static ToolDescriptor Probe(string id, ToolPack? pack)
        => new(
            Id: id,
            Category: ToolCategory.System,
            CategoryLabelKey: "ToolCategorySystem",
            LabelKey: id,
            LabelWithArgKey: null,
            CommandPrefixes: [id.ToLowerInvariant()],
            IsNetworkTool: false,
            Pack: pack);
}
