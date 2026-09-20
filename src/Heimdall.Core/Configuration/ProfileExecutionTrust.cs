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

using Heimdall.Core.Models;

namespace Heimdall.Core.Configuration;

/// <summary>
/// Evaluates whether a profile carries local-execution payload that must be explicitly trusted.
/// </summary>
public static class ProfileExecutionTrust
{
    /// <summary>
    /// Returns true when a LOCAL profile carries a custom local shell executable, arguments,
    /// working directory, or a request to run elevated.
    /// </summary>
    /// <remarks>
    /// The working directory and the elevation mode were outside this payload, so an imported
    /// profile could set both without the confirmation prompt ever appearing. Elevation still
    /// raises a UAC prompt of its own, but a consent dialog that arrives unannounced is the
    /// thing this gate exists to prevent.
    /// </remarks>
    public static bool CarriesLocalExecutionPayload(ServerProfileDto profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        return string.Equals(profile.ConnectionType, "LOCAL", StringComparison.OrdinalIgnoreCase)
            && (!string.IsNullOrWhiteSpace(profile.LocalShellExecutable)
                || !string.IsNullOrWhiteSpace(profile.LocalShellArguments)
                || !string.IsNullOrWhiteSpace(profile.LocalShellWorkingDirectory)
                || profile.EffectiveElevationMode != ElevationMode.None);
    }

    /// <summary>
    /// Returns true when a profile carries local-execution payload that has not been confirmed.
    /// </summary>
    public static bool RequiresExecutionConfirmation(ServerProfileDto profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        return CarriesLocalExecutionPayload(profile) && !profile.ExecutionConfirmed;
    }

    /// <summary>
    /// Returns true when a profile carries at least one enabled post-connect command.
    /// </summary>
    public static bool CarriesPostConnectPayload(ServerProfileDto profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (profile.PostConnectSteps is null)
        {
            return false;
        }

        foreach (PostConnectStep? step in profile.PostConnectSteps)
        {
            if (step is not null
                && step.Enabled
                && (!string.IsNullOrWhiteSpace(step.Input)
                    || !string.IsNullOrWhiteSpace(step.CommandLibraryId)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns true when a profile carries post-connect payload that has not been confirmed.
    /// </summary>
    public static bool RequiresPostConnectConfirmation(ServerProfileDto profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        return CarriesPostConnectPayload(profile) && !profile.ExecutionConfirmed;
    }
}
