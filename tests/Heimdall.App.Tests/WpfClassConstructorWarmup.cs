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

using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

using Xunit.Abstractions;
using Xunit.Sdk;

[assembly: Heimdall.App.Tests.WpfClassConstructorWarmupObserved]
[assembly: TestFramework(
    "Heimdall.App.Tests.WpfClassConstructorWarmupTestFramework",
    "Heimdall.App.Tests")]

namespace Heimdall.App.Tests;

/// <summary>
/// Runs the class constructor of every WPF dependency object type once, on one STA thread,
/// before any test in this assembly starts.
/// </summary>
/// <remarks>
/// <para><b>The deadlock this removes.</b> On 2026-10-18 a local full run of this assembly hung
/// for over an hour at 0% progress (the suite otherwise finishes in about 1 m 45 s). A dump of the
/// test host (<c>dotnet-dump collect</c>, then <c>clrstack -all</c>) showed a lock-order cycle
/// between two STA tests:</para>
/// <list type="bullet">
/// <item><c>FolderViewModelProjectionTests.FilterPass_KeepsTheFocusedContainerAndItsFocus</c> held
/// WPF's shared BAML schema lock (<c>WpfSharedBamlSchemaContext</c>) inside
/// <c>SystemResources.LookupResourceInDictionary</c> -&gt; <c>ResourceDictionary.CreateObject</c>
/// -&gt; <c>Baml2006Reader</c> -&gt;
/// <c>Create_BamlProperty_ScrollViewer_HorizontalScrollBarVisibility</c>, and needed
/// <c>ScrollViewer</c>'s class constructor.</item>
/// <item><c>SessionTreeGestureTests.RightClickOutsideAMultiSelection_StillSelectsThePressedRow</c>
/// was inside that class constructor (<c>TextBox..cctor</c> -&gt; <c>TextBoxBase..cctor</c> -&gt;
/// <c>ScrollViewer..cctor</c>, which builds its default template in code) and waited for the BAML
/// lock in <c>WpfSharedBamlSchemaContext.GetKnownXamlType</c>.</item>
/// </list>
/// <para>About a dozen other STA tests then queued behind them on <c>ComboBox</c> and
/// <c>PasswordBox</c> class constructors and <c>SystemResources.FindResourceInternal</c>.</para>
/// <para><b>Why every type and not the five named above.</b> The cycle needs one thread to run a
/// class constructor that takes the BAML lock while another thread, holding that lock, needs the
/// same type initialised. A class constructor runs once per process: once it has finished, the
/// type can no longer stand on either side of the cycle. Initialising only the types seen in one
/// dump would lower the odds and leave every other control that builds a template in code, or
/// reads known XAML types, free to form the same cycle in a later run. Sweeping every dependency
/// object type of the WPF assemblies and their theme assemblies removes the cycle for all of them,
/// because no test can start before the sweep ends. <c>Heimdall.App</c> declares no class
/// constructors of its own, so it adds nothing to the sweep.</para>
/// <para><b>Why the test framework runs it.</b> xunit builds
/// <see cref="WpfClassConstructorWarmupTestFramework"/> before it runs any test of this assembly,
/// and the framework waits for the sweep, so there is no window in which a test could race it.
/// A module initializer would run even earlier, and was tried first: measured on 2026-10-18 it
/// hangs the test host before the first test. The initializer runs inside the class constructor
/// of <c>&lt;Module&gt;</c>, and every method of this assembly, the sweep thread's own body
/// included, waits for that constructor to finish before it can run, so joining the thread from
/// there never returns.</para>
/// <para><b>Why a test cannot reproduce the race.</b> It needs first-time initialisation of the
/// same types on several threads at the same instant, and a passing run proves nothing.
/// <c>WpfClassConstructorWarmupTests</c> pins the cause instead: that the sweep had finished when
/// the first test started, that it covered every type an independent enumeration finds, and that
/// no class constructor failed.</para>
/// </remarks>
internal static class WpfClassConstructorWarmup
{
    /// <summary>
    /// Theme assemblies WPF picks its default styles from. Their chrome types are dependency
    /// objects created under the BAML lock while a theme dictionary loads, which is exactly the
    /// side of the cycle the dump showed.
    /// </summary>
    internal static readonly IReadOnlyList<string> ThemeAssemblyNames =
    [
        "PresentationFramework.Aero",
        "PresentationFramework.Aero2",
        "PresentationFramework.AeroLite",
        "PresentationFramework.Classic",
        "PresentationFramework.Luna",
        "PresentationFramework.Royale",
        "PresentationFramework.Fluent",
    ];

    private static readonly List<Type> s_initialized = [];
    private static readonly List<string> s_failures = [];
    private static bool s_completed;

    /// <summary>Whether the sweep ran to its end.</summary>
    internal static bool Completed
    {
        get => Volatile.Read(ref s_completed);
        private set => Volatile.Write(ref s_completed, value);
    }

    /// <summary>
    /// Whether the sweep had finished when <see cref="Run"/> returned, which is what makes the
    /// test framework wait for it. Recorded apart from <see cref="Completed"/> because discovery
    /// takes seconds in the same host before the first test starts, so a sweep the framework did
    /// not wait for still finishes before any test in practice, by timing alone.
    /// </summary>
    internal static bool CompletedWhenRunReturned { get; private set; }

    /// <summary>The types whose class constructor the sweep ran.</summary>
    internal static IReadOnlyList<Type> Initialized => s_initialized;

    /// <summary>Class constructors that threw, each with its type and exception.</summary>
    internal static IReadOnlyList<string> Failures => s_failures;

    /// <summary>How long the sweep took, for the record in a test log.</summary>
    internal static TimeSpan Elapsed { get; private set; }

    private static readonly Lock s_runGate = new();

    /// <summary>
    /// Runs the sweep and waits for it. Idempotent: xunit may build the framework more than once
    /// in one process, for discovery and for execution.
    /// </summary>
    internal static void Run()
    {
        lock (s_runGate)
        {
            if (!Completed)
            {
                RunOnce();
            }

            CompletedWhenRunReturned = Completed;
        }
    }

    private static void RunOnce()
    {
        var stopwatch = Stopwatch.StartNew();

        var thread = new Thread(InitializeAll) { Name = nameof(WpfClassConstructorWarmup) };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Elapsed = stopwatch.Elapsed;
    }

    /// <summary>
    /// The assemblies the sweep covers: the three WPF core assemblies, and every theme assembly
    /// this runtime ships.
    /// </summary>
    internal static IReadOnlyList<Assembly> SweptAssemblies()
    {
        var assemblies = new List<Assembly>
        {
            typeof(DependencyObject).Assembly,
            typeof(UIElement).Assembly,
            typeof(FrameworkElement).Assembly,
        };

        AssemblyName framework = typeof(FrameworkElement).Assembly.GetName();
        foreach (string name in ThemeAssemblyNames)
        {
            var themeName = new AssemblyName(name)
            {
                Version = framework.Version,
                CultureInfo = framework.CultureInfo,
            };
            themeName.SetPublicKeyToken(framework.GetPublicKeyToken());

            try
            {
                assemblies.Add(Assembly.Load(themeName));
            }
            catch (Exception ex) when (ex is FileNotFoundException or FileLoadException)
            {
                // A theme this runtime does not ship cannot be loaded by WPF either.
            }
        }

        return assemblies;
    }

    private static void InitializeAll()
    {
        foreach (Assembly assembly in SweptAssemblies())
        {
            foreach (Type type in LoadableTypes(assembly))
            {
                if (type.IsGenericTypeDefinition
                    || type.TypeInitializer is null
                    || !typeof(DependencyObject).IsAssignableFrom(type))
                {
                    continue;
                }

                try
                {
                    RuntimeHelpers.RunClassConstructor(type.TypeHandle);
                    s_initialized.Add(type);
                }
                catch (TypeInitializationException ex)
                {
                    s_failures.Add($"{type.FullName}: {ex.InnerException ?? ex}");
                }
            }
        }

        // Set by the sweep thread itself rather than by the caller after its Join, so that a caller
        // which stopped waiting would be seen by WpfClassConstructorWarmupObserved.
        Completed = true;

        // A class constructor may have created a dispatcher for this thread. Shut it down as
        // StaDispatcherRunner does, rather than leave a dispatcher whose thread has exited.
        Dispatcher.FromThread(Thread.CurrentThread)?.InvokeShutdown();
    }

    private static IEnumerable<Type> LoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.OfType<Type>();
        }
    }
}

/// <summary>
/// The default xunit framework, preceded by <see cref="WpfClassConstructorWarmup"/>.
/// </summary>
public sealed class WpfClassConstructorWarmupTestFramework : XunitTestFramework
{
    /// <summary>Runs the warm-up, then builds the default framework.</summary>
    public WpfClassConstructorWarmupTestFramework(IMessageSink messageSink)
        : base(messageSink)
    {
        WpfClassConstructorWarmup.Run();
    }
}

/// <summary>
/// Applied to the assembly. Records whether the warm-up had finished when the first test started,
/// which is the property the warm-up exists to give.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
public sealed class WpfClassConstructorWarmupObserved : BeforeAfterTestAttribute
{
    private static int s_observed;

    /// <summary>
    /// The warm-up's state at the start of the first test; <see langword="null"/> before any test
    /// has started.
    /// </summary>
    internal static bool? CompletedWhenFirstTestStarted { get; private set; }

    /// <inheritdoc />
    public override void Before(MethodInfo methodUnderTest)
    {
        if (Interlocked.Exchange(ref s_observed, 1) == 0)
        {
            CompletedWhenFirstTestStarted = WpfClassConstructorWarmup.Completed;
        }
    }
}
