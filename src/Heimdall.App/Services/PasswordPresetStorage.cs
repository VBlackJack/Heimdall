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

using Heimdall.App.ViewModels.Tools;
using Heimdall.Core.Logging;
using Heimdall.Core.Security;

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
}

internal interface IPasswordPresetStorage
{
    PasswordGeneratorStore Load();
    void Save(PasswordGeneratorStore store);
}

internal sealed class PasswordPresetStorage : IPasswordPresetStorage
{
    private const string PresetsFileName = "password-presets.json";
    private const char ByteOrderMark = (char)0xFEFF;
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
                return new PasswordGeneratorStore();
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
            FileLogger.Warn($"[PasswordGenerator] Failed to read the saved presets: {ex.Message}");
            return new PasswordGeneratorStore();
        }
    }

    public void Save(PasswordGeneratorStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(store, new JsonSerializerOptions { WriteIndented = true });

            // Sealed the way a session's password is: the vault's key when it is unlocked, the
            // Windows account's own otherwise. A preset holds no password, but it does describe
            // the shape of the ones this person makes, and the leet mode's base word is whatever
            // they typed.
            File.WriteAllText(_filePath, CredentialProtector.Protect(json), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"[PasswordGenerator] Failed to save custom presets: {ex.Message}");
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
