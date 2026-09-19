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

using TwinShell.Core.Interfaces;
using TwinShell.Core.Security;

namespace Heimdall.App.Tests;

/// <summary>
/// Pins how a command-history value is written and read back.
/// </summary>
/// <remarks>
/// <para>
/// <b>This replaces a test that pinned the opposite rule.</b> It used to assert that the
/// payload kept the un-substituted pattern and dropped every parameter value, because
/// <c>twinshell.db</c> is a plain SQLite file and storing what the user typed would have
/// leaked it. That premise has changed: both fields are now sealed through
/// <see cref="HistorySecretEnvelope"/> before they reach the database, so the reason to
/// throw the information away is gone. The old assertion is not stale, it is the decision
/// being overturned, and it is recorded here rather than deleted silently.
/// </para>
/// <para>
/// The tests use a reversible stand-in rather than the real protector, which is a static
/// holding process-wide vault state: driving that state from a test would race every other
/// test in the run, and none of the rules below are about the cipher.
/// </para>
/// </remarks>
public sealed class CommandLibraryViewModelHistoryTests
{
    private const string Secret = "mysql -u root -phunter2";

    [Fact]
    public void ASealedValueSurvivesTheRoundTrip()
    {
        var protector = new ReversibleProtector();

        var stored = HistorySecretEnvelope.Seal(Secret, protector);

        Assert.Equal(Secret, HistorySecretEnvelope.Open(stored, protector));
    }

    /// <summary>
    /// The point of sealing: the stored form must not contain the plaintext.
    /// </summary>
    /// <remarks>
    /// Without this, the round-trip test above would pass on an envelope that sealed
    /// nothing at all, which is precisely the regression worth catching.
    /// </remarks>
    [Fact]
    public void ASealedValueDoesNotContainThePlaintext()
    {
        var protector = new ReversibleProtector();

        var stored = HistorySecretEnvelope.Seal(Secret, protector);

        Assert.DoesNotContain("hunter2", stored, StringComparison.Ordinal);
        Assert.NotEqual(Secret, stored);
    }

    /// <summary>
    /// Rows written before sealing existed hold a command pattern in clear and must stay
    /// readable, because nothing migrates them.
    /// </summary>
    [Fact]
    public void ARowWrittenBeforeSealingIsReturnedUnchanged()
    {
        var protector = new ReversibleProtector();
        const string legacy = "mysql -u root -p{password}";

        Assert.False(HistorySecretEnvelope.IsSealed(legacy));
        Assert.Equal(legacy, HistorySecretEnvelope.Open(legacy, protector));
    }

    /// <summary>
    /// A sealed row whose key is gone reads as unreadable rather than as ciphertext.
    /// </summary>
    [Fact]
    public void ASealedRowThatCannotBeOpenedReadsAsNull()
    {
        var stored = HistorySecretEnvelope.Seal(Secret, new ReversibleProtector());

        Assert.Null(HistorySecretEnvelope.Open(stored, new RefusingProtector()));
    }

    /// <summary>
    /// Sealing propagates a refusal to write instead of falling back to a weaker form.
    /// </summary>
    /// <remarks>
    /// This is what lets the view model decide to record nothing while the vault is
    /// locked. An envelope that swallowed the refusal would write the value in clear, and
    /// no test above would notice.
    /// </remarks>
    [Fact]
    public void SealingPropagatesARefusalToWrite()
    {
        Assert.Throws<InvalidOperationException>(
            () => HistorySecretEnvelope.Seal(Secret, new RefusingProtector()));
    }

    [Fact]
    public void OpeningRequiresAProtector()
    {
        Assert.Throws<ArgumentNullException>(
            () => HistorySecretEnvelope.Open("anything", null!));
    }

    /// <summary>
    /// Reversible stand-in for the real protector. Not a cipher, and not pretending to be:
    /// it only has to be distinguishable from the plaintext and exactly undoable.
    /// </summary>
    private sealed class ReversibleProtector : ISecretProtector
    {
        private const string Wrapper = "reversed:";

        public string Protect(string plainText) =>
            Wrapper + new string(plainText.Reverse().ToArray());

        public string? Unprotect(string protectedValue) =>
            protectedValue.StartsWith(Wrapper, StringComparison.Ordinal)
                ? new string(protectedValue[Wrapper.Length..].Reverse().ToArray())
                : null;
    }

    /// <summary>Stands for a configured vault that is currently locked.</summary>
    private sealed class RefusingProtector : ISecretProtector
    {
        public string Protect(string plainText) =>
            throw new InvalidOperationException("The vault is locked.");

        public string? Unprotect(string protectedValue) => null;
    }
}
