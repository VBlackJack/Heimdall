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

namespace Heimdall.Core.Configuration;

/// <summary>
/// Strips imported profile fields that must only be populated by trusted
/// local discovery or scanner flows before profiles cross into persisted
/// application configuration, and resets local-execution trust on import.
/// </summary>
public static class ImportedProfileSanitizer
{
    /// <summary>
    /// Clears fields that are intended to be produced only by trusted local
    /// scanners and must not cross the trust boundary of an external import,
    /// and clears any imported local-execution trust assertion.
    /// New rules can be added here as additional trust-boundary fields are identified.
    /// </summary>
    public static void Sanitize(IList<ServerProfileDto> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);

        foreach (var profile in profiles)
        {
            if (profile is null)
            {
                continue;
            }

            profile.CitrixLaunchCommandLine = null;
            profile.ExecutionConfirmed = false;
            profile.WinRmSkipCertificateCheck =
                profile.WinRmUseSsl && profile.WinRmSkipCertificateCheck;
        }
    }
}
