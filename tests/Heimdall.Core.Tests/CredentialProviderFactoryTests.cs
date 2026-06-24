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
using Heimdall.Core.Configuration;
using Heimdall.Core.Security;

namespace Heimdall.Core.Tests;

[SupportedOSPlatform("windows")]
public class CredentialProviderFactoryTests
{
    [Fact]
    public void Create_CommandType_ReturnsCommandProvider()
    {
        var factory = new CredentialProviderFactory();
        var settings = new AppSettings
        {
            CredentialProviderType = CredentialProviderKind.Command,
            CredentialProviderCommand = "keepassxc-cli show -s {title}"
        };

        ICredentialProvider provider = factory.Create(settings);

        Assert.IsType<CommandCredentialProvider>(provider);
    }

    [Fact]
    public void Create_WindowsCredentialManagerType_ReturnsCredManProvider()
    {
        var factory = new CredentialProviderFactory();
        var settings = new AppSettings
        {
            CredentialProviderType = CredentialProviderKind.WindowsCredentialManager
        };

        ICredentialProvider provider = factory.Create(settings);

        Assert.IsType<WindowsCredentialManagerProvider>(provider);
    }

    [Fact]
    public void Create_DefaultSettings_DefaultsToCommandProvider()
    {
        var factory = new CredentialProviderFactory();

        ICredentialProvider provider = factory.Create(new AppSettings());

        Assert.IsType<CommandCredentialProvider>(provider);
    }

    [Fact]
    public async Task Create_FirstLineOnly_FlagFlowsToCommandProvider()
    {
        // No public accessor for the flag, so assert it flowed through via observable behaviour:
        // the command emits a value plus a trailing status line; first-line-only keeps the value.
        var factory = new CredentialProviderFactory();
        var settings = new AppSettings
        {
            CredentialProviderType = CredentialProviderKind.Command,
            CredentialProviderCommand = "cmd.exe /c echo thepass& echo OK: done",
            CredentialProviderTimeoutMs = 60000,
            CredentialProviderFirstLineOnly = true
        };

        ICredentialProvider provider = factory.Create(settings);
        var result = await provider.GetCredentialAsync("host", 22, "user", "title");

        Assert.NotNull(result);
        Assert.Equal("thepass", result!.Password);
    }

    [Fact]
    public void Create_KeyFile_FlowsToCommandProvider()
    {
        // No public accessor for the key file, so assert it flowed through via ExpandTemplate:
        // a {KeyFile} template must expand to the configured path (same technique as Database).
        var factory = new CredentialProviderFactory();
        var settings = new AppSettings
        {
            CredentialProviderType = CredentialProviderKind.Command,
            CredentialProviderCommand = "keepassxc-cli.exe show -k {KeyFile}",
            CredentialProviderKeyFile = @"C:\vault\company.keyx"
        };

        ICredentialProvider provider = factory.Create(settings);

        var command = Assert.IsType<CommandCredentialProvider>(provider);
        var expanded = command.ExpandTemplate(
            "keepassxc-cli.exe show -k {KeyFile}", "host", 22, "user", "title");
        Assert.Equal(@"keepassxc-cli.exe show -k C:\vault\company.keyx", expanded);
    }
}
