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

using System.Text.RegularExpressions;

namespace Heimdall.App.Tests.Views.EmbeddedRdp;

/// <summary>
/// Freezes that the failure text the session header shows is translatable.
/// </summary>
/// <remarks>
/// <c>HandleFailure</c> writes its message argument straight into the visible status line, next to
/// the raw exception text. Four of its five call sites passed an English literal, so a French user
/// whose connect threw read "Unable to start the embedded Remote Desktop session." followed by a
/// developer message. The fifth call site already passed a locale key, which is the whole pattern.
/// </remarks>
public sealed class RdpFailureMessageLocalizationTests
{
    [Fact]
    public void NoFailureMessageIsAStringLiteral()
    {
        string source = ViewSource.Code();

        MatchCollection literals = Regex.Matches(source, @"HandleFailure\(\s*""");

        Assert.True(
            literals.Count == 0,
            $"{literals.Count} HandleFailure call sites pass an English literal that lands verbatim "
                + "in the session header. Pass a locale key, as the gateway-attestation site does.");
    }

    // Positive control: the call sites are really there, so a zero above measures something.
    [Fact]
    public void TheFailureSitesAreStillThere()
    {
        string source = ViewSource.Code();

        Assert.True(
            Regex.Matches(source, @"HandleFailure\(").Count >= 5,
            "HandleFailure has fewer call sites than expected; the guard above may be vacuous.");
    }
}
