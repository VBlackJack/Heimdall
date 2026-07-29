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

public sealed class ImportedProfileSanitizerTests
{
    [Fact]
    public void Sanitize_ClearsCitrixLaunchCommandLine_AndPreservesOtherFields()
    {
        var profiles = new List<ServerProfileDto>
        {
            new()
            {
                Id = "profile-1",
                DisplayName = "Citrix App",
                RemoteServer = "citrix.example.com",
                CitrixAppName = "Calculator",
                CitrixStoreFrontUrl = "https://citrix.example.com/Citrix/StoreWeb/",
                CitrixLaunchCommandLine = "-launch cached value",
                ExecutionConfirmed = true
            },
            new()
            {
                Id = "profile-2",
                DisplayName = "Standard SSH",
                RemoteServer = "ssh.example.com",
                ConnectionType = "SSH",
                CitrixLaunchCommandLine = null
            }
        };

        ImportedProfileSanitizer.Sanitize(profiles);

        Assert.All(profiles, profile => Assert.Null(profile.CitrixLaunchCommandLine));
        Assert.All(profiles, profile => Assert.False(profile.ExecutionConfirmed));
        Assert.Equal("profile-1", profiles[0].Id);
        Assert.Equal("Citrix App", profiles[0].DisplayName);
        Assert.Equal("citrix.example.com", profiles[0].RemoteServer);
        Assert.Equal("Calculator", profiles[0].CitrixAppName);
        Assert.Equal("https://citrix.example.com/Citrix/StoreWeb/", profiles[0].CitrixStoreFrontUrl);
        Assert.Equal("profile-2", profiles[1].Id);
        Assert.Equal("Standard SSH", profiles[1].DisplayName);
        Assert.Equal("ssh.example.com", profiles[1].RemoteServer);
        Assert.Equal("SSH", profiles[1].ConnectionType);
    }

    [Fact]
    public void Sanitize_EmptyList_DoesNotThrow()
    {
        var profiles = new List<ServerProfileDto>();

        var exception = Record.Exception(() => ImportedProfileSanitizer.Sanitize(profiles));

        Assert.Null(exception);
    }

    [Fact]
    public void Sanitize_WinRmWithoutSsl_ClearsLatentCertificateSkip()
    {
        var profiles = new List<ServerProfileDto>
        {
            new()
            {
                ConnectionType = "WINRM",
                WinRmUseSsl = false,
                WinRmSkipCertificateCheck = true
            },
            new()
            {
                ConnectionType = "WINRM",
                WinRmUseSsl = true,
                WinRmSkipCertificateCheck = true
            }
        };

        ImportedProfileSanitizer.Sanitize(profiles);

        Assert.False(profiles[0].WinRmSkipCertificateCheck);
        Assert.True(profiles[1].WinRmSkipCertificateCheck);
    }

    [Fact]
    public void Sanitize_ListContainingNullEntries_DoesNotThrow()
    {
        var profiles = new List<ServerProfileDto>
        {
            null!,
            new()
            {
                Id = "profile-3",
                DisplayName = "Citrix App",
                CitrixLaunchCommandLine = "-launch still here"
            }
        };

        var exception = Record.Exception(() => ImportedProfileSanitizer.Sanitize(profiles));

        Assert.Null(exception);
        Assert.Null(profiles[1].CitrixLaunchCommandLine);
        Assert.Equal("profile-3", profiles[1].Id);
    }
}
