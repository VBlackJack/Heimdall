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

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

using Heimdall.Core.Security;

using Xunit.Sdk;

[assembly: Heimdall.App.Tests.ProtectorMembershipRequired]

namespace Heimdall.App.Tests;

/// <summary>
/// Fails a test whose class reaches <c>CredentialProtector</c> without belonging to
/// <see cref="CredentialProtectorAppCollection"/>, by observing the call rather than by guessing
/// from source which classes might make one.
/// </summary>
/// <remarks>
/// <para><b>Why this exists beside the source census.</b> The census in
/// <see cref="CredentialProtectorCollectionMembershipTests"/> reads names, and missed
/// <c>PasswordGeneratorViewModelTests</c> for weeks because that class reaches the protector
/// through <c>PasswordPresetStorage</c> and never spells it. Widening the census to every
/// production type that seals was measured on 2026-09-20 and rejected: it flagged 13 test classes
/// of which instrumentation showed <b>0</b> actually reach the protector, and it still could not
/// see a test added later to a class it had already excused. This guard fires on the call itself,
/// so it sees both.</para>
/// <para><b>What is asked of the calling class, and why it is membership rather than scope
/// ownership.</b> The first build of this guard asked whether an
/// <see cref="AsyncLocal{T}"/> scope flag was set at the moment of the call. Measured on
/// 2026-09-20 over the whole assembly, that produced <b>four false positives</b>: classes that do
/// own a <see cref="CredentialProtectorStateScope"/> but reach the protector from a collaborator
/// whose execution context was captured before the scope field initializer ran, so the flag had
/// not flowed there. Membership is a property of the type and is immune to where the call's
/// context came from. The companion rule, that every member also owns a scope, is a fact about a
/// class rather than about a call, and stays with the source census which already checks it.</para>
/// <para><b>The observer records and does not throw.</b> Throwing at the call site brought down
/// the test host: the first real offender reached the protector from an async continuation nobody
/// awaited, so the exception surfaced unhandled and aborted the run at 473 of 7200 tests. The
/// violation is attributed to the running test by <see cref="ProtectorMembershipRequired"/>
/// instead.</para>
/// </remarks>
internal static class CredentialProtectorRuntimeGuard
{
    /// <summary>
    /// The running test's violation list, installed before the test and read after it. Held as a
    /// reference and mutated in place: a value assigned deeper in the async flow would not be
    /// visible again in the After hook, but a mutation of the list it already points at is.
    /// </summary>
    private static readonly AsyncLocal<List<string>?> s_currentTestViolations = new();

    /// <summary>
    /// Violations seen with no test in scope, which nothing can attribute to anyone. Surfaced by
    /// <c>CredentialProtectorRuntimeGuardTests.NothingReachedTheProtectorOutsideATest</c>.
    /// </summary>
    private static readonly ConcurrentQueue<string> s_unattributed = new();

    internal static IReadOnlyCollection<string> Unattributed => s_unattributed;

    [ModuleInitializer]
    internal static void Arm() => CredentialProtector.CryptoCallObserver = Observe;

    internal static void BeginTest() => s_currentTestViolations.Value = [];

    internal static IReadOnlyList<string> EndTest()
    {
        List<string> recorded = s_currentTestViolations.Value ?? [];
        s_currentTestViolations.Value = null;
        return recorded;
    }

    /// <summary>
    /// Whether a class reaching the protector would be reported. The decision on its own, so the
    /// guard's tests can pin it without having to arrange a stack.
    /// </summary>
    internal static bool WouldReport(Type testClass)
    {
        ArgumentNullException.ThrowIfNull(testClass);

        // xUnit 2.x keeps the collection name in the attribute's constructor argument and exposes
        // no property for it, so it is read from the attribute data rather than an instance.
        return !testClass
            .GetCustomAttributesData()
            .Where(data => data.AttributeType == typeof(CollectionAttribute))
            .Any(data => data.ConstructorArguments.Count == 1
                && (string?)data.ConstructorArguments[0].Value == CredentialProtectorAppCollection.Name);
    }

    /// <summary>
    /// Captures what the guard sees during an action instead of letting it reach the running
    /// test, so a violation can be provoked on purpose without failing the prover.
    /// </summary>
    internal static IReadOnlyList<string> Observing(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        List<string>? outer = s_currentTestViolations.Value;
        List<string> mine = [];
        s_currentTestViolations.Value = mine;
        try
        {
            action();
        }
        finally
        {
            s_currentTestViolations.Value = outer;
        }

        lock (mine)
        {
            return mine.ToList();
        }
    }

    private static void Observe()
    {
        Type? caller = CallingTestClass();
        if (caller is null || !WouldReport(caller))
        {
            return;
        }

        string message =
            $"{caller.FullName} reached CredentialProtector without joining the "
            + $"{CredentialProtectorAppCollection.Name} collection. Sealing or unsealing reads "
            + "process-global key slots that another test class can change at that instant, so this "
            + $"class must join the collection and own a {nameof(CredentialProtectorStateScope)}. A "
            + "value written under one key and read back under another does not throw: it returns an "
            + "empty store, and the failure surfaces far from here.";

        List<string>? current = s_currentTestViolations.Value;
        if (current is null)
        {
            s_unattributed.Enqueue(message);
            return;
        }

        lock (current)
        {
            current.Add(message);
        }
    }

    /// <summary>
    /// The nearest test class on the stack, walked out to its outermost declaring type.
    /// Measured on 2026-09-20: without that walk an async test body reports as its compiler
    /// generated state machine, <c>Class+&lt;Method&gt;d__16</c>, and a hand-written nested helper
    /// such as a <c>Harness</c> or a <c>LaunchFixture</c> reports as itself. Neither carries the
    /// outer class's attributes, so both were reported although the class they belong to is a
    /// member. Unwrapping only the compiler-generated ones fixes the first and leaves the second.
    /// </summary>
    private static Type? CallingTestClass()
    {
        var trace = new StackTrace(skipFrames: 2, fNeedFileInfo: false);
        for (int i = 0; i < trace.FrameCount; i++)
        {
            Type? declaring = trace.GetFrame(i)?.GetMethod()?.DeclaringType;
            if (declaring?.Assembly != typeof(CredentialProtectorRuntimeGuard).Assembly)
            {
                continue;
            }

            while (declaring is { IsNested: true, DeclaringType: not null })
            {
                declaring = declaring.DeclaringType;
            }

            if (declaring is null
                || declaring == typeof(CredentialProtectorRuntimeGuard)
                || declaring == typeof(ProtectorMembershipRequired))
            {
                continue;
            }

            return declaring;
        }

        return null;
    }
}

/// <summary>
/// Applied to the assembly, so every test in it is watched. Opens a violation list before each
/// test and fails the test after it if its class reached the protector from outside the
/// collection.
/// </summary>
/// <remarks>
/// Attribution is the whole point of the hook: the observer cannot throw where the call happens,
/// because a call made on an unawaited continuation takes the test host down with it rather than
/// failing one test.
/// </remarks>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
public sealed class ProtectorMembershipRequired : BeforeAfterTestAttribute
{
    /// <inheritdoc />
    public override void Before(MethodInfo methodUnderTest)
        => CredentialProtectorRuntimeGuard.BeginTest();

    /// <inheritdoc />
    public override void After(MethodInfo methodUnderTest)
    {
        IReadOnlyList<string> violations = CredentialProtectorRuntimeGuard.EndTest();
        if (violations.Count == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            string.Join(Environment.NewLine, violations.Distinct(StringComparer.Ordinal)));
    }
}
