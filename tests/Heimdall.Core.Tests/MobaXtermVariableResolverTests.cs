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

public sealed class MobaXtermVariableResolverTests
{
    // Unique per run so a parallel test never sees another test's value.
    private static readonly string EnvironmentName = "HEIMDALL_MOBA_TEST_" + Guid.NewGuid().ToString("N");

    [Fact]
    public void FromVariables_LiteralValue_ResolvedCaseInsensitively()
    {
        Func<string, string?> resolve = MobaXtermVariableResolver.FromVariables(
            new Dictionary<string, string> { ["DAS"] = "jdoe" });

        Assert.Equal("jdoe", resolve("DAS"));
        Assert.Equal("jdoe", resolve("das"));
    }

    [Fact]
    public void FromVariables_EnvironmentReference_IsExpanded()
    {
        Environment.SetEnvironmentVariable(EnvironmentName, "corp-id-42");
        try
        {
            Func<string, string?> resolve = MobaXtermVariableResolver.FromVariables(
                new Dictionary<string, string> { ["DAS"] = $"%{EnvironmentName}%" });

            Assert.Equal("corp-id-42", resolve("DAS"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvironmentName, null);
        }
    }

    [Fact]
    public void FromVariables_UndefinedEnvironmentReference_IsUnresolved()
    {
        Func<string, string?> resolve = MobaXtermVariableResolver.FromVariables(
            new Dictionary<string, string> { ["DAS"] = $"%{EnvironmentName}_MISSING%" });

        Assert.Null(resolve("DAS"));
    }

    [Fact]
    public void FromVariables_UnknownNameOrBlankValue_IsUnresolved()
    {
        Func<string, string?> resolve = MobaXtermVariableResolver.FromVariables(
            new Dictionary<string, string> { ["EMPTY"] = "  " });

        Assert.Null(resolve("EMPTY"));
        Assert.Null(resolve("OTHER"));
    }

    [Fact]
    public void FromVariables_NullDictionary_ResolvesNothing()
    {
        Assert.Null(MobaXtermVariableResolver.FromVariables(null)("DAS"));
    }

    // The settings editor accepts a name through IsValidName; the parser replaces a token through
    // its own pattern. A name one accepts and the other never matches would be saved and silently
    // ignored at import, so the two are held to the same answer here.
    [Theory]
    [InlineData("DAS")]
    [InlineData("das")]
    [InlineData("_x")]
    [InlineData("A1_b2")]
    [InlineData("1DAS")]
    [InlineData("DA-S")]
    [InlineData("DA S")]
    [InlineData("DÉS")]
    public void IsValidName_AgreesWithTheTokensTheParserReplaces(string name)
    {
        string ini = "[Bookmarks]\nSubRep=\nImgNum=42\nsrv=#126#0%srv.example.test%22%[" + name + "]%\n";

        MobaXtermImportResult result = MobaXtermImporter.Parse(
            ini,
            new MobaXtermImportOptions { VariableResolver = _ => "resolved" });

        bool parserReplaced = Assert.Single(result.Servers).SshUsername == "resolved";
        Assert.Equal(parserReplaced, MobaXtermVariableResolver.IsValidName(name));
    }

    [Fact]
    public void ResolveValue_IsWhatTheImportResolverReturns()
    {
        Environment.SetEnvironmentVariable(EnvironmentName, "corp-id-9");
        try
        {
            string value = $"%{EnvironmentName}%";

            Assert.Equal(
                MobaXtermVariableResolver.FromVariables(new Dictionary<string, string> { ["DAS"] = value })("DAS"),
                MobaXtermVariableResolver.ResolveValue(value));
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvironmentName, null);
        }
    }

    [Fact]
    public void AppSettingsDefault_MapsDasToTheWindowsUserName()
    {
        var settings = new AppSettings();

        Assert.Equal("%USERNAME%", settings.MobaXtermUserVariables["DAS"]);
    }
}
