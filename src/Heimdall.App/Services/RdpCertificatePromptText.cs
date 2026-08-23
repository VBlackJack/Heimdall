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

namespace Heimdall.App.Services;

/// <summary>Locale keys for the RDP certificate question.</summary>
public static class RdpCertificatePromptLocaleKeys
{
    /// <summary>Title of the question.</summary>
    public const string Title = "RdpCertPromptTitle";

    /// <summary>The body: this profile has never seen this certificate.</summary>
    public const string Message = "RdpCertPromptMessage";

    /// <summary>Said when the profile already trusts exactly one other certificate.</summary>
    public const string AlreadyTrustedOne = "RdpCertPromptAlreadyTrustedOne";

    /// <summary>Said when it already trusts several.</summary>
    public const string AlreadyTrustedMany = "RdpCertPromptAlreadyTrustedMany";

    /// <summary>Remember this certificate for this profile.</summary>
    public const string Trust = "RdpCertPromptTrust";

    /// <summary>Accept for this run only.</summary>
    public const string TrustOnce = "RdpCertPromptTrustOnce";

    /// <summary>Do not connect.</summary>
    public const string Refuse = "RdpCertPromptRefuse";

    /// <summary>Label above the thumbprint.</summary>
    public const string ThumbprintLabel = "RdpCertPromptThumbprintLabel";
}

/// <summary>
/// Which sentence the certificate question carries, given what the profile already trusts.
/// </summary>
/// <remarks>
/// Pure, and separate from the dialog, because this is the part worth pinning: building a
/// WPF window in a test seals application styles onto the shared dispatcher and takes
/// unrelated tests down with it.
/// </remarks>
public static class RdpCertificatePromptText
{
    /// <summary>The reassurance line to show, or null when there is nothing to reassure about.</summary>
    /// <param name="alreadyTrustedCount">How many certificates this profile already trusts.</param>
    /// <remarks>
    /// <b>Nothing is said on the first certificate.</b> There is no reassurance to offer -
    /// the profile has never trusted anything for this name, so the plain question is the
    /// honest one, and adding "you already trust 0 others" would be noise where the alarm
    /// is appropriate.
    /// <para>
    /// One and several are separate keys rather than one key with a number, because no
    /// language this application ships pluralises by substitution: "1 certificates" is
    /// wrong in English and "1 certificats" is wrong in French.
    /// </para>
    /// </remarks>
    public static string? AlreadyTrustedKey(int alreadyTrustedCount) => alreadyTrustedCount switch
    {
        <= 0 => null,
        1 => RdpCertificatePromptLocaleKeys.AlreadyTrustedOne,
        _ => RdpCertificatePromptLocaleKeys.AlreadyTrustedMany,
    };
}
