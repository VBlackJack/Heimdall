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

using System.IO;
using Heimdall.App.Services;
using Heimdall.App.Tests.Views.EmbeddedRdp;

namespace Heimdall.App.Tests;

public sealed class VncCredentialsPolicyTests
{
    [Theory]
    [InlineData("[\"username\",\"password\"]", "ErrorVncUsernameRequired")]
    [InlineData("[\"password\"]", "ErrorVncPasswordRequired")]
    [InlineData("[]", "ErrorVncPasswordRequired")]
    [InlineData("", "ErrorVncPasswordRequired")]
    [InlineData("not json", "ErrorVncPasswordRequired")]
    public void ACredentialRequest_NamesWhatIsMissing(string requestedTypesJson, string expectedKey)
    {
        Assert.Equal(expectedKey, VncCredentialsPolicy.MessageKeyFor(requestedTypesJson));
    }

    private const string HandlerMember = "private void HandleCredentialsRequest(string requestedTypesJson)";

    [Fact]
    public void TheHost_EndsTheAttemptInsteadOfConnectingAgain()
    {
        string logic = ViewSource.HandlerBody(
            ViewSource.WithoutCommentsAndLiterals(ReadSource("Views", "EmbeddedVncView.xaml.cs")),
            HandlerMember);

        Assert.True(
            ViewSource.IsStatementOfTheMethodBody(logic, "string key = VncCredentialsPolicy.MessageKeyFor(requestedTypesJson);"),
            "The credential request does not go through the policy.");
        Assert.DoesNotContain("SendConnectCommand()", logic, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePage_NoLongerConnectsWithoutLookingForAPreviousClient()
    {
        string page = ReadSource("Assets", "vnc.html");

        // The old body went straight to the constructor; the new one disposes the previous client.
        Assert.DoesNotContain("function connect(wsUrl, password) {\r\n                try {", page, StringComparison.Ordinal);
        Assert.DoesNotContain("function connect(wsUrl, password) {\n                try {", page, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePage_CarriesNoEnglishErrorOfItsOwnToTheHost()
    {
        string page = ReadSource("Assets", "vnc.html");

        Assert.DoesNotContain("showError('Disconnected'", page, StringComparison.Ordinal);
        Assert.DoesNotContain("showError('Connection Error'", page, StringComparison.Ordinal);
    }

    private static string ReadSource(params string[] relative)
    {
        string full = Path.Combine([ViewSource.RepoRoot(), "src", "Heimdall.App", .. relative]);
        Assert.True(File.Exists(full), $"Source not found: {full}");
        return File.ReadAllText(full);
    }
}
