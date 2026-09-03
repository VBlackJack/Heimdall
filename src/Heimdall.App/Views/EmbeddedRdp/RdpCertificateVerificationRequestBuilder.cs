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
    /// <returns>The profile that identifier was minted for, or it unchanged when it is a
    /// profile's own.</returns>
    /// <remarks>
    /// <para><b>Trust is a property of the profile, not of the pane.</b> A session-scoped key is
    /// minted per pane so tunnel lifetime and error recovery stay independent, and it is written
    /// over the copy's <c>Id</c> because that is what the rest of the session pipeline keys on.
    /// Nothing about that is wrong; what was wrong was letting it reach the trust store, which
    /// stores one set of approved thumbprints per profile and is read again on the next
    /// connection, long after every key minted for this one has gone.</para>
    /// <para><b>Read back from the mint, which is where the answer actually is.</b> The pane copy
    /// is made in more than one place - a split, an ad-hoc reconnect, a multi-monitor coercion -
    /// but all of them get their identifier from <see cref="SessionIdCodec.Create"/>, so
    /// recording the profile there covers every one of them and no call site can forget to.</para>
    /// <para><b>Two weaker answers were shipped before it, and both misfiled an approval.</b>
    /// Inverting the identifier's shape unconditionally read the imported profile
    /// <c>prod_deadbeef</c> as a mint for <c>prod</c> and wrote its approval into that unrelated
    /// profile's trust set. Asking the inventory first - exact identifier, invert only when it
    /// names nothing - fixed that case and not the one where <c>prod_deadbeef</c> is deleted
    /// while its own connection is still being established: deleting a profile does not end the
    /// connection it started, so the question still carries the deleted profile's name while the
    /// approval lands under <c>prod</c> again. Absence from the inventory is not evidence of a
    /// mint, and no lookup can make it so.</para>
    /// </remarks>
    internal static string ResolveTrustProfileId(string runtimeProfileId)
        => SessionIdCodec.ResolveInventoryId(runtimeProfileId);
}
