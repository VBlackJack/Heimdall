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

namespace Heimdall.Rdp;

/// <summary>
/// Authentication settings shared by external .rdp generation and the embedded ActiveX host.
/// <paramref name="AuthenticationLevel"/> value 1 requires server authentication; value 2 attempts
/// server authentication and warns on failure; value 0 imposes no server-authentication requirement.
/// The same numeric values apply to the .rdp <c>authentication level:i:</c> field and to
/// <c>IMsRdpClientAdvancedSettings.AuthenticationLevel</c>.
/// </summary>
/// <param name="AuthenticationLevel">Server-authentication level to apply.</param>
/// <param name="EnableCredSspSupport">Whether CredSSP/NLA support is enabled.</param>
public readonly record struct RdpAuthenticationSettings(
    int AuthenticationLevel,
    bool EnableCredSspSupport)
{
    /// <summary>
    /// Whether the client checks the server's identity at all. False at level 0, where the
    /// client connects to whoever answers and nothing vouches for the machine a typed or
    /// staged password is handed to.
    /// </summary>
    public bool AuthenticatesServer =>
        AuthenticationLevel != RdpAuthenticationResolver.NoServerAuthenticationLevel;
}

/// <summary>
/// Resolves RDP authentication settings from the NLA and strict server-authentication toggles.
/// This is the single source of truth shared by the embedded ActiveX host and the
/// external .rdp generator to guarantee embedded/external parity.
/// </summary>
public static class RdpAuthenticationResolver
{
    /// <summary>Level 0: connect without any server-authentication requirement.</summary>
    public const int NoServerAuthenticationLevel = 0;

    /// <summary>Level 1: fail the connection when the server cannot be authenticated.</summary>
    public const int RequireServerAuthenticationLevel = 1;

    /// <summary>Level 2: attempt server authentication and warn when it fails.</summary>
    public const int WarnOnServerAuthenticationFailureLevel = 2;

    /// <summary>
    /// Resolves RDP server-authentication and CredSSP settings for the supplied NLA state.
    /// </summary>
    public static RdpAuthenticationSettings Resolve(
        bool nlaEnabled,
        bool strictServerAuthentication = false)
    {
        if (!nlaEnabled)
        {
            return new RdpAuthenticationSettings(NoServerAuthenticationLevel, false);
        }

        return strictServerAuthentication
            ? new RdpAuthenticationSettings(RequireServerAuthenticationLevel, true)
            : new RdpAuthenticationSettings(WarnOnServerAuthenticationFailureLevel, true);
    }
}
