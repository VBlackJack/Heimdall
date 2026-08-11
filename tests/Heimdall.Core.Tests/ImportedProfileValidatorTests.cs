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

namespace Heimdall.Core.Tests;

public sealed class ImportedProfileValidatorTests
{
    private static readonly IReadOnlySet<string> SupportedConnectionTypes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "RDP",
            "SSH",
            "SFTP",
            "VNC",
            "TELNET",
            "FTP",
            "CITRIX",
            "LOCAL",
            "WINRM"
        };

    [Theory]
    [InlineData(nameof(ServerProfileDto.RemotePort), 0, false)]
    [InlineData(nameof(ServerProfileDto.RemotePort), 1, true)]
    [InlineData(nameof(ServerProfileDto.RemotePort), 65535, true)]
    [InlineData(nameof(ServerProfileDto.RemotePort), 65536, false)]
    [InlineData(nameof(ServerProfileDto.RemotePort), -1, false)]
    [InlineData(nameof(ServerProfileDto.LocalPort), 0, false)]
    [InlineData(nameof(ServerProfileDto.LocalPort), 1, true)]
    [InlineData(nameof(ServerProfileDto.LocalPort), 65535, true)]
    [InlineData(nameof(ServerProfileDto.LocalPort), 65536, false)]
    [InlineData(nameof(ServerProfileDto.LocalPort), -1, false)]
    [InlineData(nameof(ServerProfileDto.SshPort), 0, false)]
    [InlineData(nameof(ServerProfileDto.SshPort), 1, true)]
    [InlineData(nameof(ServerProfileDto.SshPort), 65535, true)]
    [InlineData(nameof(ServerProfileDto.SshPort), 65536, false)]
    [InlineData(nameof(ServerProfileDto.SshPort), -1, false)]
    [InlineData(nameof(ServerProfileDto.WinRmPort), 0, false)]
    [InlineData(nameof(ServerProfileDto.WinRmPort), 1, true)]
    [InlineData(nameof(ServerProfileDto.WinRmPort), 65535, true)]
    [InlineData(nameof(ServerProfileDto.WinRmPort), 65536, false)]
    [InlineData(nameof(ServerProfileDto.WinRmPort), -1, false)]
    [InlineData(nameof(ServerProfileDto.FtpPort), 0, false)]
    [InlineData(nameof(ServerProfileDto.FtpPort), 1, true)]
    [InlineData(nameof(ServerProfileDto.FtpPort), 65535, true)]
    [InlineData(nameof(ServerProfileDto.FtpPort), 65536, false)]
    [InlineData(nameof(ServerProfileDto.FtpPort), -1, false)]
    [InlineData(nameof(ServerProfileDto.VncPort), 0, false)]
    [InlineData(nameof(ServerProfileDto.VncPort), 1, true)]
    [InlineData(nameof(ServerProfileDto.VncPort), 65535, true)]
    [InlineData(nameof(ServerProfileDto.VncPort), 65536, false)]
    [InlineData(nameof(ServerProfileDto.VncPort), -1, false)]
    [InlineData(nameof(ServerProfileDto.TelnetPort), 0, false)]
    [InlineData(nameof(ServerProfileDto.TelnetPort), 1, true)]
    [InlineData(nameof(ServerProfileDto.TelnetPort), 65535, true)]
    [InlineData(nameof(ServerProfileDto.TelnetPort), 65536, false)]
    [InlineData(nameof(ServerProfileDto.TelnetPort), -1, false)]
    public void Validate_RequiredPorts_EnforcesRange(string fieldName, int value, bool expectedValid)
    {
        ServerProfileDto profile = CreateValidProfile();
        SetPort(profile, fieldName, value);

        IReadOnlyList<string> errors = ImportedProfileValidator.Validate(profile, SupportedConnectionTypes);

        if (expectedValid)
        {
            Assert.DoesNotContain(errors, error => error.Contains(fieldName, StringComparison.Ordinal));
        }
        else
        {
            Assert.Contains(errors, error => error.Contains(fieldName, StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData(nameof(ServerProfileDto.SocksProxyPort), 0, true)]
    [InlineData(nameof(ServerProfileDto.SocksProxyPort), 1, true)]
    [InlineData(nameof(ServerProfileDto.SocksProxyPort), 65535, true)]
    [InlineData(nameof(ServerProfileDto.SocksProxyPort), 65536, false)]
    [InlineData(nameof(ServerProfileDto.SocksProxyPort), -1, false)]
    [InlineData(nameof(ServerProfileDto.RemoteBindPort), 0, true)]
    [InlineData(nameof(ServerProfileDto.RemoteBindPort), 1, true)]
    [InlineData(nameof(ServerProfileDto.RemoteBindPort), 65535, true)]
    [InlineData(nameof(ServerProfileDto.RemoteBindPort), 65536, false)]
    [InlineData(nameof(ServerProfileDto.RemoteBindPort), -1, false)]
    [InlineData(nameof(ServerProfileDto.RemoteLocalPort), 0, true)]
    [InlineData(nameof(ServerProfileDto.RemoteLocalPort), 1, true)]
    [InlineData(nameof(ServerProfileDto.RemoteLocalPort), 65535, true)]
    [InlineData(nameof(ServerProfileDto.RemoteLocalPort), 65536, false)]
    [InlineData(nameof(ServerProfileDto.RemoteLocalPort), -1, false)]
    public void Validate_OptionalPorts_AllowZeroOrRange(string fieldName, int value, bool expectedValid)
    {
        ServerProfileDto profile = CreateValidProfile();
        SetPort(profile, fieldName, value);

        IReadOnlyList<string> errors = ImportedProfileValidator.Validate(profile, SupportedConnectionTypes);

        if (expectedValid)
        {
            Assert.DoesNotContain(errors, error => error.Contains(fieldName, StringComparison.Ordinal));
        }
        else
        {
            Assert.Contains(errors, error => error.Contains(fieldName, StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData("RDP")]
    [InlineData("SSH")]
    [InlineData("SFTP")]
    [InlineData("VNC")]
    [InlineData("Telnet")]
    [InlineData("FTP")]
    [InlineData("Citrix")]
    [InlineData("local")]
    [InlineData("winrm")]
    public void Validate_ConnectionType_SupportedValuesAreAccepted(string connectionType)
    {
        ServerProfileDto profile = CreateValidProfile();
        profile.ConnectionType = connectionType;

        IReadOnlyList<string> errors = ImportedProfileValidator.Validate(profile, SupportedConnectionTypes);

        Assert.DoesNotContain(errors, error => error.Contains(nameof(ServerProfileDto.ConnectionType), StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("garbage")]
    [InlineData("TOOL:CHMOD")]
    public void Validate_ConnectionType_UnsupportedValuesAreRejected(string connectionType)
    {
        ServerProfileDto profile = CreateValidProfile();
        profile.ConnectionType = connectionType;

        IReadOnlyList<string> errors = ImportedProfileValidator.Validate(profile, SupportedConnectionTypes);

        Assert.Contains(errors, error => error.Contains(nameof(ServerProfileDto.ConnectionType), StringComparison.Ordinal));
    }

    [Fact]
    public void FilterValid_OutOfRangePort_AddsFailureAndExcludesProfile()
    {
        ServerProfileDto profile = CreateValidProfile();
        profile.DisplayName = "Bad Port";
        profile.SshPort = 70000;

        (List<ServerProfileDto> valid, List<string> failures) =
            ImportedProfileValidator.FilterValid([profile], SupportedConnectionTypes);

        Assert.Empty(valid);
        string failure = Assert.Single(failures);
        Assert.Contains("Bad Port", failure, StringComparison.Ordinal);
        Assert.Contains(nameof(ServerProfileDto.SshPort), failure, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterValid_UnsupportedConnectionType_AddsFailureAndExcludesProfile()
    {
        ServerProfileDto profile = CreateValidProfile();
        profile.DisplayName = "Bad Type";
        profile.ConnectionType = "TOOL:CHMOD";

        (List<ServerProfileDto> valid, List<string> failures) =
            ImportedProfileValidator.FilterValid([profile], SupportedConnectionTypes);

        Assert.Empty(valid);
        string failure = Assert.Single(failures);
        Assert.Contains("Bad Type", failure, StringComparison.Ordinal);
        Assert.Contains(nameof(ServerProfileDto.ConnectionType), failure, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterValid_UndefinedWinRmIdentityMode_AddsFailureAndExcludesProfile()
    {
        ServerProfileDto profile = CreateValidProfile();
        profile.DisplayName = "Invalid WinRM identity";
        profile.ConnectionType = "WINRM";
        profile.WinRmIdentityMode = (WinRmIdentityMode)2;

        (List<ServerProfileDto> valid, List<string> failures) =
            ImportedProfileValidator.FilterValid([profile], SupportedConnectionTypes);

        Assert.Empty(valid);
        string failure = Assert.Single(failures);
        Assert.Contains("Invalid WinRM identity", failure, StringComparison.Ordinal);
        Assert.Contains(nameof(ServerProfileDto.WinRmIdentityMode), failure, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterValid_MixedInput_PartitionsProfilesAndPreservesValidInstances()
    {
        ServerProfileDto validProfile = CreateValidProfile();
        validProfile.Id = "valid";
        validProfile.DisplayName = "Valid";
        ServerProfileDto invalidProfile = CreateValidProfile();
        invalidProfile.Id = "invalid";
        invalidProfile.DisplayName = "Invalid";
        invalidProfile.ConnectionType = "garbage";

        (List<ServerProfileDto> valid, List<string> failures) =
            ImportedProfileValidator.FilterValid([validProfile, invalidProfile], SupportedConnectionTypes);

        ServerProfileDto imported = Assert.Single(valid);
        Assert.Same(validProfile, imported);
        string failure = Assert.Single(failures);
        Assert.Contains("Invalid", failure, StringComparison.Ordinal);
        Assert.Contains(nameof(ServerProfileDto.ConnectionType), failure, StringComparison.Ordinal);
    }

    private static ServerProfileDto CreateValidProfile() =>
        new()
        {
            Id = "profile-1",
            DisplayName = "Imported",
            RemoteServer = "host.example.com",
            ConnectionType = "RDP"
        };

    private static void SetPort(ServerProfileDto profile, string fieldName, int value)
    {
        switch (fieldName)
        {
            case nameof(ServerProfileDto.RemotePort):
                profile.RemotePort = value;
                break;

            case nameof(ServerProfileDto.LocalPort):
                profile.LocalPort = value;
                break;

            case nameof(ServerProfileDto.SshPort):
                profile.SshPort = value;
                break;

            case nameof(ServerProfileDto.WinRmPort):
                profile.WinRmPort = value;
                break;

            case nameof(ServerProfileDto.FtpPort):
                profile.FtpPort = value;
                break;

            case nameof(ServerProfileDto.VncPort):
                profile.VncPort = value;
                break;

            case nameof(ServerProfileDto.TelnetPort):
                profile.TelnetPort = value;
                break;

            case nameof(ServerProfileDto.SocksProxyPort):
                profile.SocksProxyPort = value;
                break;

            case nameof(ServerProfileDto.RemoteBindPort):
                profile.RemoteBindPort = value;
                break;

            case nameof(ServerProfileDto.RemoteLocalPort):
                profile.RemoteLocalPort = value;
                break;
        }
    }
}
