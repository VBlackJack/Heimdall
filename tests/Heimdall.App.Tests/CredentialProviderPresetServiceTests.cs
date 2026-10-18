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

namespace Heimdall.App.Tests;

/// <summary>
/// The built-in command presets of the external credential provider.
/// </summary>
public sealed class CredentialProviderPresetServiceTests
{
    private readonly CredentialProviderPresetService _service = new();

    [Fact]
    public void GetPresetLabels_TheCustomEntryIsLocalized()
    {
        var labels = _service.GetPresetLabels(key => $"<{key}>");

        Assert.Equal($"<{CredentialProviderPresetService.CustomLabelKey}>", labels[0]);
        Assert.DoesNotContain("Custom", labels);
    }

    [Fact]
    public void TryGetPreset_TheCustomEntryAppliesNothing()
    {
        Assert.False(_service.TryGetPreset(0, out string command, out bool firstLineOnly));
        Assert.Equal(string.Empty, command);
        Assert.False(firstLineOnly);
    }

    [Theory]
    [InlineData("KeePass2 (KPScript)", true)]
    [InlineData("pass (GPG)", true)]
    [InlineData("KeePassXC", false)]
    [InlineData("Bitwarden CLI", false)]
    [InlineData("1Password CLI", false)]
    public void TryGetPreset_ToolsThatPrintMoreThanTheValue_TurnFirstLineOnlyOn(string label, bool expected)
    {
        // KPScript prints "OK: ..." after the value and pass prints the notes after the password;
        // without "first line only" those lines were sent as part of the password.
        int index = IndexOf(label);

        Assert.True(_service.TryGetPreset(index, out _, out bool firstLineOnly));
        Assert.Equal(expected, firstLineOnly);
    }

    [Fact]
    public void TryGetPreset_OnePasswordLooksTheItemUpByName()
    {
        // "op read op://{Title}/password" named a vault called {Title} and an item called
        // "password", with no field: op rejects that reference.
        Assert.True(_service.TryGetPreset(IndexOf("1Password CLI"), out string command, out _));

        Assert.DoesNotContain("op://", command);
        Assert.StartsWith("op item get \"{Title}\"", command);
        Assert.Contains("--reveal", command);
    }

    [Fact]
    public void TryGetPreset_KpScriptFailsOnAMissingEntry()
    {
        // Without -FailIfNoEntry a missing entry is not an error, and the status line left on
        // line 1 would be read as the password.
        Assert.True(_service.TryGetPreset(IndexOf("KeePass2 (KPScript)"), out string command, out _));

        Assert.Contains("-FailIfNoEntry", command);
    }

    private int IndexOf(string label)
    {
        int index = _service.GetPresetLabels(key => key).ToList().IndexOf(label);
        Assert.True(index > 0, $"preset '{label}' not found");
        return index;
    }
}
