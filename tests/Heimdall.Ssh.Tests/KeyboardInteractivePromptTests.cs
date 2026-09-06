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

using Renci.SshNet;
using Renci.SshNet.Common;

namespace Heimdall.Ssh.Tests;

/// <summary>
/// Pins finding A-08 of the SSH audit of 2026-09-06: the keyboard-interactive exchange
/// answered every prompt with the stored password, so a server asking for a
/// verification code after the password got the password twice, and the refusal that
/// followed was reported as a rejected password. Only a password prompt gets the
/// password now; anything else is left empty, recorded, and named by the classifier.
/// </summary>
public sealed class KeyboardInteractivePromptTests
{
    [Fact]
    public void ASinglePrompt_GetsThePasswordWhateverItsWording()
    {
        KeyboardInteractiveObservation observation = new();
        AuthenticationPrompt prompt = new(0, false, "Enter your secret: ");

        SshConnectionFactory.AnswerKeyboardInteractivePrompts([prompt], "s3cret", observation);

        Assert.Equal("s3cret", prompt.Response);
        Assert.Null(observation.UnansweredPrompt);
    }

    [Fact]
    public void APasswordPromptFollowedByAVerificationCode_AnswersOnlyThePasswordAndRecordsTheOther()
    {
        KeyboardInteractiveObservation observation = new();
        AuthenticationPrompt password = new(0, false, "Password: ");
        AuthenticationPrompt code = new(1, false, "Verification code: ");

        SshConnectionFactory.AnswerKeyboardInteractivePrompts([password, code], "s3cret", observation);

        Assert.Equal("s3cret", password.Response);
        Assert.Equal(string.Empty, code.Response);
        Assert.Equal("Verification code:", observation.UnansweredPrompt);
    }

    [Theory]
    [InlineData("Mot de passe : ")]
    [InlineData("Passwort: ")]
    [InlineData("Passphrase for key: ")]
    public void ALocalisedPasswordPrompt_IsRecognised(string wording)
    {
        KeyboardInteractiveObservation observation = new();
        AuthenticationPrompt password = new(0, false, wording);
        AuthenticationPrompt other = new(1, false, "One-time token: ");

        SshConnectionFactory.AnswerKeyboardInteractivePrompts([password, other], "s3cret", observation);

        Assert.Equal("s3cret", password.Response);
        Assert.Equal(string.Empty, other.Response);
    }

    [Fact]
    public void Classifier_KeyboardInteractiveRefusalAfterAnUnansweredPrompt_NamesTheQuestionNotThePassword()
    {
        SshConnectionParams parameters = Parameters();
        parameters.KeyboardInteractive.RecordUnanswered("Verification code: ");
        SshAuthenticationException refusal = new("Permission denied (keyboard-interactive).");

        SshFailureInfo failure = FailureClassifier.Classify(refusal, parameters);

        Assert.Equal(SshFailureCode.KeyboardInteractiveUnsupportedPrompt, failure.Code);
        Assert.Contains("Verification code:", failure.Message, StringComparison.Ordinal);
        Assert.True(failure.IsFatal);
    }

    [Fact]
    public void Classifier_KeyboardInteractiveRefusalWithEveryPromptAnswered_StillBlamesThePassword()
    {
        SshConnectionParams parameters = Parameters();
        SshAuthenticationException refusal = new("Permission denied (keyboard-interactive).");

        SshFailureInfo failure = FailureClassifier.Classify(refusal, parameters);

        Assert.Equal(SshFailureCode.PasswordRejected, failure.Code);
    }

    [Fact]
    public void Observation_KeepsTheFirstUnansweredPromptAndResets()
    {
        KeyboardInteractiveObservation observation = new();

        observation.RecordUnanswered("  Code: ");
        observation.RecordUnanswered("Second: ");
        Assert.Equal("Code:", observation.UnansweredPrompt);

        observation.Reset();
        Assert.Null(observation.UnansweredPrompt);
    }

    private static SshConnectionParams Parameters() =>
        new SshConnectionParams
        {
            Host = "example.test",
            Port = 22,
            Username = "user",
            Password = "s3cret"
        };
}
