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

namespace Heimdall.Rdp.Display;

public enum MultimonFallbackReason
{
    None = 0,
    SingleMonitorHost,
    InvalidMonitorIndex,

    /// <summary>
    /// The selected monitors do not form one connected block, so the session would be describing
    /// two islands and would be sized for the gap between them.
    /// </summary>
    NonContiguousSelection
}

public sealed record RdpDisplaySettings(
    RdpResolutionMode ResolutionMode,
    bool UseMultimon,
    IReadOnlyList<int> SelectedMonitorIndices);

public sealed record RdpMultimonValidation(
    bool ShouldFallback,
    MultimonFallbackReason Reason,
    RdpDisplaySettings CoercedSettings);
