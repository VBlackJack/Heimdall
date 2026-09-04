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
using Heimdall.Core.Certificates;
using Heimdall.Core.Codecs;
using Heimdall.Core.Configuration;

namespace Heimdall.App.Views.EmbeddedRdp;

/// <summary>Builds the certificate verification request one pane is about to run.</summary>
/// <remarks>
/// <para>Extracted from the code-behind for one reason: the scope token it carries is what
/// routes the resulting question back into the pane that asked, and a request built without one
/// is refused rather than asked. That is a security-relevant field whose only evidence, while
/// it was written inline in an object initializer, was a reading of source text - and an object
/// initializer sits below the statement level that this repository's source readings can
/// measure at all.</para>
/// <para>The profile name is decided here too, since it is what the question calls the machine
/// when the user has named it, and the bare address when they have not.</para>
/// <para><b>And the trust identity, which is not the identifier the profile arrives with.</b>
/// A pane opened by a split runs on a copy of the profile whose <c>Id</c> has been replaced by a
/// session-scoped state key, and an ad-hoc reconnect makes the same substitution on its own copy.
/// The ordinary path writes that key over the inventory profile for the length of the connect and
/// puts the original back afterwards, so a copy taken inside that window - the one made to coerce
/// a multi-monitor layout - can freeze the key too. Passing that key through as the trust
/// identity filed the approval under an identifier that dies with the pane, so the certificate
/// was asked for again next time, and it gave two panes of one profile two different coalescing
/// scopes - so one certificate was asked about twice.</para>
/// </remarks>
internal static class RdpCertificateVerificationRequestBuilder
{
    /// <summary>Builds the request for <paramref name="server"/> against a probe target.</summary>
    /// <param name="server">The profile about to be connected.</param>
    /// <param name="target">The endpoint the probe will dial.</param>
    /// <param name="promptScopeId">The token identifying the surface that must ask.</param>
    public static RdpCertificateVerificationRequest Build(
        ServerProfileDto server,
        RdpCertificateProbeTarget target,
        string promptScopeId)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentException.ThrowIfNullOrWhiteSpace(promptScopeId);

        return new RdpCertificateVerificationRequest(
            ResolveTrustKey(server),
            string.IsNullOrWhiteSpace(server.DisplayName)
                ? server.RemoteServer
                : server.DisplayName,
            target.Host,
            target.Port)
        {
            PromptScopeId = promptScopeId,
        };
    }

    /// <summary>
    /// The profile an approval belongs to, given the identifier a pane runs under.
    /// </summary>
    /// <param name="server">The profile copy this pane runs on.</param>
    /// <returns>The inventory profile the pane belongs to, or its own identifier when it is one.
    /// </returns>
    /// <remarks>
    /// <para><b>Trust is a property of the profile, not of the pane.</b> A session-scoped key is
    /// minted per pane so tunnel lifetime and error recovery stay independent, and it is written
    /// over the copy's <c>Id</c> because that is what the rest of the session pipeline keys on.
    /// Nothing about that is wrong; what was wrong was letting it reach the trust store, which
    /// stores one set of approved thumbprints per profile and is read again on the next
    /// connection, long after every key minted for this one has gone.</para>
    /// <para><b>Asked of the profile, which is the only thing that knows.</b>
    /// <see cref="ServerProfileDto.AdoptSessionIdentity"/> records the profile being left behind
    /// at the instant the key replaces it, and <see cref="ServerProfileDto.InventoryProfileId"/> reads
    /// that back. Nothing here inspects the identifier's text.</para>
    /// <para><b>Three weaker answers were shipped before it, and each misfiled an approval.</b>
    /// Inverting the identifier's SHAPE read the imported profile <c>prod_deadbeef</c> as a mint
    /// for <c>prod</c>. Asking the INVENTORY fixed that and not the profile deleted while its own
    /// connection was still being established - deleting a profile does not end the connection it
    /// started, and a deleted profile is absent for the same reason a minted key is. Keeping a
    /// process-wide LEDGER of every mint fixed that and not an import arriving under a string an
    /// earlier session was minted: the session identifier is written to the log, and an import
    /// preserves the identifier its file carried, so the ledger recognised it as its own past
    /// mint. Each answer asked what a string WAS; none could, because the same string can be both
    /// a mint and a profile's name. What distinguishes them is the object's role, and only the
    /// code that assigned the identifier ever holds it.</para>
    /// <para><b>The same rule decides the scope.</b> A destination typed by hand carries an
    /// identifier a saved profile can also hold, so the identifier's text cannot say which of the
    /// two a copy is; <see cref="ServerProfileDto.IsTypedDestination"/> is set by the two sites
    /// that mint a typed destination and by nothing that reads a profile from disk. A typed
    /// destination is then keyed by its host - the only thing the user typed, and the only thing
    /// that finds the approval again on the next launch, since the server row mints a fresh
    /// identifier per launch.</para>
    /// </remarks>
    internal static RdpTrustKey ResolveTrustKey(ServerProfileDto server)
    {
        ArgumentNullException.ThrowIfNull(server);

        return server.IsTypedDestination
            ? RdpTrustKey.ForTypedDestination(server.RemoteServer)
            : RdpTrustKey.ForProfile(server.InventoryProfileId);
    }
}
