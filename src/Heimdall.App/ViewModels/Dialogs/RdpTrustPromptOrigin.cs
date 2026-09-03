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

namespace Heimdall.App.ViewModels.Dialogs;

/// <summary>Where a certificate question came from, in the terms the user recognises.</summary>
/// <param name="RemoteEndpointLabel">
/// The machine the session is actually reaching, as the profile names it, with the tunnel
/// endpoint appended when there is one.
/// </param>
/// <param name="RouteLabel">
/// The SSH gateways the profile is configured to reach that machine through, or null for a
/// direct connection. Configuration rather than measurement, and labelled as such on screen:
/// nothing records the chain a live tunnel actually resolved, so a gateway edited during a slow
/// establishment can make this disagree with the wire.
/// </param>
/// <param name="TabTitle">
/// How the tab holding the session is announced, when it has a name. The announced name rather
/// than the displayed one: two tabs of the same profile display the same title by construction.
/// </param>
/// <param name="WindowTitle">The title of the window holding that tab, when it has one.</param>
/// <remarks>
/// <para><b>Every field here answers "which machine, and where is the question".</b> The
/// verification context on its own cannot: its address is the one that was dialled, and for a
/// session tunnelled over SSH that is the local end of the tunnel. Two tunnelled profiles both
/// named "Production" produced two questions reading <c>"Production" ... 127.0.0.1</c>, both
/// owned by the main window, and either answer could be given to the wrong machine.</para>
/// <para><b>The route is what actually distinguishes them, and it is a separate field for that
/// reason.</b> Naming the profile's own <c>RemoteServer</c> instead of 127.0.0.1 was not enough:
/// two profiles reaching one short name through two different gateways are two machines, and
/// their endpoint text differs only by an ephemeral local port. The gateway was read to pick the
/// endpoint's format string and then thrown away; carrying it as its own field is what makes the
/// two questions readably different. It is the configured chain, and the question says so rather
/// than claiming the certificate arrived that way - see <see cref="Services.RdpTrustPromptRoute"/>
/// for what would have to be recorded before it could claim more.</para>
/// <para><b>What the owner fields are, and what they are not.</b> They say where on screen the
/// question is. They are not the thing that tells two machines apart - the profile name, the
/// endpoint and the route are - because two sessions of one profile are the same machine and one
/// answer is the right answer for both.</para>
/// </remarks>
public sealed record RdpTrustPromptOrigin(
    string RemoteEndpointLabel,
    string? RouteLabel,
    string? TabTitle,
    string? WindowTitle);
