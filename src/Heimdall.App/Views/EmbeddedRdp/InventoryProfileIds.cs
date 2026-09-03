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

namespace Heimdall.App.Views.EmbeddedRdp;

/// <summary>
/// Reads the inventory once so a pane can tell an identifier a profile really has from one that
/// was minted for a session.
/// </summary>
/// <remarks>
/// <para>Its own type rather than a private helper on the view, so what it decides is measured by
/// running it: the view's certificate path needs a live <c>Application</c>, an ActiveX host and a
/// service provider before it reaches any of this, and a decision reachable only through all of
/// that ends up asserted by reading source text instead.</para>
/// <para><b>The unavailable inventory reports every identifier as its own</b>, which is the
/// direction that cannot leak an approval. Saying "no profile has this identifier" would send
/// every runtime identifier through the inversion - exactly the defect this exists to stop -
/// whereas saying "this identifier is a profile" only means a split pane's approval is filed
/// under a key that dies with the pane, so the certificate is asked about again next time.</para>
/// <para><b>An inventory that holds no profile is answered the same way, and on its own
/// grounds rather than by analogy with the read that threw.</b> Decoding exists to file an
/// approval under the inventory profile a pane belongs to; where the inventory holds no
/// profile there is none to file it under, so the decoded prefix names nothing either and the
/// inversion cannot buy anything. What it can still cost is real, because
/// <see cref="Heimdall.Core.Certificates.RdpCertificateTrustStore"/> writes under whatever key
/// it is handed and that write is persisted: an approval decoded to <c>prod</c> while the
/// inventory was empty is sitting in the settings file when a profile called <c>prod</c>
/// arrives, and that profile then opens sessions on a certificate nobody was ever asked about
/// for it. So the empty inventory is not authoritative here; it is the same "decode nothing"
/// as the unreadable one, for the opposite reason.</para>
/// <para><b>And it is a reachable state, not a defensive gesture.</b> <c>ConfigManager</c>
/// returns an empty inventory document without throwing when <c>servers.json</c> does not
/// exist, so an external restore, a synchronisation that replaces the configuration directory,
/// or the last profile being deleted while its tab stays open all leave a live pane asking
/// about a certificate against an inventory that loads cleanly and holds nothing.</para>
/// </remarks>
internal static class InventoryProfileIds
{
    /// <summary>Whether an identifier names a profile the inventory holds.</summary>
    /// <param name="configManager">
    /// The configuration store, or null when the application has none to offer.
    /// </param>
    public static async Task<Func<string, bool>> LoadPredicateAsync(IConfigManager? configManager)
    {
        if (configManager is null)
        {
            return EveryIdentifierIsItsOwn;
        }

        try
        {
            List<ServerProfileDto> servers = await configManager.LoadServersAsync();
            HashSet<string> identifiers = new(
                servers
                    .Select(profile => profile.Id)
                    .Where(id => !string.IsNullOrWhiteSpace(id)),
                StringComparer.Ordinal);

            if (identifiers.Count == 0)
            {
                Core.Logging.FileLogger.Warn(
                    "The server inventory holds no profile while a certificate approval is "
                    + "being filed. No identifier will be decoded, so no approval can be "
                    + "written under a profile that is not there.");

                return EveryIdentifierIsItsOwn;
            }

            return identifiers.Contains;
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn(
                "The server inventory could not be read while deciding which profile a "
                + $"certificate approval belongs to: {ex.Message}. No identifier will be "
                + "decoded, so no approval can reach another profile.");

            return EveryIdentifierIsItsOwn;
        }
    }

    private static bool EveryIdentifierIsItsOwn(string identifier) => true;
}
