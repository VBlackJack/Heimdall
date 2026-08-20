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

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.Core.Configuration;

namespace Heimdall.App.Tests;

/// <summary>
/// Freezes the fact that editing a profile cannot quietly delete part of it.
/// </summary>
/// <remarks>
/// <para>The server dialog composes a fresh <see cref="ServerProfileDto"/> and the caller assigns
/// it over the stored record. A field the dialog does not write is therefore not left alone: it is
/// replaced by the default. Four were being lost that way, and nothing failed, because the dialog's
/// projection and the profile's own field set are two independent statements of what a profile is
/// and each was consistent with itself.</para>
/// <para>The census below is over the type, not over a list written here, so a field added to
/// <see cref="ServerProfileDto"/> tomorrow has to be placed deliberately rather than forgotten.
/// </para>
/// </remarks>
public sealed class ServerDialogProfileFidelityTests
{
    /// <summary>
    /// Properties the dialog is allowed to write neither as its own nor as carried-forward, each
    /// with the reason it is exempt.
    /// </summary>
    private static readonly Dictionary<string, string> NotTheDialogsToWrite = new()
    {
        ["Id"] =
            "assigned by every caller after the dialog returns: a fresh one when adding or "
            + "duplicating, the original one when editing. Carrying the seed's identity through a "
            + "duplicate would produce two profiles claiming to be the same record.",
    };

    [Fact]
    public void EveryProfileFieldIsEitherEditedByTheDialogOrCarriedForward()
    {
        IReadOnlyList<PropertyInfo> properties = SettableProfileProperties();
        string source = ReadDialogSource();
        IReadOnlySet<string> written = WrittenPropertyNames(source);

        List<string> unplaced = [];
        int obsoleteShims = 0;
        foreach (PropertyInfo property in properties)
        {
            // An obsolete property is a one-way migration shim whose setter feeds the real field.
            // New code writing it would be writing the thing it replaced, so its absence from the
            // dialog is the correct outcome rather than an oversight. Exempted structurally so the
            // shims can be removed without anyone having to remember a list.
            if (property.GetCustomAttribute<System.ObsoleteAttribute>() is not null)
            {
                obsoleteShims++;
                continue;
            }

            if (written.Contains(property.Name) || NotTheDialogsToWrite.ContainsKey(property.Name))
            {
                continue;
            }

            unplaced.Add(
                $"ServerProfileDto.{property.Name} is neither assigned by ToDto nor carried forward "
                    + "by CarryForwardUneditedFields. Editing a profile will reset it to its "
                    + "default. Add it to whichever of the two is correct, or exempt it in "
                    + nameof(NotTheDialogsToWrite) + " with the reason.");
        }

        Assert.True(unplaced.Count == 0, string.Join("\n", unplaced));

        // Guarding that exemption: if the shims are ever removed this drops to zero and the branch
        // above becomes dead, which should be noticed rather than left in place forever.
        Assert.True(
            obsoleteShims > 0,
            "no obsolete shim was exempted, so that branch no longer covers anything and should go");
    }

    // Guarding the guard, twice. A census that reflected nothing, or that read a file whose
    // assignments it could not parse, would report success having placed nothing at all.
    [Fact]
    public void TheCensusActuallyCensusesSomething()
    {
        IReadOnlyList<PropertyInfo> properties = SettableProfileProperties();
        Assert.True(
            properties.Count > 80,
            $"only {properties.Count} settable properties found on ServerProfileDto");

        IReadOnlySet<string> written = WrittenPropertyNames(ReadDialogSource());
        int matched = properties.Count(property => written.Contains(property.Name));
        Assert.True(
            matched > 80,
            $"the source scan matched only {matched} of {properties.Count} properties, so it is "
                + "no longer reading the assignments it thinks it is");
    }

    // Every exemption must name a real property, or the allowlist is quietly excusing nothing while
    // looking like it excuses something.
    [Fact]
    public void EveryExemptionNamesARealProperty()
    {
        IReadOnlyList<PropertyInfo> properties = SettableProfileProperties();
        foreach (KeyValuePair<string, string> exemption in NotTheDialogsToWrite)
        {
            Assert.True(
                properties.Any(property => property.Name == exemption.Key),
                $"'{exemption.Key}' is exempted but is not a settable ServerProfileDto property");
            Assert.False(string.IsNullOrWhiteSpace(exemption.Value));
        }
    }

    // The behavioural half. The census proves a field is mentioned; this proves the four that were
    // being lost actually survive an edit, with values a default cannot be mistaken for.
    [Fact]
    public void EditingAProfileKeepsTheFieldsTheDialogDoesNotShow()
    {
        ServerProfileDto stored = new()
        {
            Id = "profile-under-test",
            DisplayName = "Edit me",
            ConnectionType = "CITRIX",
            RemoteServer = "host.example.test",
            SortOrder = 41,
            TunnelsPanelExpanded = true,
            CitrixLaunchCommandLine = "\"C:/Program Files/Citrix/SelfServicePlugin.exe\" -qlaunch app",
        };

        stored.ExtensionData["ASettingFromANewerVersion"] =
            JsonDocument.Parse("\"keep me\"").RootElement.Clone();

        ServerProfileDto edited = ServerDialogViewModel.FromDto(stored).ToDto();

        Assert.Equal(41, edited.SortOrder);
        Assert.True(edited.TunnelsPanelExpanded);
        Assert.Equal(stored.CitrixLaunchCommandLine, edited.CitrixLaunchCommandLine);
        Assert.True(edited.ExtensionData.ContainsKey("ASettingFromANewerVersion"));
        Assert.Equal("keep me", edited.ExtensionData["ASettingFromANewerVersion"].GetString());
    }

    // Carrying forward must not become inheriting. A dialog composing a new profile has no seed,
    // and must not acquire one of these values from anywhere.
    [Fact]
    public void ComposingANewProfileCarriesNothingForward()
    {
        ServerProfileDto composed = new ServerDialogViewModel { DisplayName = "New" }.ToDto();

        Assert.Equal(0, composed.SortOrder);
        Assert.Null(composed.TunnelsPanelExpanded);
        Assert.Null(composed.CitrixLaunchCommandLine);
        Assert.Empty(composed.ExtensionData);
    }

    // The extension data has to arrive detached, or the edited profile shares a dictionary with the
    // record it was seeded from and a later write to one is visible through the other.
    [Fact]
    public void CarriedExtensionDataIsNotSharedWithTheStoredRecord()
    {
        ServerProfileDto stored = new() { Id = "x", DisplayName = "x", ConnectionType = "SSH" };
        stored.ExtensionData["Original"] = JsonDocument.Parse("1").RootElement.Clone();

        ServerProfileDto edited = ServerDialogViewModel.FromDto(stored).ToDto();
        edited.ExtensionData["AddedAfterTheEdit"] = JsonDocument.Parse("2").RootElement.Clone();

        Assert.False(stored.ExtensionData.ContainsKey("AddedAfterTheEdit"));
    }

    private static IReadOnlyList<PropertyInfo> SettableProfileProperties()
        => [.. typeof(ServerProfileDto)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.CanWrite && property.SetMethod is { IsPublic: true })
            .OrderBy(property => property.Name, System.StringComparer.Ordinal)];

    private static IReadOnlySet<string> WrittenPropertyNames(string source)
    {
        // Both the object initializer inside ToDto and the assignments in the carry-forward helper
        // read as "Name =" at the start of a trimmed line; "dto.Name =" covers the second form.
        HashSet<string> names = new(System.StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(source, @"(?m)^\s*(?:dto\.)?([A-Z]\w*)\s*="))
        {
            names.Add(match.Groups[1].Value);
        }

        return names;
    }

    private static string ReadDialogSource()
    {
        string path = Path.Combine(
            FindRepoRoot(),
            "src", "Heimdall.App", "ViewModels", "Dialogs", "ServerDialogViewModel.cs");
        Assert.True(File.Exists(path), $"Dialog source not found: {path}");
        return File.ReadAllText(path);
    }

    private static string FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "Heimdall.slnx")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new DirectoryNotFoundException(
            $"Cannot find repository root containing Heimdall.slnx from test binary directory: {AppContext.BaseDirectory}");
    }
}
