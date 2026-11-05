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

namespace Heimdall.App.ViewModels.Settings;

/// <summary>
/// A <c>[NAME]</c> variable the enabled shared sources use and the MobaXterm variables list has no
/// row for: what the variables card offers to add.
/// </summary>
/// <param name="Name">The variable, spelled as the first source that uses it writes it.</param>
/// <param name="Sources">The names of the sources that use it, in order.</param>
/// <param name="AddLabel">The text of the button that adds its row.</param>
/// <param name="AddAccessibleName">What assistive technology announces for that button.</param>
public sealed record MissingMobaXtermVariable(
    string Name,
    IReadOnlyList<string> Sources,
    string AddLabel,
    string AddAccessibleName);
