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

namespace Heimdall.App.Services.SharedSessions;

/// <summary>What happened to a shared source on the last load.</summary>
public enum SharedSessionSourceStatus
{
    /// <summary>The source is turned off; nothing was read.</summary>
    Disabled,

    /// <summary>The file was read and its sessions are held.</summary>
    Loaded,

    /// <summary>The source could not be read; <see cref="SharedSessionSourceState.ErrorMessage"/> says why.</summary>
    Error,
}

/// <summary>
/// One shared source as of the last load.
/// </summary>
/// <param name="SourceId">The source identifier.</param>
/// <param name="Name">The name the user gave the source.</param>
/// <param name="ExpandedPath">The path read, environment variables expanded.</param>
/// <param name="Status">What happened.</param>
/// <param name="ErrorMessage">The localized reason, when <paramref name="Status"/> is an error.</param>
/// <param name="ProfileCount">How many shared profiles the source holds.</param>
/// <param name="GatewayCount">How many shared gateways the source holds.</param>
/// <param name="Warnings">What the parser could not take as it is.</param>
/// <param name="DroppedProfileCount">How many parsed sessions the sanitizer refused.</param>
public sealed record SharedSessionSourceState(
    string SourceId,
    string Name,
    string ExpandedPath,
    SharedSessionSourceStatus Status,
    string? ErrorMessage,
    int ProfileCount,
    int GatewayCount,
    IReadOnlyList<MobaXtermImportWarning> Warnings,
    int DroppedProfileCount);
