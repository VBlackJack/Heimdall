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

using System.Windows.Forms;
using System.Windows.Threading;
using Heimdall.Core.Configuration;
using Heimdall.Rdp.ActiveX;

namespace Heimdall.App.Services;

/// <summary>
/// Where a session view gets its RDP ActiveX control, and where it gives it back.
/// </summary>
/// <remarks>
/// A view used to create its own control and dispose it, which cost a measured 66 kernel
/// handles per session that the operating system never returns. Going through a provider
/// lets those controls be reused without the view knowing whether it received a fresh one.
/// <b>UI thread only.</b>
/// </remarks>
public interface IRdpHostProvider
{
    /// <summary>Returns a control ready to be configured for a session.</summary>
    RdpActiveXHost Acquire();

    /// <summary>
    /// Hands a control back once its session is over. The provider decides whether it is
    /// kept or destroyed; the caller must not touch it again either way.
    /// </summary>
    void Release(RdpActiveXHost host);
}

/// <summary>
/// Creates a control per session and destroys it afterwards, which is what Heimdall did
/// before pooling existed.
/// </summary>
/// <remarks>
/// Kept as the default so a view constructed without a provider behaves exactly as it used
/// to, and so the pooling can be taken out of the picture when diagnosing.
/// </remarks>
public sealed class TransientRdpHostProvider : IRdpHostProvider
{
    public RdpActiveXHost Acquire() => new();

    public void Release(RdpActiveXHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        host.Dispose();
    }
}

/// <summary>
/// Hands out controls from a small pool, so a session that follows another can inherit its
/// control instead of paying for a new one, and lets an idle control go once nobody has asked
/// for it in a while.
/// </summary>
/// <remarks>
/// <para><b>The expiry is what gives the memory back.</b> A pooled control holds about 300 MB
/// for as long as it sits idle; two of them kept for the life of the process is what left a
/// used Heimdall 600 MB above a fresh one after every tab was closed. A dispatcher timer asks
/// the pool to trim while anything is idle, and stops when nothing is.</para>
/// <para>Capacity and expiry are read from the live settings through the delegates the owner
/// supplies, so a change on the settings screen applies to the next release and the next trim
/// without a restart. Where no settings are reachable the pool's own defaults apply.</para>
/// <para><b>UI thread only</b>, like the controls it holds: the timer is created on the thread
/// of the first release and disposes controls on that thread.</para>
/// </remarks>
public sealed class PooledRdpHostProvider : IRdpHostProvider, IDisposable
{
    /// <summary>How often the idle controls are checked against the expiry.</summary>
    internal static readonly TimeSpan TrimInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How often released controls are checked for a finished disconnect. Short, because the
    /// disconnect normally completes within a second and a control waiting for it is a
    /// connected one still holding its resources; cheap, because it reads one COM property
    /// per waiting control and only runs while any is waiting.
    /// </summary>
    internal static readonly TimeSpan SettleInterval = TimeSpan.FromMilliseconds(250);

    private readonly ReusableHostPool<RdpActiveXHost> _pool;
    private readonly HiddenFormHostParking _parking = new();
    private DispatcherTimer? _trimTimer;
    private DispatcherTimer? _settleTimer;
    private bool _disposed;

    /// <summary>Creates a provider of fixed capacity whose idle controls never expire.</summary>
    public PooledRdpHostProvider(int capacity = ReusableHostPool<RdpActiveXHost>.DefaultCapacity)
        : this(() => capacity, static () => TimeSpan.Zero)
    {
    }

    /// <summary>Creates a provider whose capacity and idle expiry are read live.</summary>
    /// <param name="capacity">Idle controls to keep, read at every release and trim.</param>
    /// <param name="idleExpiry">How long an idle control is kept; zero or less keeps it for ever.</param>
    /// <param name="timeProvider">The clock idle time is measured by.</param>
    public PooledRdpHostProvider(
        Func<int> capacity,
        Func<TimeSpan> idleExpiry,
        TimeProvider? timeProvider = null)
    {
        _pool = new ReusableHostPool<RdpActiveXHost>(
            static () => new RdpActiveXHost(),
            capacity,
            idleExpiry,
            timeProvider,
            Core.Logging.FileLogger.Info,
            disconnectSettleTimeout: null,
            parking: _parking);
    }

    /// <summary>Controls handed out that came from the pool. Exposed for diagnostics.</summary>
    public int ReuseCount => _pool.ReuseCount;

    /// <summary>Controls the pool had to build. Exposed for diagnostics.</summary>
    public int CreationCount => _pool.CreationCount;

    /// <summary>Controls sitting idle right now.</summary>
    public int IdleCount => _pool.IdleCount;

    /// <summary>Released controls still waiting for their disconnect to settle.</summary>
    public int SettleCount => _pool.SettleCount;

    /// <summary>The number of idle controls kept, as the settings say right now.</summary>
    public int Capacity => _pool.Capacity;

    /// <summary>How long an idle control is kept, as the settings say right now.</summary>
    public TimeSpan IdleExpiry => _pool.IdleExpiry;

    /// <summary>
    /// The capacity a settings object asks for, or the pool's default when there is none.
    /// </summary>
    public static int ResolveCapacity(AppSettings? settings)
        => settings is null
            ? ReusableHostPool<RdpActiveXHost>.DefaultCapacity
            : Math.Max(0, settings.RdpHostPoolCapacity);

    /// <summary>
    /// The idle expiry a settings object asks for, or the pool's default when there is none.
    /// Zero in the settings means "never", which the pool reads as <see cref="TimeSpan.Zero"/>.
    /// </summary>
    public static TimeSpan ResolveIdleExpiry(AppSettings? settings)
        => settings is null
            ? ReusableHostPool<RdpActiveXHost>.DefaultIdleExpiry
            : settings.RdpHostPoolIdleExpiryMinutes <= 0
                ? TimeSpan.Zero
                : TimeSpan.FromMinutes(settings.RdpHostPoolIdleExpiryMinutes);

    public RdpActiveXHost Acquire() => _pool.Acquire();

    public void Release(RdpActiveXHost host)
    {
        _pool.Release(host);
        if (_pool.SettleCount > 0)
        {
            EnsureSettleTimer();
        }

        if (_pool.IdleCount > 0)
        {
            EnsureTrimTimer();
        }
    }

    /// <summary>
    /// Moves the released controls whose disconnect has finished into the pool, and disposes
    /// those that never will. Stops the settle timer once nothing is waiting.
    /// </summary>
    public int ProcessSettling()
    {
        int resolved = _pool.ProcessSettling();
        if (_pool.IdleCount > 0)
        {
            EnsureTrimTimer();
        }

        if (_pool.SettleCount == 0)
        {
            StopSettleTimer();
        }

        return resolved;
    }

    /// <summary>Disposes the idle controls that have expired, and reports how many.</summary>
    public int Trim()
    {
        int disposed = _pool.Trim();
        if (disposed > 0)
        {
            Core.Logging.FileLogger.Info(
                $"PooledRdpHostProvider: trimmed {disposed} idle control(s), idle={_pool.IdleCount}");
        }

        if (_pool.IdleCount == 0)
        {
            StopTrimTimer();
        }

        return disposed;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopTrimTimer();
        StopSettleTimer();
        _pool.Dispose();
        _parking.Dispose();
    }

    private void EnsureSettleTimer()
    {
        if (_settleTimer is not null || _disposed)
        {
            return;
        }

        _settleTimer = new DispatcherTimer(
            SettleInterval,
            DispatcherPriority.Background,
            (_, _) => ProcessSettling(),
            Dispatcher.CurrentDispatcher);
        _settleTimer.Start();
    }

    private void StopSettleTimer()
    {
        if (_settleTimer is null)
        {
            return;
        }

        _settleTimer.Stop();
        _settleTimer = null;
    }

    private void EnsureTrimTimer()
    {
        if (_trimTimer is not null || _disposed)
        {
            return;
        }

        _trimTimer = new DispatcherTimer(
            TrimInterval,
            DispatcherPriority.Background,
            (_, _) => Trim(),
            Dispatcher.CurrentDispatcher);
        _trimTimer.Start();
    }

    private void StopTrimTimer()
    {
        if (_trimTimer is null)
        {
            return;
        }

        _trimTimer.Stop();
        _trimTimer = null;
    }

    /// <summary>
    /// Parks a released control in a hidden top-level window while its disconnect settles.
    /// </summary>
    /// <remarks>
    /// The view takes the control out of its window before the asynchronous disconnect has
    /// finished, which deactivates it in place, and mstscax cannot complete a disconnect
    /// without a window. The form is never shown: it exists only so the control has a parent
    /// with a live handle. One form serves every parked control, created on first use.
    /// </remarks>
    private sealed class HiddenFormHostParking : IHostParking<RdpActiveXHost>
    {
        /// <summary>
        /// Where the parking form sits: far outside any monitor layout, so even a stray show
        /// could not be seen.
        /// </summary>
        private const int OffScreenCoordinate = -32000;

        /// <summary>Size of the parking form; small, as nothing is ever drawn in it.</summary>
        private const int FormSide = 64;

        /// <summary>
        /// Size given to a parked control. Non-zero on purpose: a 0x0 parent could make the
        /// control renegotiate its display size.
        /// </summary>
        private static readonly System.Drawing.Size ParkedControlSize = new(1024, 768);

        private HiddenForm? _form;

        public void Park(RdpActiveXHost host)
        {
            ArgumentNullException.ThrowIfNull(host);

            Form form = EnsureForm();
            host.Size = ParkedControlSize;
            form.Controls.Add(host);
            // CreateControl() is a no-op on a parent that was never shown, so the handle is
            // forced instead: the disconnect needs a live window to complete in.
            _ = host.Handle;

            // The first Disconnect() ran while the control had no window; repeating it is
            // idempotent and now has one to complete in.
            host.Disconnect();
        }

        public void Unpark(RdpActiveXHost host)
        {
            ArgumentNullException.ThrowIfNull(host);

            if (_form is not null && ReferenceEquals(host.Parent, _form))
            {
                _form.Controls.Remove(host);
            }
        }

        public void Dispose()
        {
            _form?.Dispose();
            _form = null;
        }

        private Form EnsureForm()
        {
            if (_form is null)
            {
                _form = new HiddenForm
                {
                    FormBorderStyle = FormBorderStyle.None,
                    ShowInTaskbar = false,
                    StartPosition = FormStartPosition.Manual,
                    Location = new System.Drawing.Point(OffScreenCoordinate, OffScreenCoordinate),
                    Size = new System.Drawing.Size(FormSide, FormSide),
                };

                // Forces the native window into existence without showing it.
                _ = _form.Handle;
            }

            return _form;
        }

        /// <summary>A form that never takes focus and stays out of Alt+Tab.</summary>
        private sealed class HiddenForm : Form
        {
            /// <summary>Extended style: a tool window, not listed in Alt+Tab.</summary>
            private const int WsExToolWindow = 0x00000080;

            /// <summary>Extended style: never becomes the active window.</summary>
            private const int WsExNoActivate = 0x08000000;

            protected override CreateParams CreateParams
            {
                get
                {
                    CreateParams parameters = base.CreateParams;
                    parameters.ExStyle |= WsExToolWindow | WsExNoActivate;
                    return parameters;
                }
            }
        }
    }
}
