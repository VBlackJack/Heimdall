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

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Heimdall.Core.Models;

namespace Heimdall.Core.Configuration;

/// <summary>
/// A hash of what a profile's post-connect steps would make a session do, so an approval can be
/// tied to the exact commands that were approved.
/// </summary>
/// <remarks>
/// Covers, in order, everything that decides what runs: the text typed, the library entry and its
/// parameters, whether the step is enabled, and what a failure does. The step identifier is left
/// out: a shared file mints it afresh on every read, and an approval that a re-read invalidated
/// would ask again for commands that never changed.
/// </remarks>
public static class PostConnectFingerprint
{
    private const string Version = "v1:";

    /// <summary>The fingerprint of <paramref name="steps"/>.</summary>
    public static string Compute(IEnumerable<PostConnectStep?>? steps)
    {
        var canonical = (steps ?? [])
            .Where(step => step is not null)
            .Select(step => new
            {
                step!.Input,
                step.CommandLibraryId,
                Parameters = step.CommandLibraryParams?
                    .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => new[] { pair.Key, pair.Value })
                    .ToArray(),
                step.Enabled,
                OnFailure = step.OnFailure.ToString(),
            })
            .ToArray();

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(canonical)));
        return Version + Convert.ToHexStringLower(hash);
    }
}
