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

using Heimdall.App.Services;
using Heimdall.Core.Certificates;

namespace Heimdall.App.Tests;

/// <summary>
/// Which sentence a stopped connection shows, given why it stopped.
/// </summary>
/// <remarks>
/// <para><b>The false claim this ends.</b> Every way of failing to reach a person used to come
/// back as a refusal, and the pane then wrote "Connection cancelled: you did not approve the
/// certificate this server presented" - a sentence about an answer that was never asked for.
/// Before the question moved into the pane it could not be false: a prompt always had a window
/// to appear on, so a refusal only ever came from a person.</para>
/// <para>Extracted rather than left as a branch inside the view, because a decision inside a
/// <c>UserControl</c> that builds a WPF surface is a decision nothing can test.</para>
/// </remarks>
public sealed class RdpCertificateStoppedStatusTests
{
    [Fact]
    public void APersonPressedDoNotConnect_IsReportedAsTheirAnswer()
        => Assert.Equal(
            RdpCertificatePromptLocaleKeys.RefusedStatus,
            RdpCertificateStoppedStatus.StatusKey(RdpVerificationOutcome.RefusedByUser));

    [Fact]
    public void AQuestionThatReachedNobody_IsNotReportedAsTheirAnswer()
    {
        string key = RdpCertificateStoppedStatus.StatusKey(
            RdpVerificationOutcome.QuestionNotAsked);

        Assert.Equal(RdpCertificatePromptLocaleKeys.NotAskedStatus, key);
        Assert.NotEqual(RdpCertificatePromptLocaleKeys.RefusedStatus, key);
    }

    [Fact]
    public void NoOutcomeAtAll_IsAlsoNotReportedAsAnAnswer()
    {
        // The default leans towards silence about the user's intentions rather than towards a
        // claim about it. Attributing a decision nobody made is the failure being removed;
        // saying the question could not be put is at worst vague.
        Assert.Equal(
            RdpCertificatePromptLocaleKeys.NotAskedStatus,
            RdpCertificateStoppedStatus.StatusKey(null));
    }

    [Fact]
    public void TheTwoSentencesAreTwoDifferentKeys()
    {
        // Both keys reaching a catalogue entry is guarded elsewhere, by
        // CSharpLocaleKeyCoverageTests over every key-holder class - which is the guard that
        // catches a key referenced from C# and never merged, and which renders as its own
        // identifier when it is missed. Repeating the catalogue read here would only duplicate
        // that failure.
        Assert.NotEqual(
            RdpCertificatePromptLocaleKeys.RefusedStatus,
            RdpCertificatePromptLocaleKeys.NotAskedStatus);
    }
}
