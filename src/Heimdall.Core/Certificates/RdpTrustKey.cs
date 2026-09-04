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

namespace Heimdall.Core.Certificates;

/// <summary>Who owns an RDP certificate approval.</summary>
/// <remarks>
/// <para>Two things connect over RDP and can meet an unknown certificate: a profile the user
/// saved, and a destination the user typed by hand. They can carry the same identifier string,
/// because the command palette mints <c>adhoc-rdp-&lt;host&gt;</c> for a typed destination and an
/// import, an old installation or a hand edit can put that same string on a saved profile. An
/// approval keyed by the string alone therefore had two possible owners and nothing on disk to
/// tell them apart; approving for one silenced the question for the other.</para>
/// <para>The scope is part of the key, so the two are two owners even when their strings agree.
/// Nothing here reads the identifier's text to decide which it is: the code that minted the
/// profile says so through <c>ServerProfileDto.MarkAsTypedDestination</c>, and the pane reads that
/// mark. Three earlier attempts inferred the role from the string's shape, and each handed one
/// owner's approval to the other.</para>
/// </remarks>
public enum RdpTrustScope
{
    /// <summary>A profile in the inventory; the identity is its identifier.</summary>
    Profile,

    /// <summary>A destination typed by hand; the identity is its host.</summary>
    TypedDestination
}

/// <summary>The key an RDP certificate approval is filed under: an owner and its identity.</summary>
/// <param name="Scope">Which kind of owner.</param>
/// <param name="Identity">The profile identifier, or the normalized host.</param>
/// <remarks>
/// <para><b>A typed destination is keyed by its host, not by the identifier it was minted.</b> A
/// typed destination IS a host: that is all the user typed. The server row's "connect as RDP"
/// mints a fresh identifier per launch, under which an approval could never be found again, and
/// the palette's identifier is only the host with a prefix. The host is normalized once, here,
/// and compared ordinal everywhere after, which is the discipline the store already applies to
/// thumbprints: a set that dedupes by a looser rule than its lookup applies hides entries from
/// that lookup.</para>
/// <para>A profile is keyed by the identifier the inventory knows it by, which for a pane-scoped
/// copy is <c>ServerProfileDto.InventoryProfileId</c>, not the session key the pane runs under.
/// </para>
/// </remarks>
public readonly record struct RdpTrustKey(RdpTrustScope Scope, string Identity)
{
    /// <summary>The key of a saved profile's approvals.</summary>
    /// <param name="profileId">The inventory identifier of the profile.</param>
    public static RdpTrustKey ForProfile(string profileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        return new RdpTrustKey(RdpTrustScope.Profile, profileId);
    }

    /// <summary>The key of a typed destination's approvals.</summary>
    /// <param name="host">The host the user typed, in whatever case and padding they typed it.</param>
    public static RdpTrustKey ForTypedDestination(string host)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        return new RdpTrustKey(RdpTrustScope.TypedDestination, NormalizeHost(host));
    }

    /// <summary>The one spelling of a typed destination's host.</summary>
    /// <param name="host">The host as typed.</param>
    /// <remarks>
    /// Trimmed and lower-cased with the invariant culture. Host names are case-insensitive by
    /// definition, and a user who types <c>PROD.example</c> today and <c>prod.example</c>
    /// tomorrow means the same machine; keeping two sets would ask them twice for one
    /// certificate.
    /// </remarks>
    public static string NormalizeHost(string host)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        return host.Trim().ToLowerInvariant();
    }

    /// <summary>The key as one string, for a log line or a coalescing scope.</summary>
    /// <remarks>
    /// Carries the scope, so two keys with one identity string never render the same. The
    /// separator cannot occur in a scope name, so the rendering is unambiguous.
    /// </remarks>
    public override string ToString() => $"{Scope}:{Identity}";
}
