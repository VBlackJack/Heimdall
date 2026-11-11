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

namespace Heimdall.App.Services;

/// <summary>
/// Marks a tool whose unsaved work is kept somewhere and offered again at the next start.
/// </summary>
/// <remarks>
/// Closing a single tab still asks such a tool. Exiting the application does not, because a
/// prompt there could only stop an exit for work that is not lost.
/// </remarks>
public interface IRecoverableUnsavedWork
{
}
