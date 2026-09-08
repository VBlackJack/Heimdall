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

using Heimdall.App.ViewModels.Dialogs;
using Heimdall.Core.Configuration;
using Heimdall.Core.Security;
using Heimdall.Core.Ssh;
using Heimdall.Ssh;

namespace Heimdall.App.Services;

/// <summary>Adapts the dialog snapshot to isolated, pinned diagnostics without saving configuration.</summary>
internal static class GatewayDiagnosticService
{
    public static void Configure(GatewayDialogViewModel viewModel, AppSettings settings, IHostKeyTrustService trust)
    {
        viewModel.ConfigureDiagnostics(settings.SshGateways, (route, host, port, progress, ct) =>
            Task.Run(async () =>
            {
                // Authentication material is prepared off the dispatcher and never enters report models.
                List<SshConnectionParams> chain = GatewayChainResolver.ToConnectionParams(
                    route, CredentialProtector.Unprotect, settings.SshAgentPreference);
                return await GatewayRouteDiagnostic.RunAsync(chain,
                    hop => trust.GetEffectiveEntry(hop.Host, hop.Port)?.Fingerprint,
                    TimeSpan.FromMilliseconds(settings.HostKeyProbeTimeoutMs),
                    host, port, progress, ct).ConfigureAwait(false);
            }, ct));
    }
}

