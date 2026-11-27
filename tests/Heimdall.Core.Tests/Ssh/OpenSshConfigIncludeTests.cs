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

using Heimdall.Core.Ssh;

namespace Heimdall.Core.Tests.Ssh;

public sealed class OpenSshConfigIncludeTests
{
    [Fact]
    public void ParseFile_ExpandsGlobbedIncludesInLexicalOrder()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var configD = Path.Combine(tempRoot, "config.d");
            Directory.CreateDirectory(configD);

            File.WriteAllText(
                Path.Combine(configD, "01-bastion.conf"),
                "Host bastion\n    HostName 10.0.0.1\n    User jump\n");
            File.WriteAllText(
                Path.Combine(configD, "02-app.conf"),
                "Host app01\n    HostName 10.0.1.10\n    User deploy\n    ProxyJump bastion\n");

            var mainConfig = Path.Combine(tempRoot, "config");
            File.WriteAllText(
                mainConfig,
                "Include config.d/*.conf\n\nHost direct01\n    HostName 10.0.2.20\n    User ops\n");

            var result = OpenSshConfigParser.ParseFile(mainConfig, tempRoot);

            Assert.Equal(3, result.Candidates.Count);
            Assert.Equal(["bastion", "app01", "direct01"], result.Candidates.Select(c => c.Alias).ToArray());
            Assert.Equal("10.0.0.1", result.Candidates[0].HostName);
            var hop = Assert.Single(result.Candidates[1].ProxyJumpChain);
            Assert.Equal("bastion", hop.Host);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public void ParseFile_StopsIncludeCyclesGracefully()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var aPath = Path.Combine(tempRoot, "a.conf");
            var bPath = Path.Combine(tempRoot, "b.conf");

            File.WriteAllText(aPath, "Include b.conf\nHost alpha\n    HostName 10.1.0.1\n");
            File.WriteAllText(bPath, "Include a.conf\nHost beta\n    HostName 10.1.0.2\n");

            var result = OpenSshConfigParser.ParseFile(aPath, tempRoot);

            Assert.Equal(["beta", "alpha"], result.Candidates.Select(c => c.Alias).ToArray());
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public void ParseFile_RejectsRelativeParentTraversalAndIgnoresMissingIncludes()
    {
        var outerRoot = CreateTempDirectory();
        try
        {
            var sshDir = Path.Combine(outerRoot, ".ssh");
            Directory.CreateDirectory(sshDir);

            File.WriteAllText(
                Path.Combine(outerRoot, "secret.conf"),
                "Host escaped\n    HostName 10.9.9.9\n");

            var mainConfig = Path.Combine(sshDir, "config");
            File.WriteAllText(
                mainConfig,
                "Include ../secret.conf\nInclude nonexistent/*.conf\nHost safe\n    HostName 10.2.0.1\n");

            var result = OpenSshConfigParser.ParseFile(mainConfig, outerRoot);

            var only = Assert.Single(result.Candidates);
            Assert.Equal("safe", only.Alias);
        }
        finally
        {
            TryDeleteDirectory(outerRoot);
        }
    }

    [Fact]
    public void Parse_StringOverload_PreservesIncludeIgnoredDiagnostic()
    {
        var result = OpenSshConfigParser.Parse("Include config.d/*.conf\nHost web\n    HostName 10.0.0.5\n");
        Assert.Contains(result.Diagnostics, d => d.Code == OpenSshDiagnosticCode.IncludeDirectiveIgnored);
    }

    [Fact]
    public void ParseFile_BlocksForwardSlashUncAbsoluteOutsideRootAndMatchScopedIncludes()
    {
        var outerRoot = CreateTempDirectory();
        var siblingDir = CreateTempDirectory();
        try
        {
            var sshDir = Path.Combine(outerRoot, ".ssh");
            Directory.CreateDirectory(sshDir);

            var outsideFile = Path.Combine(siblingDir, "outside.conf");
            File.WriteAllText(outsideFile, "Host outside\n    HostName 10.9.9.1\n");

            var matchTarget = Path.Combine(sshDir, "in-match.conf");
            File.WriteAllText(matchTarget, "Host leaked-from-match\n    HostName 10.9.9.2\n");

            var mainConfig = Path.Combine(sshDir, "config");
            File.WriteAllText(
                mainConfig,
                $"Include //nonexistent.invalid/share/x.conf\n" +
                $"Include {outsideFile}\n" +
                "Match host prod-*\n" +
                "    Include in-match.conf\n" +
                "Host clean\n" +
                "    HostName 10.3.0.1\n");

            var result = OpenSshConfigParser.ParseFile(mainConfig, outerRoot);

            var only = Assert.Single(result.Candidates);
            Assert.Equal("clean", only.Alias);
        }
        finally
        {
            TryDeleteDirectory(outerRoot);
            TryDeleteDirectory(siblingDir);
        }
    }

    private static string CreateTempDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "heimdall-ssh-include-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
