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
using System.Windows.Threading;
using Heimdall.Core.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace Heimdall.App.Services;

/// <summary>A one-shot timer the clipboard clear waits on; replaced by a fake in tests.</summary>
internal interface ISecretClearTimer
{
    /// <summary>Raised when the delay has elapsed.</summary>
    event EventHandler Elapsed;

    /// <summary>Starts the countdown again from zero.</summary>
    void Restart(TimeSpan delay);

    /// <summary>Stops the countdown.</summary>
    void Stop();
}

/// <summary>Where the application announces that it is exiting; replaced by a fake in tests.</summary>
internal interface IApplicationExitSource
{
    /// <summary>Raised once when the application exits.</summary>
    event EventHandler Exiting;
}

/// <summary>
/// Copies a secret (a private key) through <see cref="SecretClipboardGuard"/> and takes it back
/// after a delay, when the tool closes, and when the application exits, so a key does not stay
/// on the clipboard once the program is gone. A clear only removes the value this class put
/// there.
/// </summary>
internal sealed class TimedSecretClipboard : IDisposable
{
    /// <summary>How long a copied private key stays on the clipboard.</summary>
    internal const int ClearDelaySeconds = 30;

    private static readonly TimeSpan ClearDelay = TimeSpan.FromSeconds(ClearDelaySeconds);

    private readonly SecretClipboardGuard _guard;
    private readonly ISecretClearTimer _timer;
    private readonly IApplicationExitSource _exitSource;
    private bool _disposed;

    public TimedSecretClipboard(
        IClipboardService clipboard,
        ISecretClearTimer timer,
        IApplicationExitSource exitSource)
    {
        ArgumentNullException.ThrowIfNull(timer);
        ArgumentNullException.ThrowIfNull(exitSource);

        _guard = new SecretClipboardGuard(clipboard);
        _timer = timer;
        _exitSource = exitSource;
        _timer.Elapsed += OnElapsed;
        _exitSource.Exiting += OnExiting;
    }

    /// <summary>Builds an instance on the application's clipboard, a UI timer and the application exit event.</summary>
    public static TimedSecretClipboard CreateDefault()
        => new(
            (Application.Current as App)?.Services?.GetService<IClipboardService>() ?? new WpfClipboardService(),
            new DispatcherSecretClearTimer(),
            new WpfApplicationExitSource());

    /// <summary>Copies <paramref name="text"/> as a secret and arms the clear. False when the clipboard is busy.</summary>
    public bool TryCopy(string text)
    {
        if (!_guard.TryCopy(text, armAutoClear: true))
        {
            return false;
        }

        _timer.Restart(ClearDelay);
        return true;
    }

    /// <summary>Finishes a pending clear when the tool is closed.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _timer.Elapsed -= OnElapsed;
        _exitSource.Exiting -= OnExiting;
        ClearQuietly();
    }

    private void OnElapsed(object? sender, EventArgs e)
    {
        _timer.Stop();
        ClearQuietly();
    }

    private void OnExiting(object? sender, EventArgs e)
    {
        _timer.Stop();
        ClearQuietly();
    }

    /// <summary>Clears the clipboard when it still holds the secret; never throws, since this runs during shutdown.</summary>
    private void ClearQuietly()
    {
        try
        {
            _guard.ClearIfStillHeld();
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"Could not clear the private key from the clipboard: {ex.GetType().Name}");
        }
    }

    private sealed class DispatcherSecretClearTimer : ISecretClearTimer
    {
        private readonly DispatcherTimer _timer = new();

        public DispatcherSecretClearTimer() => _timer.Tick += (_, _) => Elapsed?.Invoke(this, EventArgs.Empty);

        public event EventHandler? Elapsed;

        public void Restart(TimeSpan delay)
        {
            _timer.Stop();
            _timer.Interval = delay;
            _timer.Start();
        }

        public void Stop() => _timer.Stop();
    }

    private sealed class WpfApplicationExitSource : IApplicationExitSource
    {
        public event EventHandler? Exiting
        {
            add
            {
                if (Application.Current is { } application)
                {
                    _handlers += value;
                    application.Exit -= OnApplicationExit;
                    application.Exit += OnApplicationExit;
                }
            }
            remove
            {
                _handlers -= value;
                if (_handlers is null && Application.Current is { } application)
                {
                    application.Exit -= OnApplicationExit;
                }
            }
        }

        private EventHandler? _handlers;

        private void OnApplicationExit(object sender, ExitEventArgs e) => _handlers?.Invoke(this, EventArgs.Empty);
    }
}
