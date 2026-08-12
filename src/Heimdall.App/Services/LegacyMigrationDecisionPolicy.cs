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

using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Heimdall.Core.Configuration;

namespace Heimdall.App.Services;

internal sealed record LegacyMigrationOffer(int Version, string SourceFingerprint);

/// <summary>
/// Versions legacy migration offers and identifies their source inventory without
/// retaining legacy paths or content.
/// </summary>
internal static class LegacyMigrationDecisionPolicy
{
    internal const int CurrentOfferVersion = 1;

    private const int HashBufferSize = 81920;
    private static readonly byte[] FingerprintDomain =
        Encoding.UTF8.GetBytes("Heimdall.LegacyMigrationOffer.v1");

    internal static async Task<LegacyMigrationOffer> CreateOfferAsync(
        string legacyPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyPath);

        string configPath = Path.Combine(
            legacyPath,
            AppConstants.BundledConfigDirectoryName);
        string settingsPath = Path.Combine(configPath, "settings.json");
        string serversPath = Path.Combine(configPath, "servers.json");

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(FingerprintDomain);
        await AppendFileAsync(hash, settingsPath, cancellationToken);
        await AppendFileAsync(hash, serversPath, cancellationToken);
        return new LegacyMigrationOffer(
            CurrentOfferVersion,
            Convert.ToHexString(hash.GetHashAndReset()));
    }

    internal static bool ShouldOffer(AppSettings settings, LegacyMigrationOffer offer)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(offer);
        return settings.LegacyMigrationDeclinedOfferVersion < offer.Version
            || !string.Equals(
                settings.LegacyMigrationDeclinedSourceFingerprint,
                offer.SourceFingerprint,
                StringComparison.Ordinal);
    }

    internal static bool HasDeclineMarker(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.LegacyMigrationDeclinedOfferVersion > 0
            || !string.IsNullOrWhiteSpace(
                settings.LegacyMigrationDeclinedSourceFingerprint);
    }

    internal static Task RecordDeclineAsync(
        IConfigManager configManager,
        LegacyMigrationOffer offer)
    {
        ArgumentNullException.ThrowIfNull(configManager);
        ArgumentNullException.ThrowIfNull(offer);
        return configManager.MergeSettingAsync(settings =>
        {
            settings.LegacyMigrationDeclinedOfferVersion = offer.Version;
            settings.LegacyMigrationDeclinedSourceFingerprint = offer.SourceFingerprint;
        });
    }

    internal static Task ClearDeclineAsync(IConfigManager configManager)
    {
        ArgumentNullException.ThrowIfNull(configManager);
        return configManager.MergeSettingAsync(settings =>
        {
            settings.LegacyMigrationDeclinedOfferVersion = 0;
            settings.LegacyMigrationDeclinedSourceFingerprint = null;
        });
    }

    private static async Task AppendFileAsync(
        IncrementalHash hash,
        string path,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            HashBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        byte[] lengthBytes = new byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(lengthBytes, stream.Length);
        hash.AppendData(lengthBytes);

        byte[] buffer = new byte[HashBufferSize];
        int bytesRead;
        while ((bytesRead = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            hash.AppendData(buffer, 0, bytesRead);
        }
    }
}
