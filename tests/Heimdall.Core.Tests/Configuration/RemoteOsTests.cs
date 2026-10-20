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

using System.Text.Json;
using System.Text.Json.Serialization;
using Heimdall.Core.Configuration;

namespace Heimdall.Core.Tests.Configuration;

/// <summary>
/// Pins the declared operating system of a profile: the rule that turns it into a platform, and
/// its survival through the two paths every profile takes, the JSON file and the clone.
/// </summary>
public sealed class RemoteOsTests
{
    // The options the configuration manager writes the inventory with.
    private static readonly JsonSerializerOptions InventoryOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    [Fact]
    public void ExplicitWindows_OverSsh_IsWindows()
    {
        Assert.Equal(RemoteOs.Windows, RemoteOsResolver.Resolve(RemoteOs.Windows, "SSH"));
    }

    [Fact]
    public void ExplicitLinux_OverLocal_IsLinux()
    {
        Assert.Equal(RemoteOs.Linux, RemoteOsResolver.Resolve(RemoteOs.Linux, "LOCAL"));
    }

    // Auto keeps the rule that predates the declaration, in any case of the protocol name.
    [Theory]
    [InlineData("SSH", RemoteOs.Linux)]
    [InlineData("ssh", RemoteOs.Linux)]
    [InlineData("LOCAL", RemoteOs.Windows)]
    [InlineData("Local", RemoteOs.Windows)]
    [InlineData("RDP", RemoteOs.Windows)]
    [InlineData("WINRM", RemoteOs.Windows)]
    [InlineData("VNC", RemoteOs.Auto)]
    [InlineData("", RemoteOs.Auto)]
    [InlineData(null, RemoteOs.Auto)]
    public void Auto_KeepsTheGuessFromTheProtocol(string? connectionType, RemoteOs expected)
    {
        Assert.Equal(expected, RemoteOsResolver.Resolve(RemoteOs.Auto, connectionType));
    }

    [Fact]
    public void ADeclaredOperatingSystem_RoundTripsThroughTheInventoryJson_AsAName()
    {
        ServerProfileDto source = new() { Id = "p", DisplayName = "P", ConnectionType = "SSH", RemoteOs = RemoteOs.Windows };

        string json = JsonSerializer.Serialize(source, InventoryOptions);
        ServerProfileDto read = JsonSerializer.Deserialize<ServerProfileDto>(json, InventoryOptions)!;

        Assert.Contains("\"remoteOs\": \"Windows\"", json, StringComparison.Ordinal);
        Assert.Equal(RemoteOs.Windows, read.RemoteOs);
    }

    // A profile written before the field existed, or by an importer, reads as Auto.
    [Fact]
    public void AProfileWithoutTheField_ReadsAsAuto()
    {
        ServerProfileDto read = JsonSerializer.Deserialize<ServerProfileDto>(
            "{\"id\":\"p\",\"displayName\":\"P\",\"connectionType\":\"SSH\"}",
            InventoryOptions)!;

        Assert.Equal(RemoteOs.Auto, read.RemoteOs);
    }

    [Fact]
    public void ADeclaredOperatingSystem_SurvivesTheClone()
    {
        ServerProfileDto source = new() { Id = "p", DisplayName = "P", RemoteOs = RemoteOs.Linux };

        Assert.Equal(RemoteOs.Linux, source.CloneFaithfully().RemoteOs);
    }
}
