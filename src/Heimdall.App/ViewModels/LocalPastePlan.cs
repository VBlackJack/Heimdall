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

using Heimdall.App.Services;

namespace Heimdall.App.ViewModels;

/// <summary>The outcome of planning a local paste: what will be copied, and what was refused.</summary>
/// <param name="Roots">Each accepted root with its ordered operations.</param>
/// <param name="RefusedSelfTargets">Roots that are the destination or contain it.</param>
/// <param name="RefusedLinks">Roots that are reparse points, which are never copied through.</param>
/// <param name="Errors">Roots whose walk failed, with the message to show.</param>
internal sealed record LocalPastePlan(
    List<(string SourcePath, IReadOnlyList<LocalPasteOp> Operations)> Roots,
    List<string> RefusedSelfTargets,
    List<string> RefusedLinks,
    List<(string SourceName, string Message)> Errors);
