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
using Heimdall.Core.Logging;

namespace Heimdall.App.Services.SharedSessions;

/// <summary>
/// Whether the post-connect commands of a shared profile may run: asked the first time, and again
/// whenever the commands change in the shared file.
/// </summary>
/// <remarks>
/// <para>The approval is the <see cref="PostConnectFingerprint"/> of the commands, stored locally
/// under the shared profile's identifier in <see cref="AppSettings.SharedPostConnectApprovals"/>.
/// <see cref="ServerProfileDto.ExecutionConfirmed"/> stays false on the profile: it is rebuilt from
/// a file other people edit, and a flag on it would approve whatever that file says next.</para>
/// </remarks>
public sealed class SharedPostConnectTrust
{
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
        string fingerprint = PostConnectFingerprint.Compute(profile.PostConnectSteps);
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
}
