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

/// <summary>
/// Freezes that a live region says a thing once, not once per handler that rewrote it.
/// </summary>
public sealed class RdpAnnouncementLatchTests
{
    private const string SetStatusTextMember = "private void SetStatusText(string text)";
    private const string UpdateHealthDotMember =
        "private void UpdateHealthDot(bool? wasUserInitiatedDisconnectOverride = null)";

    [Fact]
    public void TheFirstWriteIsAnnounced()
    {
        RdpAnnouncementLatch latch = new();

        Assert.True(latch.ShouldAnnounce("Connected"));
    }

    [Fact]
    public void RewritingTheSameTextIsNotAnnouncedAgain()
    {
        RdpAnnouncementLatch latch = new();
        _ = latch.ShouldAnnounce("Connected");

        Assert.False(latch.ShouldAnnounce("Connected"));
        Assert.False(latch.ShouldAnnounce("Connected"));
    }

    [Fact]
    public void ADifferentTextIsAnnounced_AndTheOldOneBecomesNewAgain()
    {
        RdpAnnouncementLatch latch = new();
        _ = latch.ShouldAnnounce("Connected");

        Assert.True(latch.ShouldAnnounce("Disconnected"));
        Assert.True(latch.ShouldAnnounce("Connected"));
    }

    [Fact]
    public void TheComparisonIsExact()
    {
        RdpAnnouncementLatch latch = new();
        _ = latch.ShouldAnnounce("Connected");

        Assert.True(latch.ShouldAnnounce("connected"));
    }

    /// <summary>
    /// The two regions rewritten on every transition announce through a latch in the view.
    /// </summary>
    /// <remarks>
    /// Read as a condition standing as a step of the writer, and the announcement standing as a
    /// step of the block that condition guards - not as a fragment of text, which a fold behind
    /// a term that is false by construction would keep intact. What this cannot say is that no
    /// other site announces the same element bare; the live-region suite counts the sites.
    /// </remarks>
    [Theory]
    [InlineData(SetStatusTextMember, "if (_statusAnnouncements.ShouldAnnounce(text))", "_ = RdpLiveRegion.Announce(StatusTextBlock);")]
    [InlineData(UpdateHealthDotMember, "if (_healthDotAnnouncements.ShouldAnnounce(label))", "_ = RdpLiveRegion.Announce(HealthDot);")]
    public void TheViewAnnouncesThroughTheLatch(string member, string condition, string announcement)
    {
        string logic = ViewSource.HandlerLogic(member);

        Assert.True(
            ViewSource.IsStatementOfTheMethodBody(logic, condition),
            $"{member} no longer consults its announcement latch before announcing.");

        // The block the condition guards, read from the condition to the end of the member.
        string guarded = ViewSource.HandlerBody(logic, condition);

        Assert.True(
            ViewSource.IsStatementOfTheMethodBody(guarded, announcement),
            $"{member} consults the latch but does not announce inside the branch it guards.");
    }
}
