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

using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Heimdall.App.ViewModels.Tools;
using Heimdall.Core.Logging;
using Heimdall.Core.Security;
using Heimdall.Core.Security.Vault;

namespace Heimdall.App.Services;

/// <summary>
/// Everything the password generator keeps between runs.
/// </summary>
/// <remarks>
/// The settings are kept apart from the presets because they answer different questions. A preset
/// is a thing the operator named and asked for; the settings are where the tool was left, and they
/// are only written down at all when the operator has said to.
/// </remarks>
internal sealed class PasswordGeneratorStore
{
    public List<PasswordGeneratorViewModel.PasswordPreset> Presets { get; set; } = [];

    /// <summary>Whether the tool reopens where it was left.</summary>
    public bool RememberSettings { get; set; }

    /// <summary>Where it was left, written only while <see cref="RememberSettings"/> is on.</summary>
    public PasswordGeneratorViewModel.PasswordPreset? Settings { get; set; }

    /// <summary>
    /// True when the file exists but could not be read or unsealed, so this store is an empty
    /// stand-in rather than what the file holds. A save is refused while it is set: writing the
    /// stand-in would replace the operator's real presets with nothing.
    /// </summary>
    [JsonIgnore]
    public bool IsUnreadable { get; init; }
}

internal interface IPasswordPresetStorage
{
    PasswordGeneratorStore Load();
    void Save(PasswordGeneratorStore store);

    /// <summary>Saves without holding the caller; a storage with no background path saves at once.</summary>
    Task SaveAsync(PasswordGeneratorStore store)
    {
        Save(store);
        return Task.CompletedTask;
    }
}

internal sealed class PasswordPresetStorage : IPasswordPresetStorage
{
    internal const string PresetsFileName = "password-presets.json";
    internal const char ByteOrderMark = (char)0xFEFF;
    private readonly string _filePath;

    /// <summary>Creates a storage rooted in the supplied directory.</summary>
    /// <param name="directoryPath">Where the preset file lives.</param>
    /// <remarks>
    /// <b>There is deliberately no parameterless constructor.</b> One existed and chained to
    /// <c>ApplicationDataPathResolver.Resolve()</c>, so any caller - a test included - could
    /// reach the operator's own preset file under <c>%LOCALAPPDATA%\Heimdall</c> by writing
    /// nothing at all. The production location is now resolved once, in the composition root,
    /// and reaching it any other way does not compile.
    /// </remarks>
    internal PasswordPresetStorage(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        _filePath = Path.Combine(directoryPath, PresetsFileName);
    }

    /// <summary>
    /// Reads the store, sealed or in either of the two shapes it has had.
    /// </summary>
    /// <remarks>
    /// <para>A file written before the settings were kept is a bare array of presets; a file
    /// written before the sealing is plain JSON. Both are read and both are written back in the
    /// current shape, so an operator upgrading loses neither their presets nor a run.</para>
    /// <para>The discriminator is the first character. A sealed blob is base64 or a versioned
    /// envelope and never opens on a brace or a bracket, so the test cannot mistake one for the
    /// other in either direction.</para>
    /// </remarks>
    public PasswordGeneratorStore Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return new PasswordGeneratorStore();
            }

            // The mark is written as a number rather than as an escape: a source file that
            // spells it in the usual way ends up carrying one, and the typography guard is
            // right to refuse that.
            var raw = File.ReadAllText(_filePath, Encoding.UTF8).TrimStart(ByteOrderMark).Trim();
            if (raw.Length == 0)
            {
                return new PasswordGeneratorStore();
            }

            var wasSealed = raw[0] is not ('{' or '[');
            var json = wasSealed ? CredentialProtector.Unprotect(raw) : raw;
            if (string.IsNullOrWhiteSpace(json))
            {
                FileLogger.Warn(
                    "[PasswordGenerator] The saved presets could not be unsealed and were left "
                    + "untouched. This happens when the file was written by another Windows "
                    + "account, or on another machine.");
                return new PasswordGeneratorStore { IsUnreadable = true };
            }

            var store = Parse(json);

            if (!wasSealed)
            {
                // Read in the clear once, written back sealed. The old file is overwritten rather
                // than left beside the new one, which would defeat the point of sealing it.
                Save(store);
            }

            return store;
        }
        catch (Exception ex)
        {
            FileLogger.Warn(
                $"[PasswordGenerator] Failed to read the saved presets, the file is left untouched: {ex.Message}");
            return new PasswordGeneratorStore { IsUnreadable = true };
        }
    }

    /// <summary>
    /// Saves and waits for the write. Kept for callers that need the file to be there on return.
    /// </summary>
    /// <remarks>
    /// The seal gate is entered on a pool thread, never by the caller itself, and nothing the
    /// exclusive side does waits on the UI thread, so the wait cannot become a cycle. The secret is
    /// sealed on the caller's thread, inside the scope, and the write lands before the scope ends.
    /// </remarks>
    public void Save(PasswordGeneratorStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        if (!TrySerialize(store, out string json))
        {
            return;
        }

        IDisposable? sealScope = null;
        try
        {
            sealScope = Task.Run(() => VaultSealGate.Shared.EnterAsync().AsTask()).GetAwaiter().GetResult();
            var content = CredentialProtector.Protect(json);
            Task.Run(() => WriteContentAsync(content)).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"[PasswordGenerator] Failed to save custom presets: {ex.Message}");
        }
        finally
        {
            sealScope?.Dispose();
        }
    }

    /// <summary>
    /// Saves without holding the caller: the seal gate and the file are entered on a pool thread,
    /// so a save started on the UI thread never blocks it on a disable that is draining writers.
    /// Failures are logged, as they always were; the unreadable-store protection is unchanged.
    /// </summary>
    public Task SaveAsync(PasswordGeneratorStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        return TrySerialize(store, out string json)
            ? Task.Run(() => WriteSealedAsync(json))
            : Task.CompletedTask;
    }

    /// <summary>
    /// Serializes the store now, on the caller's thread (it may change once this returns). False
    /// when nothing is to be written: an unreadable store is never written over the file.
    /// </summary>
    private bool TrySerialize(PasswordGeneratorStore store, out string json)
    {
        json = string.Empty;
        if (store.IsUnreadable)
        {
            FileLogger.Warn("[PasswordGenerator] The saved presets could not be read, so nothing is written over them.");
            return false;
        }

        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            json = JsonSerializer.Serialize(store, new JsonSerializerOptions { WriteIndented = true });
            return true;
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"[PasswordGenerator] Failed to save custom presets: {ex.Message}");
            return false;
        }
    }

    private async Task WriteSealedAsync(string json)
    {
        try
        {
            // Sealed the way a session's password is: the vault's key when it is unlocked, the
            // Windows account's own otherwise. A preset holds no password, but it does describe
            // the shape of the ones this person makes, and the leet mode's base word is whatever
            // they typed. The seal and the write are one scope for the vault: disabling it waits for
            // this save to land before it withdraws the key, so no blob sealed with that key is left.
            using IDisposable sealScope = await VaultSealGate.Shared.EnterAsync().ConfigureAwait(false);
            var content = CredentialProtector.Protect(json);
            await WriteContentAsync(content).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"[PasswordGenerator] Failed to save custom presets: {ex.Message}");
        }
    }

    /// <summary>
    /// The same atomic, restricted-ACL writer the migration uses: a reader or a crash never sees
    /// half a file.
    /// </summary>
    private async Task WriteContentAsync(string content)
    {
        var gate = PasswordPresetFileGate.For(_filePath);
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await SecureFileWriter.WriteAllTextAtomicAsync(_filePath, content).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Reads either shape: the object written since the settings were kept, or the bare array
    /// that came before it.
    /// </summary>
    private static PasswordGeneratorStore Parse(string json)
    {
        var trimmed = json.TrimStart();

        if (trimmed.StartsWith('['))
        {
            var presets = JsonSerializer
                .Deserialize<List<PasswordGeneratorViewModel.PasswordPreset>>(json);

            return new PasswordGeneratorStore { Presets = presets ?? [] };
        }

        var store = JsonSerializer.Deserialize<PasswordGeneratorStore>(json);
        store ??= new PasswordGeneratorStore();
        store.Presets ??= [];
        return store;
    }
}
