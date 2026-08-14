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
using Heimdall.App.Views.EmbeddedRdp;

namespace Heimdall.App.Tests.Views.EmbeddedRdp;

/// <summary>
/// RDP-016: a DPI change dropped during the post-connect lockout must be replayed when the lockout
/// ends, and replayed by force - the scale can change while the pixel dimensions do not.
/// </summary>
public sealed class RdpStabilizationResumePolicyTests
{
    [Fact]
    public void Decide_DpiChangeDropped_ForcesTheApplyEvenWhenTheSizeIsUnchanged()
    {
        RdpStabilizationResumeAction action = RdpStabilizationResumePolicy.Decide(
            dpiChangeDropped: true,
            queuedWidth: 1920,
            queuedHeight: 1080,
            lastAppliedWidth: 1920,
            lastAppliedHeight: 1080);

        Assert.Equal(RdpStabilizationResumeAction.ApplyForced, action);
    }

    [Fact]
    public void Decide_DpiChangeDropped_ForcesTheApplyEvenWhenNothingIsQueued()
    {
        RdpStabilizationResumeAction action = RdpStabilizationResumePolicy.Decide(
            dpiChangeDropped: true,
            queuedWidth: 0,
            queuedHeight: 0,
            lastAppliedWidth: 1920,
            lastAppliedHeight: 1080);

        Assert.Equal(RdpStabilizationResumeAction.ApplyForced, action);
    }

    [Fact]
    public void Decide_NoDropAndSizeChanged_AppliesTheQueuedSize()
    {
        RdpStabilizationResumeAction action = RdpStabilizationResumePolicy.Decide(
            dpiChangeDropped: false,
            queuedWidth: 2560,
            queuedHeight: 1440,
            lastAppliedWidth: 1920,
            lastAppliedHeight: 1080);

        Assert.Equal(RdpStabilizationResumeAction.ApplyQueued, action);
    }

    [Fact]
    public void Decide_NoDropAndSizeUnchanged_DoesNothing()
    {
        RdpStabilizationResumeAction action = RdpStabilizationResumePolicy.Decide(
            dpiChangeDropped: false,
            queuedWidth: 1920,
            queuedHeight: 1080,
            lastAppliedWidth: 1920,
            lastAppliedHeight: 1080);

        Assert.Equal(RdpStabilizationResumeAction.None, action);
    }

    [Theory]
    [InlineData(0, 1080)]
    [InlineData(1920, 0)]
    [InlineData(-1, -1)]
    public void Decide_NoDropAndNothingUsableQueued_DoesNothing(int queuedWidth, int queuedHeight)
    {
        RdpStabilizationResumeAction action = RdpStabilizationResumePolicy.Decide(
            dpiChangeDropped: false,
            queuedWidth,
            queuedHeight,
            lastAppliedWidth: 1920,
            lastAppliedHeight: 1080);

        Assert.Equal(RdpStabilizationResumeAction.None, action);
    }
}

/// <summary>
/// RDP-017: every resolution choice must state its SmartSizing value. The defect is symmetric -
/// a fixed preset that fits must turn scaling off, and Fit-to-Window must turn it on.
/// </summary>
public sealed class RdpSmartSizingPolicyTests
{
    [Fact]
    public void ShouldEnable_FitToWindow_AlwaysScales()
    {
        Assert.True(RdpSmartSizingPolicy.ShouldEnable(
            ResolutionChoiceKind.MatchWindow,
            resolutionExceedsSurface: false));
    }

    [Fact]
    public void ShouldEnable_FixedLargerThanSurface_Scales()
    {
        Assert.True(RdpSmartSizingPolicy.ShouldEnable(
            ResolutionChoiceKind.Fixed,
            resolutionExceedsSurface: true));
    }

    // The half of the defect the original recommendation missed: after Fit-to-Window, choosing a
    // preset that fits left the content stretched because nothing ever turned scaling back off.
    [Fact]
    public void ShouldEnable_FixedFittingInTheSurface_DoesNotScale()
    {
        Assert.False(RdpSmartSizingPolicy.ShouldEnable(
            ResolutionChoiceKind.Fixed,
            resolutionExceedsSurface: false));
    }
}
