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
            ViewSource.IsStatementOfTheMethodBody(logic, "string key = VncCredentialsPolicy.MessageKeyFor(requestedTypesJson, canAsk);"),
            "The credential request does not go through the policy.");
        Assert.DoesNotContain("SendConnectCommand()", logic, StringComparison.Ordinal);
    }

    // A session whose profile cannot hold the password is told to type it, and only for a
    // password alone. Kills: the profile advice on a shared session, or the typing advice on a
    // request for a username too.
    [Theory]
    [InlineData("[\"password\"]", true, "ErrorVncSharedPasswordRequired")]
    [InlineData("[\"password\"]", false, "ErrorVncPasswordRequired")]
    [InlineData("[\"username\",\"password\"]", true, "ErrorVncUsernameRequired")]
    [InlineData("not json", true, "ErrorVncPasswordRequired")]
    public void ACredentialRequest_SaysWhereThePasswordGoes(string requestedTypesJson, bool canTypePassword, string expectedKey)
    {
        Assert.Equal(expectedKey, VncCredentialsPolicy.MessageKeyFor(requestedTypesJson, canTypePassword));
    }

    // Only a password alone can be typed for a shared session: Heimdall has no VNC username.
    // Kills: offering the password entry for every credential request.
    [Theory]
    [InlineData("[\"password\"]", true)]
    [InlineData("[\"username\",\"password\"]", false)]
    [InlineData("[\"username\"]", false)]
    [InlineData("[]", false)]
    [InlineData("", false)]
    [InlineData("not json", false)]
    public void OnlyAPasswordAlone_CanBeTyped(string requestedTypesJson, bool expected)
    {
        Assert.Equal(expected, VncCredentialsPolicy.AsksForPasswordOnly(requestedTypesJson));
    }

    // What the page reports with a security failure. A malformed report counts as before the
    // password, the direction that forgets nothing. Kills: reading afterPassword from the wrong
    // member, or defaulting it to true.
    [Theory]
    [InlineData("{\"afterPassword\":true,\"status\":1,\"reason\":\"Authentication failure\"}", true, "Authentication failure")]
    [InlineData("{\"afterPassword\":false,\"status\":1,\"reason\":\"Too many security failures\"}", false, "Too many security failures")]
    [InlineData("{\"afterPassword\":true,\"status\":1,\"reason\":null}", true, null)]
    [InlineData("{\"afterPassword\":\"true\"}", false, null)]
    [InlineData("[]", false, null)]
    [InlineData("not json", false, null)]
    [InlineData("", false, null)]
    public void ASecurityFailure_IsReadFromThePagesReport(string json, bool afterPassword, string? reason)
    {
        Assert.Equal(new VncSecurityFailure(afterPassword, reason), VncCredentialsPolicy.ReadSecurityFailure(json));
    }

    // A refused password says so; a refusal before it names the server's reason when it gave one.
    // Kills: "connection lost" or a profile advice shown for a shared session.
    [Theory]
    [InlineData(true, "x", true, "ErrorVncSharedPasswordRejected")]
    [InlineData(true, "x", false, "ErrorVncPasswordRejected")]
    [InlineData(false, "Too many security failures", true, "ErrorVncSecurityRefusedReason")]
    [InlineData(false, null, false, "ErrorVncSecurityRefused")]
    [InlineData(false, " ", true, "ErrorVncSecurityRefused")]
    public void ASecurityFailure_NamesWhatHappened(bool afterPassword, string? reason, bool canAsk, string expectedKey)
    {
        Assert.Equal(expectedKey, VncCredentialsPolicy.MessageKeyForSecurityFailure(new VncSecurityFailure(afterPassword, reason), canAsk));
    }

    [Fact]
    public void ThePage_NoLongerConnectsWithoutLookingForAPreviousClient()
    {
        string page = ReadSource("Assets", "vnc.html");

        // The old body went straight to the constructor; the new one disposes the previous client.
        Assert.DoesNotContain("function connect(wsUrl, password, viewOnly) {\r\n                try {", page, StringComparison.Ordinal);
        Assert.DoesNotContain("function connect(wsUrl, password, viewOnly) {\n                try {", page, StringComparison.Ordinal);
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
