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
using System.Runtime.Versioning;

using Heimdall.App.Services;
using Heimdall.Core.Security;

namespace Heimdall.App.Tests;

/// <summary>
/// Proves the runtime guard reports what it claims to, and stays silent when it should.
/// </summary>
/// <remarks>
/// A guard never seen to fail is indistinguishable from one that cannot fail. The violation is
/// provoked on purpose here rather than waiting for a regression to supply one.
/// </remarks>
[SupportedOSPlatform("windows")]
[Collection(CredentialProtectorAppCollection.Name)]
public sealed class CredentialProtectorRuntimeGuardTests : IDisposable
{
    private readonly CredentialProtectorStateScope _protectorState = new();

    public void Dispose() => _protectorState.Dispose();

    /// <summary>
    /// The decision itself: a class outside the collection is reported, one inside it is not.
    /// </summary>
    [Fact]
    public void TheDecision_TurnsOnMembershipAndNothingElse()
    {
        Assert.True(CredentialProtectorRuntimeGuard.WouldReport(typeof(ProtectorGuardOutsiderProbeTests)));
        Assert.False(CredentialProtectorRuntimeGuard.WouldReport(typeof(CredentialProtectorRuntimeGuardTests)));

        // The class whose omission started this. It is a member now, and must stay one.
        Assert.False(CredentialProtectorRuntimeGuard.WouldReport(typeof(PasswordGeneratorViewModelTests)));
    }

    /// <summary>
    /// A member calling the protector is not reported, so the guard reacts to membership and not
    /// merely to the protector being touched at all.
    /// </summary>
    [Fact]
    public void AMemberTouchingTheProtector_IsNotReported()
    {
        IReadOnlyList<string> seen = CredentialProtectorRuntimeGuard.Observing(
            () => CredentialProtector.Protect("anything"));

        Assert.Empty(seen);
    }

    /// <summary>
    /// Nothing reached the protector outside a test, where no failure could be attributed. A
    /// non-empty list here is a real finding needing a home, not noise.
    /// </summary>
    [Fact]
    public void NothingReachedTheProtectorOutsideATest()
        => Assert.Empty(CredentialProtectorRuntimeGuard.Unattributed);
}

/// <summary>
/// Deliberately <b>not</b> a member of the collection: it is the positive control, and proves the
/// guard reports a real call made from outside.
/// </summary>
/// <remarks>
/// Its calls are wrapped in <c>Observing</c>, which captures the violation instead of letting it
/// reach the running test, so provoking one does not fail this class. Nothing is read back, so a
/// sibling changing the key slots mid-call cannot affect the result.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class ProtectorGuardOutsiderProbeTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), nameof(ProtectorGuardOutsiderProbeTests), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    /// <summary>A direct call from a non-member is reported.</summary>
    [Fact]
    public void ADirectCallFromOutsideTheCollection_IsReported()
    {
        IReadOnlyList<string> seen = CredentialProtectorRuntimeGuard.Observing(
            () => CredentialProtector.Protect("anything"));

        Assert.NotEmpty(seen);
        Assert.Contains(seen, message => message.Contains(nameof(ProtectorGuardOutsiderProbeTests), StringComparison.Ordinal));
    }

    /// <summary>
    /// And so is a call made through a collaborator the test never names, which is the shape the
    /// source census could not see and that shipped undetected.
    /// </summary>
    [Fact]
    public void ACallThroughACollaboratorTheTestNeverNames_IsReportedJustTheSame()
    {
        IReadOnlyList<string> seen = CredentialProtectorRuntimeGuard.Observing(
            () => new PasswordPresetStorage(_directory).Save(new PasswordGeneratorStore()));

        Assert.NotEmpty(seen);
        Assert.Contains(seen, message => message.Contains(nameof(ProtectorGuardOutsiderProbeTests), StringComparison.Ordinal));
    }
}
