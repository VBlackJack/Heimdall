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

using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Ssh;
using Heimdall.Ssh;

namespace Heimdall.App.Services;

/// <summary>
/// Evaluates local OpenSSH user certificates on both target profiles and SSH gateway hops,
/// surfacing proactive warnings on success and diagnostic causes when authentication rejects
/// an expired or not-yet-valid certificate.
/// </summary>
internal static class SshCertificateWarningHelper
{
    public sealed record EvaluationSummary(string? WarningMessage, bool HasInvalidValidityWindow);

    public static EvaluationSummary EvaluateServerAndGateways(
        ServerProfileDto server,
        AppSettings settings,
        LocalizationManager localizer)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(localizer);

        List<string> warnings = [];
        bool hasInvalidWindow = false;

        EvaluatePair(server.SshKeyPath, server.SshCertificatePath, prefix: null, localizer, warnings, ref hasInvalidWindow);

        if (SshGatewayRoute.GatewayIdFor(server, settings.GroupDefaults) is { } gatewayId
            && settings.SshGateways is { Count: > 0 })
        {
            try
            {
                List<SshGatewayDto> chain = GatewayChainResolver.ResolveChainDtos(gatewayId, settings.SshGateways);
                foreach (SshGatewayDto hop in chain)
                {
                    if (string.IsNullOrWhiteSpace(hop.KeyPath) && string.IsNullOrWhiteSpace(hop.SshCertificatePath))
                    {
                        continue;
                    }

                    string label = string.IsNullOrWhiteSpace(hop.Name) ? hop.Host : hop.Name;
                    EvaluatePair(hop.KeyPath, hop.SshCertificatePath, label, localizer, warnings, ref hasInvalidWindow);
                }
            }
            catch (GatewayChainException)
            {
            }
        }

        return warnings.Count == 0
            ? new EvaluationSummary(null, false)
            : new EvaluationSummary(string.Join(" ", warnings), hasInvalidWindow);
    }

    public static EvaluationSummary EvaluateChain(
        IReadOnlyList<SshConnectionParams> chain,
        LocalizationManager localizer)
    {
        ArgumentNullException.ThrowIfNull(chain);
        ArgumentNullException.ThrowIfNull(localizer);

        List<string> warnings = [];
        bool hasInvalidWindow = false;
        foreach (SshConnectionParams hop in chain)
        {
            string prefix = string.IsNullOrWhiteSpace(hop.LegacyCredentialName)
                ? hop.Host
                : hop.LegacyCredentialName;
            EvaluatePair(hop.KeyPath, hop.CertificatePath, prefix, localizer, warnings, ref hasInvalidWindow);
        }

        return warnings.Count == 0
            ? new EvaluationSummary(null, false)
            : new EvaluationSummary(string.Join(" ", warnings), hasInvalidWindow);
    }

    public static string? AppendInvalidWindowHint(string? baseMessage, EvaluationSummary summary)
    {
        if (!summary.HasInvalidValidityWindow || string.IsNullOrWhiteSpace(summary.WarningMessage))
        {
            return baseMessage;
        }

        return string.IsNullOrWhiteSpace(baseMessage)
            ? summary.WarningMessage
            : $"{baseMessage} {summary.WarningMessage}";
    }

    private static void EvaluatePair(
        string? keyPath,
        string? certPath,
        string? prefix,
        LocalizationManager localizer,
        ICollection<string> warnings,
        ref bool hasInvalidWindow)
    {
        SshCertificateExpiryAssessment assessment = SshCertificateExpiryEvaluator.Evaluate(keyPath, certPath);
        if (assessment.Status is SshCertificateExpiryStatus.Expired or SshCertificateExpiryStatus.NotYetValid)
        {
            hasInvalidWindow = true;
        }

        string? formatted = SshCertificateExpiryEvaluator.FormatLocalizedWarning(assessment, localizer);
        if (string.IsNullOrWhiteSpace(formatted))
        {
            return;
        }

        string message = string.IsNullOrWhiteSpace(prefix) ? formatted : $"{prefix}: {formatted}";
        if (!warnings.Contains(message, StringComparer.Ordinal))
        {
            warnings.Add(message);
        }
    }
}
