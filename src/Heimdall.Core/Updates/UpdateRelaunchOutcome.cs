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

namespace Heimdall.Core.Updates;

/// <summary>What the previous update attempt turned out to have done.</summary>
public enum UpdateRelaunchOutcome
{
    /// <summary>No attempt was pending. The ordinary case, and it says nothing.</summary>
    None,

    /// <summary>The running version is the one that was attempted. Also says nothing.</summary>
    Succeeded,

    /// <summary>
    /// An attempt was made and the version did not move. The application came back as
    /// the version it already was.
    /// </summary>
    NotApplied,
}
