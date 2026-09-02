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

using System.Windows;
using System.Windows.Automation.Peers;

namespace Heimdall.App.Views.EmbeddedRdp;

/// <summary>
/// Announces a live region to UI Automation clients.
/// </summary>
/// <remarks>
/// <para><c>AutomationProperties.LiveSetting</c> only publishes a property. A client is told that
/// the region changed by one thing and one thing only: an
/// <see cref="AutomationEvents.LiveRegionChanged"/> event raised on the element's peer. The RDP
/// view declared the setting on ten elements and raised the event on none of them, so a screen
/// reader stayed silent through every status change, including the disconnect message on a tab the
/// user was not looking at.</para>
/// <para>The peer is taken from the element if it already has one, and created otherwise: WPF
/// caches the created peer on the element, which is the same route the repository's binding-driven
/// live-region behavior takes.</para>
/// </remarks>
internal static class RdpLiveRegion
{
    /// <summary>Raises the live-region event, and reports whether the element could carry one.</summary>
    internal static bool Announce(UIElement? element)
    {
        if (element is null)
        {
            return false;
        }

        AutomationPeer? peer = UIElementAutomationPeer.FromElement(element)
            ?? UIElementAutomationPeer.CreatePeerForElement(element);
        if (peer is null)
        {
            return false;
        }

        peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        return true;
    }
}
