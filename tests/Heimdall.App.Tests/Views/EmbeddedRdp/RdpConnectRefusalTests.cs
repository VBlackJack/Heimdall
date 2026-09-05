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

using Heimdall.App.Views.EmbeddedRdp;
using Heimdall.Core.Security.Vault;

namespace Heimdall.App.Tests.Views.EmbeddedRdp;

/// <summary>
/// Freezes that a locked vault refuses the connect with its own sentence rather than failing it.
/// </summary>
public sealed class RdpConnectRefusalTests
{
    private const string RunConnectAttemptMember = "private void RunConnectAttempt(int attempt)";
    private const string RefusalClause =
        "catch (Exception ex) when (RdpConnectRefusal.StatusKeyFor(ex) is string refusalKey)";
    private const string RefusalHandling = "HandleConnectRefused(refusalKey, ex);";

    [Fact]
    public void ALockedVaultIsARefusalWithTheUnlockSentence()
    {
        Assert.Equal(
            RdpConnectRefusal.VaultLockedStatusKey,
            RdpConnectRefusal.StatusKeyFor(new VaultLockedException()));
    }

    [Theory]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(System.Runtime.InteropServices.COMException))]
    public void AnythingElseIsAFault(Type exceptionType)
    {
        Exception exception = (Exception)Activator.CreateInstance(exceptionType)!;

        Assert.Null(RdpConnectRefusal.StatusKeyFor(exception));
    }

    /// <summary>
    /// The connect attempt routes a refusal through the mapping above, as a clause of its own.
    /// </summary>
    /// <remarks>
    /// The clause is read as a step of the member and the handling as a step of the block it
    /// guards. Its position in front of the generic catch is the compiler's to enforce: a
    /// general catch placed above a filtered catch of the same type does not compile.
    /// </remarks>
    [Fact]
    public void TheConnectAttemptRoutesRefusalsThroughTheMapping()
    {
        string logic = ViewSource.HandlerLogic(RunConnectAttemptMember);

        Assert.True(
            ViewSource.IsStatementOfTheMethodBody(logic, RefusalClause),
            "The connect attempt no longer routes refusals through RdpConnectRefusal, so a locked "
                + "vault reports as a fault again.");

        string guarded = ViewSource.HandlerBody(logic, RefusalClause);

        Assert.True(
            ViewSource.IsStatementOfTheMethodBody(guarded, RefusalHandling),
            "The refusal clause no longer hands the key to HandleConnectRefused.");
    }
}
