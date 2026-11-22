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

/// <summary>What <c>MainViewModel.OpenToolTabAsync</c> did with a request to open a tool.</summary>
public enum ToolOpenOutcome
{
    /// <summary>A new tool tab was created (or the unknown-tool placeholder was shown).</summary>
    Opened,

    /// <summary>An already open tab of the tool was brought to the front.</summary>
    FocusedExisting,

    /// <summary>The tool is switched off or forbidden: nothing was opened or focused and the user was told why.</summary>
    Refused,
}
