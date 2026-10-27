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

public sealed class ImportedPathPolicyTests
{
    // Each of these makes Windows reach another host, or bypasses path normalization, the moment
    // anything asks whether the file exists.
    [Theory]
    [InlineData(@"\\attacker.example\share\id_rsa")]
    [InlineData("//attacker.example/share/id_rsa")]
    [InlineData(@"\/attacker.example\share\id_rsa")]
    [InlineData(@"/\attacker.example\share\id_rsa")]
    [InlineData(@"\\?\UNC\attacker.example\share\id_rsa")]
    [InlineData(@"\\?\C:\keys\id_rsa")]
    [InlineData(@"\\.\pipe\agent")]
    [InlineData(@"\??\UNC\attacker.example\share\a.ica")]
    [InlineData(@"   \\attacker.example\share\id_rsa")]
    public void IsNetworkOrDevicePath_RefusesSharesAndDevicePaths(string path)
    {
        Assert.True(ImportedPathPolicy.IsNetworkOrDevicePath(path));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"C:\Users\me\.ssh\id_ed25519")]
    [InlineData("C:/Users/me/.ssh/id_ed25519")]
    [InlineData(@"\Users\me\.ssh\id_ed25519")]
    [InlineData("/home/me/.ssh/id_ed25519")]
    [InlineData(@"keys\id_rsa")]
    [InlineData("~/.ssh/id_rsa")]
    [InlineData(@"Z:\mapped\id_rsa")]
    public void IsNetworkOrDevicePath_KeepsLocalPaths(string? path)
    {
        Assert.False(ImportedPathPolicy.IsNetworkOrDevicePath(path));
    }
}
