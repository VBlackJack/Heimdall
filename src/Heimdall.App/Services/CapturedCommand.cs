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

using Heimdall.Core.Configuration;

namespace Heimdall.App.Services;

/// <summary>
/// A command captured from a terminal, ready to prefill the Command Library editor.
/// </summary>
/// <param name="Text">The command line, already joined to one line.</param>
/// <param name="Platform">
/// <see cref="RemoteOs.Windows"/> or <see cref="RemoteOs.Linux"/>: the pattern the text is
/// written into. Never <see cref="RemoteOs.Auto"/> when built by <see cref="CommandCapture"/>.
/// </param>
/// <param name="Tag">The session's connection type in lower case, or null when it has none.</param>
public sealed record CapturedCommand(string Text, RemoteOs Platform, string? Tag);
