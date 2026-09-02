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

using Heimdall.App.Views.EmbeddedRdp;

namespace Heimdall.App.Tests.Views.EmbeddedRdp;

public sealed class RdpRedirectionVisibilityPolicyTests
{
    [Theory]
    [InlineData(true, false, false, true)]   // active wins regardless of expand state
    [InlineData(true, true, false, true)]
    [InlineData(true, false, true, true)]
    [InlineData(false, true, false, true)]   // alwaysExpanded shows disabled
    [InlineData(false, false, true, true)]   // session override shows disabled
    [InlineData(false, true, true, true)]
    [InlineData(false, false, false, false)] // default: hide disabled
    public void IsIndicatorVisible_ReflectsActiveAndExpandFlags(
        bool isActive,
        bool alwaysExpanded,
        bool sessionExpandedOverride,
        bool expectedVisible)
    {
        var actual = RdpRedirectionVisibilityPolicy.IsIndicatorVisible(
            isActive,
            alwaysExpanded,
            sessionExpandedOverride);

        Assert.Equal(expectedVisible, actual);
    }

    [Theory]
    [InlineData(0, false, false, false)] // nothing disabled - no badge
    [InlineData(3, false, false, true)]  // default mode + disabled count > 0 -> badge
    [InlineData(3, true, false, false)]  // alwaysExpanded suppresses badge
    [InlineData(3, false, true, false)]  // session override hides badge (already expanded)
    [InlineData(8, false, false, true)]  // many disabled -> badge
    public void ShouldShowExpandBadge_OnlyWhenAutoCollapseHidesSomething(
        int disabledCount,
        bool alwaysExpanded,
        bool sessionExpandedOverride,
        bool expected)
    {
        var actual = RdpRedirectionVisibilityPolicy.ShouldShowExpandBadge(
            disabledCount,
            alwaysExpanded,
            sessionExpandedOverride);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void CountDisabled_CountsFalseEntries()
    {
        var states = new[] { true, false, true, false, false, true };

        var actual = RdpRedirectionVisibilityPolicy.CountDisabled(states);

        Assert.Equal(3, actual);
    }

    [Fact]
    public void CountDisabled_AllActive_ReturnsZero()
    {
        var states = new[] { true, true, true };

        var actual = RdpRedirectionVisibilityPolicy.CountDisabled(states);

        Assert.Equal(0, actual);
    }

    [Fact]
    public void CountDisabled_AllDisabled_ReturnsLength()
    {
        var states = new[] { false, false, false, false };

        var actual = RdpRedirectionVisibilityPolicy.CountDisabled(states);

        Assert.Equal(4, actual);
    }

    [Fact]
    public void CountDisabled_NullEnumerable_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => RdpRedirectionVisibilityPolicy.CountDisabled(null!));
    }
}
