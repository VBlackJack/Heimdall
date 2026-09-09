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
    public void InteractiveCodeOnlyRound_DoesNotSpendStoredPassword()
    {
        KeyboardInteractiveObservation observation = new();
        AuthenticationPrompt code = new(0, false, "Verification code: ");
        string? requested = null;

        SshConnectionFactory.AnswerKeyboardInteractivePrompts([code], "stored-password", observation,
            request => { requested = request; return "123456"; });

        Assert.Equal("Verification code: ", requested);
        Assert.Equal("123456", code.Response);
        Assert.True(observation.HasInteractiveAnswer);
        Assert.True(observation.TryTakePasswordAnswer());
        Assert.Null(observation.UnansweredPrompt);
    }

    [Fact]
    public void InteractiveTwoRounds_UsesStoredPasswordThenAsksForCode()
    {
        KeyboardInteractiveObservation observation = new();
        AuthenticationPrompt password = new(0, false, "Password: ");
        AuthenticationPrompt code = new(0, false, "Verification code: ");
        int questions = 0;
        string? Answer(string request) { questions++; return "654321"; }

        SshConnectionFactory.AnswerKeyboardInteractivePrompts([password], "stored-password", observation, Answer);
        SshConnectionFactory.AnswerKeyboardInteractivePrompts([code], "stored-password", observation, Answer);

        Assert.Equal("stored-password", password.Response);
        Assert.Equal("654321", code.Response);
        Assert.Equal(1, questions);
    }

    [Fact]
    public void InteractiveWithoutStoredPassword_AsksInsteadOfSendingEmptyAnswer()
    {
        KeyboardInteractiveObservation observation = new();
        AuthenticationPrompt prompt = new(0, false, "Password: ");

        SshConnectionFactory.AnswerKeyboardInteractivePrompts([prompt], string.Empty, observation, _ => "typed-value");

        Assert.Equal("typed-value", prompt.Response);
        Assert.True(observation.HasInteractiveAnswer);
    }

    [Fact]
    public void InteractiveCancellation_StopsBeforeAnsweringRemainingQuestions()
    {
        KeyboardInteractiveObservation observation = new();
        AuthenticationPrompt first = new(0, false, "Verification code: ");
        AuthenticationPrompt second = new(1, false, "Recovery code: ");
        int questions = 0;

        Assert.Throws<OperationCanceledException>(() =>
            SshConnectionFactory.AnswerKeyboardInteractivePrompts([first, second], "stored", observation,
                _ => { questions++; return null; }));

        Assert.Equal(1, questions);
        Assert.False(observation.HasInteractiveAnswer);
        Assert.Null(second.Response);
    }

    [Fact]
    public void InteractiveNewAttempt_DoesNotReusePreviousCode()
    {
        KeyboardInteractiveObservation observation = new();
        AuthenticationPrompt first = new(0, false, "Code: ");
        SshConnectionFactory.AnswerKeyboardInteractivePrompts([first], "stored", observation, _ => "111111");
        observation.Reset();
        Assert.False(observation.HasInteractiveAnswer);
        AuthenticationPrompt next = new(0, false, "Code: ");

        SshConnectionFactory.AnswerKeyboardInteractivePrompts([next], "stored", observation, _ => "222222");

        Assert.Equal("222222", next.Response);
    }

    [Fact]
    public void RejectedInteractiveCode_IsNotReportedAsMissingPassword()
    {
        SshConnectionParams parameters = new() { Host = "fixture.test", Username = "audit" };
        parameters.KeyboardInteractive.RecordInteractiveAnswer();

        SshFailureInfo result = FailureClassifier.Classify(
            new SshAuthenticationException("Permission denied (keyboard-interactive)."), parameters);

        Assert.Equal(SshFailureCode.AuthRejected, result.Code);
    }

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

    /// <summary>
    /// The stored password is spent once per attempt, not once per round.
    /// </summary>
    /// <remarks>
    /// P-01 change 2. A server that authenticates in stages asks the password and then the
    /// verification code as two separate single-prompt rounds, and the second is
    /// indistinguishable from the first by wording. The password used to be sent to both, which
    /// fails and which the server records as a failed second factor.
    /// </remarks>
    [Fact]
    public void ASecondSinglePromptRound_IsRefusedAndRecorded()
    {
        KeyboardInteractiveObservation observation = new();
        observation.Reset();

        AuthenticationPrompt first = new(0, false, "Password: ");
        SshConnectionFactory.AnswerKeyboardInteractivePrompts([first], "s3cret", observation);

        AuthenticationPrompt second = new(0, false, "Verification code: ");
        SshConnectionFactory.AnswerKeyboardInteractivePrompts([second], "s3cret", observation);

        Assert.Equal("s3cret", first.Response);
        Assert.Equal(string.Empty, second.Response);
        Assert.Equal("Verification code:", observation.UnansweredPrompt);
    }

    /// <summary>
    /// A new attempt may spend the password again.
    /// </summary>
    /// <remarks>
    /// The control. A flag that latched for the life of the object would make the SECOND
    /// connection attempt of a session refuse a perfectly ordinary password round, and every
    /// assertion above would still pass.
    /// </remarks>
    [Fact]
    public void Reset_LetsTheNextAttemptSpendThePasswordAgain()
    {
        KeyboardInteractiveObservation observation = new();

        AuthenticationPrompt first = new(0, false, "Password: ");
        SshConnectionFactory.AnswerKeyboardInteractivePrompts([first], "s3cret", observation);

        observation.Reset();

        AuthenticationPrompt afterReset = new(0, false, "Password: ");
        SshConnectionFactory.AnswerKeyboardInteractivePrompts([afterReset], "s3cret", observation);

        Assert.Equal("s3cret", afterReset.Response);
        Assert.Null(observation.UnansweredPrompt);
    }
}
