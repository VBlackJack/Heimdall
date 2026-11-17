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

namespace Heimdall.Terminal;

/// <summary>
/// A latched signal that a session has read its first output, for observers that only need to
/// know the process produced something and must not consume the output replay.
/// </summary>
public interface IFirstOutputSignal
{
    /// <summary>
    /// Raised once, when the first chunk of output (standard output or standard error) has been
    /// read and then delivered or buffered for the data event. A handler added after that moment
    /// is invoked at once, inside the add. An exit or a dispose does not raise it in itself; only
    /// output that was read does. Subscribing does not touch the data event or its replay.
    /// </summary>
    event Action? FirstOutputReceived;
}
