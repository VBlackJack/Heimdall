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

namespace TwinShell.Core.Helpers;

/// <summary>
/// The public ids of the shipped catalogue, the same on every install.
/// </summary>
/// <remarks>
/// A shipped action and its templates used to take a random public id per install, so the
/// same seed action was a different entry on each teammate's machine and a sync folder
/// carried one copy of the catalogue per install. The id is now a name-based UUID (version 5,
/// RFC 4122) of the seed id, which every install derives alike.
/// </remarks>
public static class SeedPublicIds
{
    /// <summary>The namespace of every shipped public id. Never change it: ids already stored derive from it.</summary>
    public static readonly Guid Namespace = new("6f1c2a7e-3b84-4d5f-9a0e-2c7d8b1e4f93");

    private const string ActionNamePrefix = "action:";
    private const string TemplateNamePrefix = "template:";

    private const int GuidByteCount = 16;
    private const int VersionByteIndex = 6;
    private const int VariantByteIndex = 8;
    private const byte VersionMask = 0x0F;
    private const byte Version5Bits = 0x50;
    private const byte VariantMask = 0x3F;
    private const byte Rfc4122VariantBits = 0x80;

    /// <summary>The public id of the shipped action whose seed id is <paramref name="actionId"/>.</summary>
    public static Guid ForAction(string actionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actionId);
        return CreateVersion5(Namespace, ActionNamePrefix + actionId);
    }

    /// <summary>The public id of the shipped template whose seed id is <paramref name="templateId"/>.</summary>
    public static Guid ForTemplate(string templateId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(templateId);
        return CreateVersion5(Namespace, TemplateNamePrefix + templateId);
    }

    /// <summary>A name-based UUID, version 5 (SHA-1), as RFC 4122 section 4.3 defines it.</summary>
    public static Guid CreateVersion5(Guid namespaceId, string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        byte[] namespaceBytes = namespaceId.ToByteArray();
        SwapToNetworkOrder(namespaceBytes);
        byte[] nameBytes = Encoding.UTF8.GetBytes(name);
        byte[] input = new byte[namespaceBytes.Length + nameBytes.Length];
        namespaceBytes.CopyTo(input, 0);
        nameBytes.CopyTo(input, namespaceBytes.Length);

        byte[] hash = SHA1.HashData(input);
        byte[] uuid = new byte[GuidByteCount];
        Array.Copy(hash, uuid, GuidByteCount);
        uuid[VersionByteIndex] = (byte)((uuid[VersionByteIndex] & VersionMask) | Version5Bits);
        uuid[VariantByteIndex] = (byte)((uuid[VariantByteIndex] & VariantMask) | Rfc4122VariantBits);
        SwapToNetworkOrder(uuid);
        return new Guid(uuid);
    }

    /// <summary>
    /// Swaps the first three fields between <see cref="Guid"/>'s little-endian layout and the
    /// network order the RFC hashes; the swap is its own inverse.
    /// </summary>
    private static void SwapToNetworkOrder(byte[] bytes)
    {
        (bytes[0], bytes[3]) = (bytes[3], bytes[0]);
        (bytes[1], bytes[2]) = (bytes[2], bytes[1]);
        (bytes[4], bytes[5]) = (bytes[5], bytes[4]);
        (bytes[6], bytes[7]) = (bytes[7], bytes[6]);
    }
}
