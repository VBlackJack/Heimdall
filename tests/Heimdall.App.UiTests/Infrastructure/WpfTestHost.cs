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
using System.Windows.Threading;
using System.Xml.Linq;
using Heimdall.App.Localization;
using Heimdall.Core.Localization;
using WpfApplication = System.Windows.Application;

namespace Heimdall.App.UiTests.Infrastructure;

/// <summary>
/// Lazily starts a shared STA thread hosting a WPF <see cref="WpfApplication"/>
/// and its dispatcher for the UI smoke tests.
/// </summary>
public static class WpfTestHost
{
    private static readonly object Sync = new();
    private static Dispatcher? _dispatcher;
    private static Thread? _thread;
    private static WpfApplication? _application;
    private static LocalizationManager? _localizer;
    private static Exception? _startupException;
    private static string? _repoRoot;

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

            // This host builds the real application, so its startup takes the
            // single-instance guard on the developer's own data root. Left engaged, the
            // test process owns the directory and the end-to-end test that launches the
            // product finds it taken and exits at once. Set before the App is built, and
            // inherited by every process the tests launch.
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
                    }

                    _application.ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;

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
