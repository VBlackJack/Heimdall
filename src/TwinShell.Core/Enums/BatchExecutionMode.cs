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
/// Defines how batch execution should handle errors
/// </summary>
public enum BatchExecutionMode
{
    /// <summary>
    /// Stop execution when any command fails (ExitCode != 0)
    /// </summary>
    StopOnError,

    /// <summary>
    /// Continue executing remaining commands even if some fail
    /// </summary>
    ContinueOnError
}
