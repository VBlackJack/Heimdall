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

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Heimdall.Core.Configuration;
using Heimdall.Core.Logging;

namespace Heimdall.App.Services.SharedSessions;

/// <summary>
/// Whether the post-connect commands of a shared profile may run: asked the first time, and again
/// whenever the commands change in the shared file.
/// </summary>
/// <remarks>
/// <para>The approval is <see cref="ApprovalFingerprint"/>: the <see cref="PostConnectFingerprint"/>
/// of the commands bound to the destination they run against (host, ports, user names), stored
/// locally under the shared profile's identifier in <see cref="AppSettings.SharedPostConnectApprovals"/>.
/// Binding the destination means a shared file that keeps the commands and points the profile at
/// another machine, or another account, is asked about again.
/// <see cref="ServerProfileDto.ExecutionConfirmed"/> stays false on the profile: it is rebuilt from
/// a file other people edit, and a flag on it would approve whatever that file says next.</para>
/// </remarks>
public sealed class SharedPostConnectTrust
{
    /// <summary>
    /// Prefix of a stored approval. Approvals written before the destination was bound carry the
    /// commands' own <c>v1:</c> prefix and never match, so each is asked about once more.
    /// </summary>
    private const string ApprovalVersion = "v2:";

    /// <summary>Joins the commands' fingerprint to the destination before hashing.</summary>
    private const char FieldSeparator = (char)0x1F;

    private readonly IConfigManager _configManager;

    public SharedPostConnectTrust(IConfigManager configManager)
    {
        _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
    }

    /// <summary>
    /// True when the commands of <paramref name="profile"/> may run: already approved as they are,
    /// or approved now through <paramref name="confirm"/>, in which case the approval is recorded.
    /// </summary>
    public async Task<bool> AuthorizeAsync(ServerProfileDto profile, Func<Task<bool>> confirm)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(confirm);

        string profileId = profile.InventoryProfileId;
        string fingerprint = ApprovalFingerprint(profile);
        AppSettings settings = await _configManager.LoadSettingsAsync().ConfigureAwait(false);
        if (settings.SharedPostConnectApprovals.TryGetValue(profileId, out string? approved)
            && string.Equals(approved, fingerprint, StringComparison.Ordinal))
        {
            return true;
        }

        if (!await confirm().ConfigureAwait(false))
        {
            return false;
        }

        await _configManager.MergeSettingAsync(current => current.SharedPostConnectApprovals[profileId] = fingerprint)
            .ConfigureAwait(false);
        FileLogger.Info($"Post-connect commands of shared session {profileId} approved ({fingerprint}).");
        return true;
    }

    /// <summary>
    /// The value an approval is stored as: the commands' fingerprint together with the destination
    /// they run against.
    /// </summary>
    /// <remarks>
    /// The host is trimmed and lower-cased, so spelling a host name in another case is not a new
    /// destination. Every port and user name the profile carries is included rather than the pair
    /// the connection type happens to use: a post-connect step runs in a shell the profile opens,
    /// and a change to any of them changes who or what the commands act on.
    /// </remarks>
    public static string ApprovalFingerprint(ServerProfileDto profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        string destination = JsonSerializer.Serialize(new object?[]
        {
            profile.ConnectionType?.Trim().ToUpperInvariant(),
            profile.RemoteServer?.Trim().ToLowerInvariant(),
            profile.RemotePort,
            profile.SshPort,
            profile.WinRmPort,
            profile.RdpUsername?.Trim(),
            profile.WinRmUsername?.Trim(),
            profile.SshUsername?.Trim(),
        });
        string commands = PostConnectFingerprint.Compute(profile.PostConnectSteps);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(commands + FieldSeparator + destination));
        return ApprovalVersion + Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Forgets the approvals of the sessions of every source other than <paramref name="keptSourceIds"/>;
    /// writes the settings only when there is one to forget.
    /// </summary>
    /// <remarks>
    /// A source the saved settings list at the time of the write is kept too, whatever
    /// <paramref name="keptSourceIds"/> says: the list was taken from the settings a load started
    /// with, and a source added since must not lose what was approved for it meanwhile. An
    /// approval whose key is not a shared identifier names no source and is left alone.
    /// </remarks>
    public async Task ForgetAllExceptAsync(IReadOnlySet<string> keptSourceIds)
    {
        ArgumentNullException.ThrowIfNull(keptSourceIds);

        AppSettings settings = await _configManager.LoadSettingsAsync().ConfigureAwait(false);
        if (ForgottenApprovals(settings, keptSourceIds).Count == 0)
        {
            return;
        }

        int forgotten = 0;
        await _configManager.MergeSettingAsync(current =>
        {
            foreach (string profileId in ForgottenApprovals(current, keptSourceIds))
            {
                current.SharedPostConnectApprovals.Remove(profileId);
                forgotten++;
            }
        }).ConfigureAwait(false);
        FileLogger.Info($"Post-connect approvals of {forgotten} shared session(s) of removed sources forgotten.");
    }

    private static List<string> ForgottenApprovals(AppSettings settings, IReadOnlySet<string> keptSourceIds)
    {
        if (settings.SharedPostConnectApprovals is not { Count: > 0 } approvals)
        {
            return [];
        }

        HashSet<string> listed = (settings.SharedSessionSources ?? [])
            .Where(source => source is not null)
            .Select(source => source.Id)
            .ToHashSet(StringComparer.Ordinal);
        return [.. approvals.Keys.Where(profileId =>
            SharedSessionIds.SourceIdOf(profileId) is { } sourceId
            && !keptSourceIds.Contains(sourceId)
            && !listed.Contains(sourceId))];
    }
}
