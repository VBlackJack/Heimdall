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

namespace Heimdall.App.Tests.Views.EmbeddedRdp;

/// <summary>
/// Freezes that the disconnect overlay survives a trip to the profile editor.
/// </summary>
/// <remarks>
/// <para>"Edit profile" is the pre-focused default for six disconnect codes, so Enter on the
/// overlay takes it. It used to collapse the overlay before raising the request, and nothing ever
/// brought it back: the only writer that sets the overlay visible is the disconnect path itself,
/// reachable only from a fresh callback on a session that is already dead, and it collapses the
/// native host on the line above. The user fixed the password, closed the editor, and was returned
/// to a pane with a header strip over nothing - no Reconnect button, no Close button, no way
/// forward but closing the tab from the tab strip.</para>
/// <para>The editor is a modal dialog over the same tab, so leaving the overlay behind it costs
/// nothing and it is still there when the dialog closes.</para>
/// </remarks>
public sealed class RdpOverlayWayForwardTests
{
    private const string CollapseOverlay =
        "ReconnectOverlay.Visibility = System.Windows.Visibility.Collapsed;";

    [Fact]
    public void EditProfileDoesNotTakeTheOverlayAwayWithIt()
    {
        string handler = ViewSource.HandlerBody("private void OnOverlayEditProfileClick");

        Assert.DoesNotContain(CollapseOverlay, handler, StringComparison.Ordinal);
        Assert.Contains("EditServerRequested?.Invoke(", handler, StringComparison.Ordinal);
    }

    // Positive control: the two handlers that genuinely finish with the overlay still collapse it,
    // so the assertion above is measuring this handler and not a string that stopped existing.
    [Fact]
    public void TheHandlersThatDoFinishWithTheOverlayStillCollapseIt()
    {
        Assert.Contains(
            CollapseOverlay,
            ViewSource.HandlerBody("private void OnOverlayReconnectClick"),
            StringComparison.Ordinal);
        Assert.Contains(
            CollapseOverlay,
            ViewSource.HandlerBody("private void OnOverlayCloseClick"),
            StringComparison.Ordinal);
    }
}
