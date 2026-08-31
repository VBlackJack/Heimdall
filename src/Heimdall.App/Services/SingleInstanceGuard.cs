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
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Heimdall.Core.Logging;

namespace Heimdall.App.Services;

/// <summary>
/// What <see cref="SingleInstanceGuard.TryAcquire" /> established.
/// </summary>
/// <remarks>
/// Three outcomes, not two. A nullable guard would have to mean both "another
/// instance owns this" and "the lock could not be inspected", and those call for
/// opposite actions: shut down in the first case, start anyway in the second.
/// </remarks>
public enum SingleInstanceOutcome
{
    /// <summary>This process owns the data root. A guard was returned.</summary>
    Owner,

    /// <summary>
    /// Another instance owns it and has been asked to come forward. This process
    /// must exit without touching the configuration.
    /// </summary>
    AlreadyRunning,

    /// <summary>
    /// The named objects could not be created or opened, so ownership is unknown.
    /// Start normally: refusing to launch because a lock could not be inspected
    /// would be a worse failure than the write race being guarded against.
    /// </summary>
    Unavailable
}

/// <summary>
/// Lets one Heimdall own a configuration directory, and hands a second launch back
/// to the instance that already has it.
/// </summary>
/// <remarks>
/// <para>
/// <c>ConfigManager</c> serializes writes with a <see cref="SemaphoreSlim" />, which
/// is process-local, and says so itself: cross-process locking and revision/CAS are
/// listed there as prerequisites "before supporting multiple Heimdall instances that
/// share one configuration directory". Until those exist, two instances both read
/// <c>servers.json</c> at startup and both write it at the end, and the second write
/// silently discards whatever the first one recorded.
/// </para>
/// <para>
/// This guard removes the situation rather than the symptom. It is not a substitute
/// for cross-process CAS: it makes the concurrent case unreachable through the
/// supported path, which is what a desktop application of this shape is expected to
/// do anyway. A user who launches Heimdall twice wants the window they already have.
/// </para>
/// <para>
/// The name is derived from the data root, not from the executable, because the
/// resource being protected is the configuration directory. Two builds pointed at one
/// directory must still exclude each other.
/// </para>
/// </remarks>
public sealed class SingleInstanceGuard : IDisposable
{
    /// <summary>
    /// Set to <c>0</c> to keep the guard from engaging.
    /// </summary>
    /// <remarks>
    /// This exists for one measured reason. The UI test host builds the real
    /// <c>Heimdall.App.App</c> and runs its startup inside the test process, against
    /// the developer's own data root. With the guard engaged, that test process owns
    /// the directory, and the end-to-end test that launches the product finds it
    /// already taken and shuts straight back down. The child inherits this variable
    /// from the test host, so one setting covers both sides.
    /// <para>
    /// It is honoured in shipped builds because a variable the product ignores is not
    /// a variable the tests can rely on. Every time it takes effect it is logged at
    /// warning level, so a support log says plainly that the protection was off.
    /// </para>
    /// </remarks>
    public const string DisableEnvironmentVariable = "HEIMDALL_SINGLE_INSTANCE";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activationEvent;
    private readonly RegisteredWaitHandle? _activationRegistration;
    private bool _disposed;

    private SingleInstanceGuard(
        Mutex mutex,
        EventWaitHandle activationEvent,
        Action onActivationRequested)
    {
        _mutex = mutex;
        _activationEvent = activationEvent;

        _activationRegistration = ThreadPool.RegisterWaitForSingleObject(
            activationEvent,
            (_, _) => onActivationRequested(),
            state: null,
            timeout: Timeout.InfiniteTimeSpan,
            executeOnlyOnce: false);
    }

    /// <summary>
    /// Builds the synchronization object names for a data root.
    /// </summary>
    /// <remarks>
    /// Separated from the handles so the derivation is testable without touching a
    /// kernel object. <c>Local\</c> scopes the names to the logon session, which is
    /// the correct boundary: each session has its own <c>LocalApplicationData</c>, so
    /// two users - or two RDP sessions - are never in conflict to begin with.
    /// </remarks>
    public static (string MutexName, string ActivationEventName) BuildNames(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);

        // Case-insensitive because Windows paths are, and trailing separators are
        // stripped so "C:\x" and "C:\x\" cannot own the directory independently.
        string normalized = dataRoot
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();

        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        string token = Convert.ToHexString(digest)[..16];

        return ($@"Local\Heimdall-Instance-{token}", $@"Local\Heimdall-Activate-{token}");
    }

    /// <summary>
    /// Takes ownership of <paramref name="dataRoot" /> for this process, or reports
    /// that another instance already holds it.
    /// </summary>
    /// <param name="dataRoot">The configuration directory being protected.</param>
    /// <param name="onActivationRequested">
    /// Invoked on a thread-pool thread when a later launch asks this instance to come
    /// forward. The caller is responsible for marshalling to the UI thread.
    /// </param>
    /// <param name="guard">
    /// The ownership handle, set only when the outcome is
    /// <see cref="SingleInstanceOutcome.Owner" />. Dispose it on shutdown.
    /// </param>
    /// <returns>Which of the three situations was found.</returns>
    public static SingleInstanceOutcome TryAcquire(
        string dataRoot,
        Action onActivationRequested,
        out SingleInstanceGuard? guard)
    {
        guard = null;
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentNullException.ThrowIfNull(onActivationRequested);

        if (IsDisabledByEnvironment())
        {
            FileLogger.Warn(
                $"[SingleInstance] disabled by {DisableEnvironmentVariable}; concurrent"
                + " instances may overwrite each other's configuration.");
            return SingleInstanceOutcome.Unavailable;
        }

        (string mutexName, string eventName) = BuildNames(dataRoot);

        Mutex? mutex = null;
        EventWaitHandle? activationEvent = null;

        try
        {
            mutex = new Mutex(initiallyOwned: true, mutexName, out bool createdNew);
            activationEvent = new EventWaitHandle(
                initialState: false,
                EventResetMode.AutoReset,
                eventName);

            if (createdNew)
            {
                guard = new SingleInstanceGuard(mutex, activationEvent, onActivationRequested);
                return SingleInstanceOutcome.Owner;
            }

            // Someone else owns the directory. Ask them to surface, then let this
            // process end. Failing to signal is not a reason to start a second
            // instance: the write hazard is the thing being avoided.
            activationEvent.Set();
            mutex.Dispose();
            activationEvent.Dispose();
            return SingleInstanceOutcome.AlreadyRunning;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException
            or WaitHandleCannotBeOpenedException)
        {
            // A named object the current token cannot open is not a second instance.
            FileLogger.Warn($"[SingleInstance] guard unavailable, starting unguarded: {ex.Message}");
            mutex?.Dispose();
            activationEvent?.Dispose();
            return SingleInstanceOutcome.Unavailable;
        }
    }

    /// <summary>
    /// Only an explicit <c>0</c> disables the guard. An unset or unrecognised value
    /// leaves protection on, so a typo cannot silently remove it.
    /// </summary>
    public static bool IsDisabledByEnvironment() =>
        Environment.GetEnvironmentVariable(DisableEnvironmentVariable) == "0";

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _activationRegistration?.Unregister(waitObject: null);

        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Not the owning thread, or already released. Disposing still frees the
            // handle, and the kernel releases an abandoned mutex when the process
            // ends, so a later launch is never locked out by this path.
        }

        _mutex.Dispose();
        _activationEvent.Dispose();
    }
}
