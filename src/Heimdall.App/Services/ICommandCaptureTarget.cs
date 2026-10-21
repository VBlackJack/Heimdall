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
/// A tool tab that can take a command captured from a terminal without being opened again.
/// </summary>
/// <remarks>
/// Implemented by the Command Library view, so that a capture made while a library tab is open
/// lands in that tab rather than in a second one.
/// </remarks>
public interface ICommandCaptureTarget
{
    /// <summary>Opens the editor for a new action prefilled with <paramref name="capture"/>.</summary>
    void PrefillNewAction(CapturedCommand capture);
}
