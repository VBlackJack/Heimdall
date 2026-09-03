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
    /// <param name="isInventoryProfileId">
    /// Whether an identifier names a profile the inventory holds. Supplied by the caller because
    /// this type cannot reach the inventory itself, and required rather than optional so a call
    /// site cannot omit it and silently go back to inverting the mint unconditionally.
    /// </param>
    public static RdpCertificateVerificationRequest Build(
        ServerProfileDto server,
        RdpCertificateProbeTarget target,
        string promptScopeId,
        Func<string, bool> isInventoryProfileId)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentException.ThrowIfNullOrWhiteSpace(promptScopeId);
        ArgumentNullException.ThrowIfNull(isInventoryProfileId);

        return new RdpCertificateVerificationRequest(
            ResolveTrustProfileId(server.Id, isInventoryProfileId),
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
    /// <param name="isInventoryProfileId">
    /// Whether an identifier names a profile the inventory holds.
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
    /// <see cref="SessionIdCodec"/> mints that shape and is the only thing that inverts it. A
    /// field written where the mint happens would have to be written at every one of those
    /// places and stay written through every copy taken afterwards; a site that forgot it would
    /// fail silently, and there is no compiler census of a field nobody is required to set.</para>
    /// <para><b>But never inverted unconditionally, which is what shipped and what this fixes.</b>
    /// An import preserves the identifier its file carried, so a profile genuinely called
    /// <c>prod_deadbeef</c> is an ordinary inventory profile with an identifier of the minted
    /// shape. Inverting it decoded that profile to <c>prod</c>, and an approval given for
    /// <c>prod_deadbeef</c> was written into the trust set of the unrelated profile <c>prod</c>,
    /// which then opened sessions on that certificate without asking. The inventory therefore
    /// decides: the exact identifier is looked up first and the mint is inverted only when it
    /// names no profile, which is the rule the callers that already hold the inventory apply -
    /// see <c>ServerListViewModel.PersistExecutionTrustAsync</c>, whose lookup is now the same
    /// <see cref="SessionIdCodec.ResolveInventoryId"/> call as this one.</para>
    /// <para><b>The residual ambiguity, narrowed rather than closed.</b> With both <c>prod</c>
    /// and <c>prod_deadbeef</c> in the inventory, a session minted for <c>prod</c> whose
    /// discriminator is exactly <c>deadbeef</c> still resolves to <c>prod_deadbeef</c>. That
    /// needs an eight-hexadecimal-character collision out of a GUID against an identifier that
    /// already exists, where the unconditional inversion needed only the profile to exist. When
    /// the inventory cannot be read at all, and equally when it loads and holds no profile, the
    /// caller says so by reporting every identifier as an inventory one, so nothing is decoded:
    /// a split pane is then asked about its certificate again next time, which is the harmless
    /// half of the failure.</para>
    /// </remarks>
    internal static string ResolveTrustProfileId(
        string runtimeProfileId,
        Func<string, bool> isInventoryProfileId)
        => SessionIdCodec.ResolveInventoryId(runtimeProfileId, isInventoryProfileId);
}
