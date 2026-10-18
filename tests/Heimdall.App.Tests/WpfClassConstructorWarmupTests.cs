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

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

using FluentAssertions;

namespace Heimdall.App.Tests;

/// <summary>
/// Pins what <see cref="WpfClassConstructorWarmup"/> must give for the BAML lock deadlock of
/// 2026-10-18 to be impossible, since the race itself cannot be produced on demand.
/// </summary>
/// <remarks>
/// The cycle is removed only if every type that can stand on either side of it is initialised
/// before any test runs. So these tests ask exactly that: the sweep had finished when the test
/// framework stopped waiting for it and when the first test started, it covers every type an enumeration written separately here finds, and no class
/// constructor failed, since a failed one leaves its type unusable for the rest of the run.
/// </remarks>
public sealed class WpfClassConstructorWarmupTests
{
    /// <summary>
    /// The types on the cycle in the dump: <c>ScrollViewer</c> on both sides of it, reached from
    /// <c>TextBox</c> through <c>TextBoxBase</c>, and <c>ComboBox</c> and <c>PasswordBox</c>
    /// among the threads queued behind it.
    /// </summary>
    public static TheoryData<Type> TypesSeenInTheDump =>
    [
        typeof(ScrollViewer),
        typeof(TextBoxBase),
        typeof(TextBox),
        typeof(ComboBox),
        typeof(PasswordBox),
    ];

    [Fact]
    public void TheWarmupHadFinishedWhenTheFirstTestStarted()
    {
        WpfClassConstructorWarmupObserved.CompletedWhenFirstTestStarted.Should().BeTrue(
            "a test that starts while the sweep still runs can initialise a type on its own thread, "
            + "which is the race the sweep exists to remove");
    }

    [Fact]
    public void TheWarmupHadFinishedWhenItReturnedToTheTestFramework()
    {
        WpfClassConstructorWarmup.CompletedWhenRunReturned.Should().BeTrue(
            "the framework must wait for the sweep; one that only started it leaves its end to timing");
    }

    [Theory]
    [MemberData(nameof(TypesSeenInTheDump))]
    public void TheWarmupCoversTheTypesSeenInTheDump(Type type)
    {
        WpfClassConstructorWarmup.Initialized.Should().Contain(type);
    }

    [Fact]
    public void TheWarmupCoversEveryPublicDependencyObjectOfTheCoreWpfAssemblies()
    {
        Type[] expected = new[] { typeof(DependencyObject), typeof(UIElement), typeof(FrameworkElement) }
            .Select(anchor => anchor.Assembly)
            .SelectMany(assembly => assembly.GetExportedTypes())
            .Where(type => !type.IsGenericTypeDefinition
                && type.TypeInitializer is not null
                && type.IsSubclassOf(typeof(DependencyObject)))
            .ToArray();

        expected.Should().Contain(typeof(ScrollViewer), "the enumeration must reach the type seen in the dump");
        WpfClassConstructorWarmup.Initialized.Should().Contain(expected);
    }

    [Fact]
    public void TheWarmupCoversTheThemeAssemblies()
    {
        WpfClassConstructorWarmup.SweptAssemblies()
            .Select(assembly => assembly.GetName().Name)
            .Should().Contain("PresentationFramework.Aero2", "the default theme on Windows 10 and 11");

        WpfClassConstructorWarmup.Initialized
            .Should().Contain(type => type.Assembly.GetName().Name == "PresentationFramework.Aero2");
    }

    [Fact]
    public void NoClassConstructorFailed()
    {
        WpfClassConstructorWarmup.Failures.Should().BeEmpty();
    }
}
