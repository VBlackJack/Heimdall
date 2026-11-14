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

using System.Runtime.Versioning;
using System.Text.Json;
using Heimdall.Core.Configuration;
using Heimdall.Core.Logging;

namespace Heimdall.Core.Security.Vault;

/// <summary>
/// What is needed to bring the vault back if turning it off does not finish: the wrapped key and
/// the Windows Hello enrolment, exactly as settings hold them today (no new secret form).
/// </summary>
/// <remarks>
/// <para>Written atomically with the restricted-ACL writer before the wrapped key is erased from
/// settings, and deleted only once the erase has been checked and is final. If anything fails after
/// the erase (a store, the settings file, a crash, a logoff, a power cut) the journal is still
/// there, and the next start restores the vault from it before the vault state is read: the
/// application never starts as disabled while a journal exists.</para>
/// <para>When the restore cannot be written, the journal stays and <see cref="ApplyTo"/> lets the
/// start proceed as an enabled vault in memory, so the unlock gate still works.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class VaultDisableJournal
{
    /// <summary>The name of the journal file, beside the settings.</summary>
    public const string FileName = "vault-disable.journal";

    private const int CurrentFormatVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    /// <summary>The format version of the file.</summary>
    public int FormatVersion { get; set; } = CurrentFormatVersion;

    /// <summary>The wrapped data-encryption key, as settings hold it.</summary>
    public string? WrappedDek { get; set; }

    /// <summary>The vault identifier.</summary>
    public string? VaultId { get; set; }

    /// <summary>When the vault was created.</summary>
    public string? CreatedAt { get; set; }

    /// <summary>Whether Windows Hello was enrolled.</summary>
    public bool HelloEnrolled { get; set; }

    /// <summary>The Hello-wrapped key.</summary>
    public string? HelloWrappedDek { get; set; }

    /// <summary>The Hello challenge.</summary>
    public string? HelloChallenge { get; set; }

    /// <summary>The Hello salt.</summary>
    public string? HelloSalt { get; set; }

    /// <summary>The Hello credential name.</summary>
    public string? HelloCredentialName { get; set; }

    /// <summary>The Hello public key hash.</summary>
    public string? HelloPublicKeyHash { get; set; }

    /// <summary>The path of the journal beside the settings of <paramref name="configManager"/>.</summary>
    public static string PathFor(IConfigManager configManager)
    {
        ArgumentNullException.ThrowIfNull(configManager);
        string directory = Path.GetDirectoryName(Path.GetFullPath(configManager.SettingsPath)) ?? string.Empty;
        return Path.Combine(directory, FileName);
    }

    /// <summary>Captures the vault state of <paramref name="settings"/>.</summary>
    public static VaultDisableJournal From(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new VaultDisableJournal
        {
            WrappedDek = settings.VaultWrappedDek,
            VaultId = settings.VaultId,
            CreatedAt = settings.VaultCreatedAt,
            HelloEnrolled = settings.VaultHelloEnrolled,
            HelloWrappedDek = settings.VaultHelloWrappedDek,
            HelloChallenge = settings.VaultHelloChallenge,
            HelloSalt = settings.VaultHelloSalt,
            HelloCredentialName = settings.VaultHelloCredentialName,
            HelloPublicKeyHash = settings.VaultHelloPublicKeyHash,
        };
    }

    /// <summary>Writes the journal atomically with the restricted ACL.</summary>
    public static async Task WriteAsync(IConfigManager configManager, AppSettings snapshot)
    {
        string json = JsonSerializer.Serialize(From(snapshot), JsonOptions);
        await SecureFileWriter.WriteAllTextAtomicAsync(PathFor(configManager), json).ConfigureAwait(false);
    }

    /// <summary>Reads the journal, or null when there is none or it cannot be read.</summary>
    public static VaultDisableJournal? TryRead(IConfigManager configManager)
    {
        string path = PathFor(configManager);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var journal = JsonSerializer.Deserialize<VaultDisableJournal>(File.ReadAllText(path), JsonOptions);
            return journal is { FormatVersion: CurrentFormatVersion } && !string.IsNullOrEmpty(journal.WrappedDek)
                ? journal
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            FileLogger.Warn($"Vault: the disable journal could not be read ({ex.GetType().Name}).");
            return null;
        }
    }

    /// <summary>Deletes the journal; throws when it cannot be deleted.</summary>
    public static void Delete(IConfigManager configManager)
    {
        string path = PathFor(configManager);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    /// <summary>Whether a journal is on disk.</summary>
    public static bool Exists(IConfigManager configManager) => File.Exists(PathFor(configManager));

    /// <summary>Copies the journal into <paramref name="settings"/>, in memory only, as an enabled vault.</summary>
    public void ApplyTo(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.VaultWrappedDek = WrappedDek;
        settings.VaultEnabled = true;
        settings.VaultMigrationState = VaultMigrationState.InProgress;
        settings.VaultCreatedAt = CreatedAt;
        settings.VaultId = VaultId;
        settings.VaultHelloEnrolled = HelloEnrolled;
        settings.VaultHelloWrappedDek = HelloWrappedDek;
        settings.VaultHelloChallenge = HelloChallenge;
        settings.VaultHelloSalt = HelloSalt;
        settings.VaultHelloCredentialName = HelloCredentialName;
        settings.VaultHelloPublicKeyHash = HelloPublicKeyHash;
    }

    /// <summary>
    /// Brings the vault back from a journal left by a disable that did not finish: writes the state
    /// into settings with the in-progress marker, then deletes the journal, in that order.
    /// </summary>
    /// <returns>
    /// Null when there was no journal or it was restored; the journal itself when the restore could
    /// not be written and the caller must start as enabled in memory (<see cref="ApplyTo"/>).
    /// </returns>
    public static async Task<VaultDisableJournal?> RecoverAsync(IConfigManager configManager)
    {
        VaultDisableJournal? journal = TryRead(configManager);
        if (journal is null)
        {
            return null;
        }

        try
        {
            await configManager.MergeSettingAsync(journal.ApplyTo).ConfigureAwait(false);
            Delete(configManager);
            FileLogger.Info("Vault: an interrupted disable was undone; the master password is on.");
            return null;
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"Vault: the interrupted disable could not be undone yet ({ex.GetType().Name}).");
            return journal;
        }
    }
}
