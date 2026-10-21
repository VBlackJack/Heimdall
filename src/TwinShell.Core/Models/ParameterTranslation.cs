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

namespace TwinShell.Core.Models;

/// <summary>
/// The translated text of one template parameter.
/// </summary>
/// <remarks>
/// Either half may be null: a missing half keeps the pivot text, field by field.
/// </remarks>
/// <param name="Label">The translated label, or null to keep the pivot label.</param>
/// <param name="Description">The translated help text, or null to keep the pivot text.</param>
public sealed record ParameterTranslation(string? Label, string? Description);
