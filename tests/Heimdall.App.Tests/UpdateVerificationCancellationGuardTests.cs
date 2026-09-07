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

namespace Heimdall.App.Tests;

/// <summary>
/// The install verification pass hashes the downloaded file through the abandonable overload.
/// </summary>
/// <remarks>
/// <para>
/// A-21. <c>Sha256Verifier.ComputeHexAsync</c> has its own behavioural oracles, but they prove
/// the helper, not that the caller uses it. Reverting the one call site back to the synchronous
/// overload leaves both of those green, which is this project's most repeated review failure: a
/// mutant that damages a neighbour proves nothing about the hunk the fix introduces.
/// </para>
/// <para>
/// A guard rather than a case, because no behavioural test can reach that line. Cancelling the
/// token any earlier is caught by the copy or by the flush before it, and the file the hash reads
/// is a real one on local disk that no seam can slow down. So the instrument is the call site
/// itself, carried through the statement predicate so that folding it behind a false term does
/// not keep this green, and its mutant is putting the synchronous call back.
/// </para>
/// <para>
/// Living in this assembly rather than beside the code it reads: the statement predicate and its
/// reader are here, and Heimdall.Core.Tests has never read source. Duplicating the machinery
/// there to keep a file next to its subject would be the worse trade.
/// </para>
/// </remarks>
public sealed class UpdateVerificationCancellationGuardTests
{
    private const string DownloadMember =
        "public async Task<IVerifiedUpdatePackage> DownloadVerifiedAsync(";

    /// <summary>
    /// The anchor. A prefix rather than the whole statement: the predicate compares the source
    /// text ordinally, so a multi-line call would have to be reproduced with its exact newlines
    /// and indentation, and would then break on a reformat that changed nothing.
    /// </summary>
    private const string CancellableHashStatement = "actualSha256 = await Sha256Verifier";

    /// <summary>
    /// The chain the anchor sits in: the method body, the staging try, then the lease try. Two
    /// try blocks deep, which is why a plain body-level assertion cannot see it.
    /// </summary>
    private const string EnclosingBlock = "try";

    private static string UpdateServiceLogic() =>
        SourceStatements.Logic("src", "Heimdall.Core", "Updates", "UpdateService.cs");

    [Fact]
    public void DownloadVerifiedAsync_HashesThroughTheAbandonableOverload()
    {
        string logic = SourceStatements.Method(UpdateServiceLogic(), DownloadMember);

        SourceStatements.AssertStatementChain(
            logic, EnclosingBlock, EnclosingBlock, CancellableHashStatement);
    }

    /// <summary>
    /// The guard would notice the synchronous call coming back.
    /// </summary>
    /// <remarks>
    /// The positive control. A presence assertion dies on deletion but not always on
    /// substitution, and the substitution is exactly the mutant that matters here.
    /// </remarks>
    [Fact]
    public void TheGuard_FailsWhenTheSynchronousCallReturns()
    {
        string mutated = SourceStatements
            .Method(UpdateServiceLogic(), DownloadMember)
            .Replace(
                CancellableHashStatement,
                "actualSha256 = Sha256Verifier.ComputeHex",
                StringComparison.Ordinal);

        Assert.DoesNotContain(CancellableHashStatement, mutated, StringComparison.Ordinal);
    }
}
