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

using Heimdall.Core.Updates;

namespace Heimdall.App.Services;

/// <summary>
/// Exposes the running application version in the forms the app needs: the raw
/// informational string (for display), the parsed <see cref="HeimdallVersion"/>,
/// and the build date derived from the canonical "YYYY.MMDDxx" form.
/// </summary>
public interface IAppVersionProvider
{
    /// <summary>The raw AssemblyInformationalVersion (may carry a "+sha" suffix), or "unknown".</summary>
    string InformationalVersion { get; }

    /// <summary>The parsed version, or null when the informational string is not a valid Heimdall version.</summary>
    HeimdallVersion? Current { get; }

    /// <summary>The build date derived from the "YYYY.MMDDxx" form, or null when not derivable.</summary>
    DateOnly? BuildDate { get; }
}
