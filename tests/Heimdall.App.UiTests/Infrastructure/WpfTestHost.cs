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

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using System.Xml.Linq;
using Heimdall.App.Localization;
using Heimdall.App.Services;
using Heimdall.Core.Localization;
using Microsoft.Extensions.DependencyInjection;
using WpfApplication = System.Windows.Application;

namespace Heimdall.App.UiTests.Infrastructure;

/// <summary>
/// Lazily starts a shared STA thread hosting a WPF <see cref="WpfApplication"/>
/// and its dispatcher for the UI smoke tests.
/// </summary>
public static class WpfTestHost
{
    private const double HostMainWindowSize = 1;
    private const double HostMainWindowOffscreenPosition = -10_000;

    private static readonly object Sync = new();
    private static Dispatcher? _dispatcher;
    private static Thread? _thread;
    private static WpfApplication? _application;
    private static LocalizationManager? _localizer;
    private static Exception? _startupException;
    private static string? _repoRoot;

    /// <summary>
    /// What the views find in <c>App.Services</c> under this host. Only services that hold
    /// no state and reach neither the user's profile nor the network belong here: the
    /// product container they used to get was built over the developer's own data root.
    /// </summary>
    public static IServiceProvider HostServices { get; } = new ServiceCollection()
        .AddSingleton<IUiDispatcher, WpfUiDispatcher>()
        .BuildServiceProvider();

    public static Dispatcher Dispatcher
    {
        get
        {
            EnsureStarted();
            return _dispatcher!;
        }
    }

    public static LocalizationManager Localizer
    {
        get
        {
            EnsureStarted();
            return _localizer!;
        }
    }

    public static string RepoRoot
    {
        get
        {
            EnsureStarted();
            return _repoRoot!;
        }
    }

    /// <summary>
    /// Resolves the Heimdall.App build output directory for a given configuration by reading
    /// the app project's current target framework, so a TFM bump does not break UI test paths.
    /// Returns the matching directory that contains Heimdall.exe, or null when none is present.
    /// </summary>
    public static string? ResolveAppBuildDir(string configuration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configuration);

        var configRoot = Path.Combine(RepoRoot, "src", "Heimdall.App", "bin", configuration);
        if (!Directory.Exists(configRoot))
        {
            return null;
        }

        var targetFrameworks = ResolveAppTargetFrameworks();
        if (targetFrameworks.Count == 0)
        {
            return null;
        }

        return Directory.GetDirectories(configRoot)
            .Select(dir => (Path: dir, TargetFramework: new DirectoryInfo(dir).Name))
            .Where(candidate => targetFrameworks.Contains(candidate.TargetFramework))
            .Where(candidate => File.Exists(Path.Combine(candidate.Path, "Heimdall.exe")))
            .OrderByDescending(candidate => candidate.TargetFramework.Length)
            .ThenBy(candidate => candidate.Path, StringComparer.Ordinal)
            .Select(candidate => candidate.Path)
            .FirstOrDefault();
    }

    public static void EnsureStarted()
    {
        if (_dispatcher is not null)
        {
            return;
        }

        lock (Sync)
        {
            if (_dispatcher is not null)
            {
                return;
            }

            // This host builds the real application for its resources and dispatcher only.
            // WPF queues the startup event from the App constructor, so without this switch
            // the first dispatcher pump runs the product startup in the test process,
            // against the developer's own data root: the scheduled task engine, the health
            // monitor and every writer of the profile. Set before the App is built.
            Heimdall.App.App.SuppressProductLifecycle = true;

            // Inherited by every process the tests launch: the end-to-end test starts the
            // real executable, which would otherwise hand over to a Heimdall the developer
            // already has open on the same data root and exit at once.
            Environment.SetEnvironmentVariable(
                Heimdall.App.Services.SingleInstanceGuard.DisableEnvironmentVariable, "0");

            using var ready = new ManualResetEventSlim(false);
            _thread = new Thread(() =>
            {
                try
                {
                    _repoRoot = LocateRepoRoot();
                    typeof(WpfApplication)
                        .GetField("_resourceAssembly", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
                        ?.SetValue(null, typeof(Heimdall.App.App).Assembly);
                    _application = WpfApplication.Current ?? new Heimdall.App.App();
                    if (_application is Heimdall.App.App app)
                    {
                        app.InitializeComponent();
                        app.HostServices = HostServices;
                    }

                    _application.ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
                    _application.MainWindow = CreateHostMainWindow();

                    _localizer = new LocalizationManager();
                    _localizer.LoadAsync(Path.Combine(_repoRoot, "locales"), "en").GetAwaiter().GetResult();
                    LocalizationSource.Instance.Initialize(_localizer);

                    _dispatcher = Dispatcher.CurrentDispatcher;
                }
                catch (Exception ex)
                {
                    _startupException = ex;
                }
                finally
                {
                    ready.Set();
                }

                if (_startupException is null)
                {
                    Dispatcher.Run();
                }
            })
            {
                IsBackground = true,
                Name = "WpfTestHost-STA"
            };

            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();

            if (!ready.Wait(TimeSpan.FromSeconds(60)))
            {
                throw new InvalidOperationException("WpfTestHost failed to start within 60 seconds.");
            }

            if (_startupException is not null)
            {
                throw new InvalidOperationException("WpfTestHost failed to start.", _startupException);
            }

            AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown();
        }
    }

    public static void Invoke(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        EnsureStarted();
        _dispatcher!.Invoke(action);
    }

    public static T Invoke<T>(Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        EnsureStarted();
        return _dispatcher!.Invoke(action);
    }

    public static void ResetLocale()
    {
        SwitchLocale("en");
    }

    public static void SwitchLocale(string locale)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(locale);
        EnsureStarted();
        Invoke(() =>
        {
            LocalizationSource.Instance.Initialize(_localizer!);

            if (string.Equals(_localizer!.CurrentLocale, locale, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _localizer.SwitchLocaleAsync(locale).GetAwaiter().GetResult();
            LocalizationSource.Instance.Initialize(_localizer);
        });
    }

    public static string Translate(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        EnsureStarted();
        return Invoke(() => _localizer![key]);
    }

    private static void Shutdown()
    {
        var dispatcher = _dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        try
        {
            dispatcher.Invoke(() =>
            {
                _application?.Shutdown();
                dispatcher.InvokeShutdown();
            });
        }
        catch
        {
            // Process is exiting; ignore teardown failures.
        }
    }

    /// <summary>
    /// The window the dialog service takes as owner, for the whole run. WPF otherwise makes
    /// the first window ever created the main window, which is a test's own window, closed
    /// when that test ends; every dialog shown later then failed to take a closed window as
    /// its owner. The product startup used to provide the real main window, which is what
    /// the suite relied on without saying so.
    /// </summary>
    private static Window CreateHostMainWindow()
    {
        Window window = new()
        {
            Title = nameof(WpfTestHost),
            Width = HostMainWindowSize,
            Height = HostMainWindowSize,
            Left = HostMainWindowOffscreenPosition,
            Top = HostMainWindowOffscreenPosition,
            WindowStyle = WindowStyle.None,
            WindowStartupLocation = WindowStartupLocation.Manual,
            ShowInTaskbar = false,
            ShowActivated = false
        };

        // An owner must have been shown once.
        window.Show();
        return window;
    }

    private static HashSet<string> ResolveAppTargetFrameworks()
    {
        var appProjectPath = Path.Combine(RepoRoot, "src", "Heimdall.App", "Heimdall.App.csproj");
        if (!File.Exists(appProjectPath))
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        return XDocument.Load(appProjectPath)
            .Descendants()
            .Where(element => element.Name.LocalName is "TargetFramework" or "TargetFrameworks")
            .SelectMany(element => element.Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string LocateRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Heimdall.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate Heimdall.slnx from AppContext.BaseDirectory.");
    }
}
