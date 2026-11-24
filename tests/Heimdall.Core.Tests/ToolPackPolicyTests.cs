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

using Heimdall.Core.Models;
using Heimdall.Core.Security;

namespace Heimdall.Core.Tests;

/// <summary>
/// The organisation veto on optional tool packs, tested at the reader seam: what each raw value
/// means, which pack it applies to, and the guarantee that the permanent set is never touched.
/// </summary>
public sealed class ToolPackPolicyTests
{
    private static readonly ToolPack[] OptionalPacks =
    [
        ToolPack.Essentials,
        ToolPack.NetworkDiagnostics,
        ToolPack.Administration,
        ToolPack.Audit,
        ToolPack.Recipes,
    ];

    [Theory]
    [InlineData(ToolPack.Essentials, "ToolPackEssentials")]
    [InlineData(ToolPack.NetworkDiagnostics, "ToolPackNetworkDiagnostics")]
    [InlineData(ToolPack.Administration, "ToolPackAdministration")]
    [InlineData(ToolPack.Audit, "ToolPackAudit")]
    public void EachOptionalPackHasItsOwnValueName(ToolPack pack, string expected)
        => Assert.Equal(expected, ToolPackPolicy.ValueNameFor(pack));

    [Fact]
    public void ThePolicyLivesUnderTheSameKeyAsTheOtherPolicyValues()
        => Assert.Equal(SftpSudoFallbackPolicy.PolicyKeyPath, ToolPackPolicy.PolicyKeyPath);

    [Theory]
    [InlineData(null, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(-1, false)]
    [InlineData(3, false)]
    [InlineData(99, false)]
    [InlineData(int.MaxValue, false)]
    [InlineData(int.MinValue, false)]
    public void OnlyZeroForbidsAPack(int? raw, bool forbidden)
    {
        ToolPackPolicy policy = new(name => name == "ToolPackAudit" ? raw : null);

        Assert.Equal(forbidden, policy.IsForbidden(ToolPack.Audit));
    }

    [Fact]
    public void AValueOnlyAppliesToThePackItIsNamedAfter()
    {
        ToolPackPolicy policy = new(name => name == "ToolPackAudit" ? ToolPackPolicy.ForbiddenValue : null);

        Assert.True(policy.IsForbidden(ToolPack.Audit));
        Assert.False(policy.IsForbidden(ToolPack.Essentials));
        Assert.False(policy.IsForbidden(ToolPack.NetworkDiagnostics));
        Assert.False(policy.IsForbidden(ToolPack.Administration));
        Assert.False(policy.IsForbidden(ToolPack.Core));
    }

    [Fact]
    public void ThePermanentSetIsNeverForbiddenAndIsNeverEvenRead()
    {
        List<string> requested = [];
        ToolPackPolicy policy = new(name =>
        {
            requested.Add(name);
            return ToolPackPolicy.ForbiddenValue;
        });

        Assert.False(policy.IsForbidden(ToolPack.Core));
        foreach (ToolPack pack in OptionalPacks)
        {
            Assert.True(policy.IsForbidden(pack));
        }

        Assert.DoesNotContain(ToolPackPolicy.ValueNameFor(ToolPack.Core), requested);
        Assert.Equal(
            OptionalPacks.Select(ToolPackPolicy.ValueNameFor).Order(StringComparer.Ordinal),
            requested.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void AnAbsentPolicyForbidsNothing()
    {
        ToolPackPolicy policy = new(_ => null);

        foreach (ToolPack pack in Enum.GetValues<ToolPack>())
        {
            Assert.False(policy.IsForbidden(pack));
        }
    }

    [Fact]
    public void ThePolicyIsReadOnceAndKept()
    {
        int reads = 0;
        int? value = ToolPackPolicy.NoRestrictionValue;
        ToolPackPolicy policy = new(_ =>
        {
            reads++;
            return value;
        });

        Assert.Equal(0, reads);
        Assert.False(policy.IsForbidden(ToolPack.Audit));
        int readsAfterFirstUse = reads;
        value = ToolPackPolicy.ForbiddenValue;
        Assert.False(policy.IsForbidden(ToolPack.Audit));
        Assert.False(policy.IsForbidden(ToolPack.Essentials));

        Assert.Equal(OptionalPacks.Length, readsAfterFirstUse);
        Assert.Equal(readsAfterFirstUse, reads);
    }
}
