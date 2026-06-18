/*
 * Copyright 2025 Julien Bombled
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

namespace TwinShell.Core.Enums;

/// <summary>
/// Represents the criticality level of an action
/// </summary>
public enum CriticalityLevel
{
    /// <summary>
    /// Informational command (read-only, no system changes)
    /// </summary>
    Info = 0,

    /// <summary>
    /// Execution command (may modify system state)
    /// </summary>
    Run = 1,

    /// <summary>
    /// Dangerous command (can cause significant system changes or data loss)
    /// </summary>
    Dangerous = 2
}
