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
            ResolveTrustProfileId(server.Id),
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
    /// <param name="runtimeProfileId">
    /// The <c>Id</c> the profile copy this pane runs on carries.
    /// </param>
    /// <returns>The inventory profile, or the argument unchanged when it is already one.</returns>
    /// <remarks>
    /// <para><b>Trust is a property of the profile, not of the pane.</b> A session-scoped key is
    /// minted per pane so tunnel lifetime and error recovery stay independent, and it is written
    /// over the copy's <c>Id</c> because that is what the rest of the session pipeline keys on.
    /// Nothing about that is wrong; what was wrong was letting it reach the trust store, which
    /// stores one set of approved thumbprints per profile and is read again on the next
    /// connection, long after every key minted for this one has gone.</para>
    /// <para><b>Recovered from the identifier rather than carried beside it</b>, because the pane
    /// copy is made in more than one place - a split, an ad-hoc reconnect, a multi-monitor
    /// coercion - and only the shape of the identifier is common to all of them.
    /// <see cref="SessionIdCodec"/> mints that shape and is the only thing that inverts it.</para>
    /// <para><b>The one ambiguity, stated rather than hidden.</b> An inventory identifier that
    /// itself ends in an underscore and eight hexadecimal characters is indistinguishable from a
    /// minted one without consulting the inventory, which this type cannot reach; such a profile
    /// would share a trust set with the profile named by its prefix. The same ambiguity is
    /// already accepted where a session identifier is decoded with the inventory in hand, and
    /// identifiers this application generates are GUIDs, which never take that shape.</para>
    /// </remarks>
    internal static string ResolveTrustProfileId(string runtimeProfileId)
        => SessionIdCodec.TryGetInventoryId(runtimeProfileId, out string inventoryProfileId)
            ? inventoryProfileId
            : runtimeProfileId;
}
