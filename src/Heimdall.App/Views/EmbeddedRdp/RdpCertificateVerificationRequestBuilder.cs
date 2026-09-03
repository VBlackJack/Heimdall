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
            server.Id,
            string.IsNullOrWhiteSpace(server.DisplayName)
                ? server.RemoteServer
                : server.DisplayName,
            target.Host,
            target.Port)
        {
            PromptScopeId = promptScopeId,
        };
    }
}
