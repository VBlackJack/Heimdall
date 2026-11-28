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

using Heimdall.App.Services.Handlers;

namespace Heimdall.App.Tests;

public sealed class SshHandlerForwardArgsTests
{
    [Fact]
    public void BuildPipeModeArguments_IncludesValidatedLocalDynamicAndRemoteForwards()
    {
        var args = SshHandler.BuildPipeModeArguments(
            keyPath: null,
            compression: true,
            agentForwarding: false,
            x11Forwarding: false,
            port: 22,
            target: "ops@example.com",
            hostKeyFingerprint: "SHA256:abc",
            localForwards: ["127.0.0.1:8080:10.0.0.5:80", "[::1]:8443:[::1]:443", "not-valid"],
            socksProxyPort: 1080,
            remoteBindPort: 9000,
            remoteLocalPort: 3000);

        Assert.Contains("-L 127.0.0.1:8080:10.0.0.5:80", args, StringComparison.Ordinal);
        Assert.Contains("-L [::1]:8443:[::1]:443", args, StringComparison.Ordinal);
        Assert.DoesNotContain("not-valid", args, StringComparison.Ordinal);
        Assert.Contains("-D 127.0.0.1:1080", args, StringComparison.Ordinal);
        Assert.Contains("-R 127.0.0.1:9000:127.0.0.1:3000", args, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPuttyStartInfo_IncludesValidatedLocalDynamicAndRemoteForwards()
    {
        var psi = SshHandler.BuildPuttyStartInfo(
            puttyPath: @"C:\Tools\putty.exe",
            keyPath: null,
            compression: false,
            agentForwarding: false,
            x11Forwarding: false,
            port: 2222,
            target: "ops@example.com",
            hostKeyFingerprint: "SHA256:abc",
            localForwards: ["8080:db.internal:5432"],
            socksProxyPort: 1085,
            remoteBindPort: 9100,
            remoteLocalPort: 0);

        var list = psi.ArgumentList;
        Assert.Contains("-L", list);
        Assert.Contains("127.0.0.1:8080:db.internal:5432", list);
        Assert.Contains("-D", list);
        Assert.Contains("127.0.0.1:1085", list);
        Assert.Contains("-R", list);
        Assert.Contains("127.0.0.1:9100:127.0.0.1:9100", list);
    }
}
