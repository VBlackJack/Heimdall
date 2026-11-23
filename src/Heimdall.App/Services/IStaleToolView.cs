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
/// A tool view that can outlive what it was built from. Opening the tool again must not focus such
/// a tab as if it were the fresh tool: it keeps what it was opened with, and the thing it was
/// opened with was replaced or removed.
/// </summary>
public interface IStaleToolView
{
    /// <summary>True when the view no longer matches what is imported; evaluated when read.</summary>
    bool IsStale { get; }
}
