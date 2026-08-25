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

namespace Heimdall.App.Services;

/// <summary>
/// Applies an edited gateway to a freshly loaded settings object.
/// </summary>
/// <remarks>
/// Extracted so the decision can be measured without a window: the edit dialog is modal, so
/// the caller's snapshot could be minutes old by the time the user pressed Save, and writing
/// that snapshot back erased everything persisted meanwhile. The position is therefore
/// resolved against the list handed in - which the caller must obtain inside
/// <see cref="IConfigManager.MergeSettingAsync"/> - never against the snapshot the dialog was
/// populated from.
/// </remarks>
public static class GatewayEditCommit
{
    /// <summary>
    /// Replaces the gateway carrying <paramref name="gatewayId"/> with
    /// <paramref name="updated"/>.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when the gateway was found and replaced; <see langword="false"/>
    /// when it is no longer there, which is the case where another surface deleted it while
    /// the dialog was open. Not finding it is not an error: the edit is dropped rather than
    /// resurrecting a gateway someone removed on purpose.
    /// </returns>
    public static bool Apply(AppSettings settings, string gatewayId, SshGatewayDto updated)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(updated);
        ArgumentException.ThrowIfNullOrWhiteSpace(gatewayId);

        int index = settings.SshGateways.FindIndex(
            gateway => string.Equals(gateway.Id, gatewayId, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return false;
        }

        updated.Id = settings.SshGateways[index].Id;
        settings.SshGateways[index] = updated;
        return true;
    }
}
