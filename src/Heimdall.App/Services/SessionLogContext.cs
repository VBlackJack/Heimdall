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
/// Immutable description of a session whose terminal output is being recorded.
/// </summary>
/// <param name="SessionKey">
/// Stable identifier used to route subsequent <c>WriteOutput</c>/<c>StopSession</c> calls.
/// Not part of the log file name.
/// </param>
/// <param name="Protocol">Protocol label (e.g. <c>SSH</c>, <c>TELNET</c>); used in the file name and header.</param>
/// <param name="Host">Target host or display host; used in the file name and header.</param>
/// <param name="DisplayTitle">Human-readable session title shown in the header.</param>
/// <param name="StartedUtc">UTC instant the session started; used for the file name timestamp and header.</param>
public sealed record SessionLogContext(
    string SessionKey,
    string Protocol,
    string Host,
    string DisplayTitle,
    DateTime StartedUtc);
