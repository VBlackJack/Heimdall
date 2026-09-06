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

public sealed class OpenSshConfigParserTests
{
    [Fact]
    public void Parse_EmptyString_ReturnsEmptyResult()
    {
        var result = OpenSshConfigParser.Parse(string.Empty);

        Assert.Empty(result.Candidates);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Parse_SimpleHost_ReturnsOneCandidateWithAllFields()
    {
        var result = OpenSshConfigParser.Parse(
            """
            Host prod
                HostName server.example.com
                Port 2222
                User alice
                IdentityFile ~/.ssh/id_ed25519
            """);

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("prod", candidate.Alias);
        Assert.Equal("server.example.com", candidate.HostName);
        Assert.Equal(2222, candidate.Port);
        Assert.Equal("alice", candidate.User);
        Assert.NotNull(candidate.IdentityFile);
        Assert.Contains($"{Path.DirectorySeparatorChar}.ssh{Path.DirectorySeparatorChar}id_ed25519", candidate.IdentityFile!, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_HostWithoutHostName_FallsBackToAlias_AndEmitsDiagnostic()
    {
        var result = OpenSshConfigParser.Parse(
            """
            Host prod
                User alice
            """);

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("prod", candidate.HostName);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(OpenSshDiagnosticCode.HostNameFallbackToAlias, diagnostic.Code);
        Assert.Equal("prod", diagnostic.Context);
    }

    // B-06: OpenSSH expands %h in HostName to the host alias. Importing the token literally
    // produced a profile whose host fails validation on the first connection attempt.
    [Fact]
    public void Parse_HostNameWithHostToken_ExpandsTokenToAlias()
    {
        OpenSshParseResult result = OpenSshConfigParser.Parse(
            """
            Host db
                HostName %h.internal.example
            """);

        OpenSshImportCandidate candidate = Assert.Single(result.Candidates);
        Assert.Equal("db.internal.example", candidate.HostName);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == OpenSshDiagnosticCode.HostNameTokenSubstitution);
    }

    [Fact]
    public void Parse_HostNameWithEscapedPercent_ExpandsToLiteralPercent()
    {
        OpenSshParseResult result = OpenSshConfigParser.Parse(
            """
            Host odd
                HostName host%%name.example
            """);

        OpenSshImportCandidate candidate = Assert.Single(result.Candidates);
        Assert.Equal("host%name.example", candidate.HostName);
    }

    [Theory]
    [InlineData("%r.internal.example")]
    [InlineData("%p-host.example")]
    [InlineData("%n.example")]
    public void Parse_HostNameWithUnsupportedToken_SkipsCandidateWithDiagnostic(string hostName)
    {
        OpenSshParseResult result = OpenSshConfigParser.Parse(
            $"""
            Host app
                HostName {hostName}
            """);

        Assert.Empty(result.Candidates);
        OpenSshImportDiagnostic diagnostic = Assert.Single(
            result.Diagnostics,
            d => d.Code == OpenSshDiagnosticCode.HostNameTokenSubstitution);
        Assert.Equal(OpenSshDiagnosticLevel.Warning, diagnostic.Level);
        Assert.Equal(hostName, diagnostic.Context);
    }

    [Fact]
    public void Parse_MultiAliasHostLine_ProducesMultipleCandidates()
    {
        var result = OpenSshConfigParser.Parse(
            """
            Host prod1 prod2 bastion
                HostName server.example.com
            """);

        Assert.Equal(3, result.Candidates.Count);
        Assert.Collection(
            result.Candidates,
            first => Assert.Equal("prod1", first.Alias),
            second => Assert.Equal("prod2", second.Alias),
            third => Assert.Equal("bastion", third.Alias));
    }

    [Fact]
    public void Parse_WildcardAlias_SkippedWithDiagnostic()
    {
        var result = OpenSshConfigParser.Parse(
            """
            Host * web-? !prod prod
                HostName server.example.com
            """);

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("prod", candidate.Alias);
        Assert.Equal(3, result.Diagnostics.Count);
        Assert.All(result.Diagnostics, diagnostic => Assert.Equal(OpenSshDiagnosticCode.WildcardAliasIgnored, diagnostic.Code));
    }

    [Fact]
    public void Parse_IdentityFileWithTilde_ExpandsToUserProfile_AndEmitsDiagnostic()
    {
        var result = OpenSshConfigParser.Parse(
            """
            Host prod
                IdentityFile ~/.ssh/id_ed25519
            """);

        var candidate = Assert.Single(result.Candidates);
        Assert.NotNull(candidate.IdentityFile);
        Assert.DoesNotContain('~', candidate.IdentityFile);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == OpenSshDiagnosticCode.IdentityFileTildeExpanded);
    }

    [Fact]
    public void Parse_IncludeDirective_IgnoredWithDiagnostic_AndContinues()
    {
        var result = OpenSshConfigParser.Parse(
            """
            Include ~/.ssh/conf.d/*.conf
            Host prod
                HostName server.example.com
            """);

        Assert.Single(result.Candidates);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(OpenSshDiagnosticCode.IncludeDirectiveIgnored, diagnostic.Code);
    }

    [Fact]
    public void Parse_MatchBlock_IgnoredUntilNextHost()
    {
        var result = OpenSshConfigParser.Parse(
            """
            Host prod
                HostName server.example.com
            Match host *.internal
                User should-not-apply
            Host logs
                HostName logs.example.com
            """);

        Assert.Equal(2, result.Candidates.Count);
        Assert.Null(result.Candidates[1].User);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == OpenSshDiagnosticCode.MatchBlockIgnored);
    }

    [Fact]
    public void Parse_ProxyJump_SingleHop_CreatesResolvedChain()
    {
        var result = OpenSshConfigParser.Parse(
            """
            Host prod
                ProxyJump alice@bastion.example.com:2222
            """);

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("prod", candidate.HostName);
        var hop = Assert.Single(candidate.ProxyJumpChain);
        Assert.Equal("bastion.example.com", hop.Host);
        Assert.Equal("bastion.example.com", hop.HostName);
        Assert.Equal("alice", hop.User);
        Assert.Equal(2222, hop.Port);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code.ToString().StartsWith("ProxyJump", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("bastion.example.com", null, "bastion.example.com", 22)]
    [InlineData("alice@bastion.example.com", "alice", "bastion.example.com", 22)]
    [InlineData("bastion.example.com:2200", null, "bastion.example.com", 2200)]
    [InlineData("alice@bastion.example.com:2200", "alice", "bastion.example.com", 2200)]
    public void Parse_ProxyJump_SupportedSingleHopForms(string proxyJump, string? expectedUser, string expectedHost, int expectedPort)
    {
        var result = OpenSshConfigParser.Parse(
            $$"""
            Host prod
                ProxyJump {{proxyJump}}
            """);

        var candidate = Assert.Single(result.Candidates);
        var hop = Assert.Single(candidate.ProxyJumpChain);
        Assert.Equal(expectedHost, hop.HostName);
        Assert.Equal(expectedUser, hop.User);
        Assert.Equal(expectedPort, hop.Port);
    }

    [Fact]
    public void Parse_ProxyJump_MultiHopWithOverrides_PreservesOrder()
    {
        var result = OpenSshConfigParser.Parse(
            """
            Host prod
                ProxyJump u1@h1:22,u2@h2:2222,h3
            """);

        var candidate = Assert.Single(result.Candidates);
        Assert.Collection(
            candidate.ProxyJumpChain,
            first =>
            {
                Assert.Equal("h1", first.HostName);
                Assert.Equal("u1", first.User);
                Assert.Equal(22, first.Port);
            },
            second =>
            {
                Assert.Equal("h2", second.HostName);
                Assert.Equal("u2", second.User);
                Assert.Equal(2222, second.Port);
            },
            third =>
            {
                Assert.Equal("h3", third.HostName);
                Assert.Null(third.User);
                Assert.Equal(22, third.Port);
            });
    }

    [Fact]
    public void Parse_ProxyJump_None_ProducesNoChain()
    {
        var result = OpenSshConfigParser.Parse(
            """
            Host prod
                ProxyJump none
            """);

        var candidate = Assert.Single(result.Candidates);
        Assert.Empty(candidate.ProxyJumpChain);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code is OpenSshDiagnosticCode.ProxyJumpUnrecognizedSyntax);
    }

    [Fact]
    public void Parse_ProxyCommand_ProducesUnsupportedDiagnostic()
    {
        var result = OpenSshConfigParser.Parse(
            """
            Host prod
                ProxyCommand ssh -W %h:%p bastion
            """);

        var candidate = Assert.Single(result.Candidates);
        Assert.Empty(candidate.ProxyJumpChain);
        Assert.Contains(result.Diagnostics, d => d.Code == OpenSshDiagnosticCode.ProxyCommandUnsupported);
    }

    [Fact]
    public void Parse_ProxyJumpAndProxyCommand_ProducesConflictDiagnostic()
    {
        var result = OpenSshConfigParser.Parse(
            """
            Host prod
                ProxyJump bastion
                ProxyCommand ssh -W %h:%p other
            """);

        var candidate = Assert.Single(result.Candidates);
        Assert.Empty(candidate.ProxyJumpChain);
        Assert.Contains(result.Diagnostics, d => d.Code == OpenSshDiagnosticCode.ProxyJumpMixedWithProxyCommand);
    }

    [Fact]
    public void Parse_ProxyJumpWithOpenSshToken_ProducesUnsupportedDiagnostic()
    {
        var result = OpenSshConfigParser.Parse(
            """
            Host prod
                ProxyJump %h
            """);

        var candidate = Assert.Single(result.Candidates);
        Assert.Empty(candidate.ProxyJumpChain);
        Assert.Contains(result.Diagnostics, d => d.Code == OpenSshDiagnosticCode.ProxyJumpTokenSubstitution);
    }

    [Theory]
    [InlineData("host1, host2")]
    [InlineData("\"host1\"")]
    [InlineData("host1,,host2")]
    public void Parse_ProxyJumpMalformedSyntax_ProducesDiagnostic(string proxyJump)
    {
        var result = OpenSshConfigParser.Parse(
            $$"""
            Host prod
                ProxyJump {{proxyJump}}
            """);

        var candidate = Assert.Single(result.Candidates);
        Assert.Empty(candidate.ProxyJumpChain);
        Assert.Contains(result.Diagnostics, d => d.Code == OpenSshDiagnosticCode.ProxyJumpUnrecognizedSyntax);
    }

    [Fact]
    public void Parse_ProxyJumpCycle_ProducesDiagnosticAndNoChain()
    {
        var result = OpenSshConfigParser.Parse(
            """
            Host prod
                ProxyJump bastion
            Host bastion
                ProxyJump prod
            """);

        var candidate = Assert.Single(result.Candidates, c => c.Alias == "prod");
        Assert.Empty(candidate.ProxyJumpChain);
        Assert.Contains(result.Diagnostics, d => d.Code == OpenSshDiagnosticCode.ProxyJumpCycle);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("65536")]
    public void Parse_InvalidPort_DefaultsTo22WithDiagnostic(string rawPort)
    {
        var result = OpenSshConfigParser.Parse(
            $$"""
            Host prod
                Port {{rawPort}}
            """);

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal(22, candidate.Port);
        var diagnostic = Assert.Single(result.Diagnostics, d => d.Code == OpenSshDiagnosticCode.InvalidPort);
        Assert.Equal(rawPort, diagnostic.Context);
    }

    [Fact]
    public void Parse_DuplicateAliasInFile_FirstWins_AndEmitsDiagnostic()
    {
        var result = OpenSshConfigParser.Parse(
            """
            Host prod
                HostName first.example.com
            Host PROD
                HostName second.example.com
            """);

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("first.example.com", candidate.HostName);
        var diagnostic = Assert.Single(result.Diagnostics, d => d.Code == OpenSshDiagnosticCode.DuplicateAliasInFile);
        Assert.Equal("PROD", diagnostic.Context);
    }

    [Fact]
    public void Parse_CommentsAndCasingAndBlankLines_AreTolerated()
    {
        var result = OpenSshConfigParser.Parse(
            """
            # comment

            HoSt "prod"   # trailing comment
                HoStNaMe "server.example.com"
                uSeR alice
                UnknownThing yes
            """);

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("prod", candidate.Alias);
        Assert.Equal("server.example.com", candidate.HostName);
        Assert.Equal("alice", candidate.User);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == OpenSshDiagnosticCode.UnknownDirectiveIgnored);
    }
}
