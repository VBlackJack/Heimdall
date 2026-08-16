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
using System.Runtime.InteropServices;

namespace Heimdall.App.Services;

/// <summary>
/// An ICA session window and the process that owns it, as observed at capture time.
/// </summary>
/// <param name="Hwnd">The session window handle.</param>
/// <param name="OwnerProcessId">The process that owned <paramref name="Hwnd"/> when it was found.</param>
internal readonly record struct CitrixSessionWindow(IntPtr Hwnd, int OwnerProcessId)
{
    /// <summary>True when both halves were actually resolved.</summary>
    internal bool IsResolved => Hwnd != IntPtr.Zero && OwnerProcessId > 0;
}

/// <summary>
/// The process surface the session handle needs.
/// </summary>
/// <remarks>
/// An interface rather than <see cref="Process"/> so the identity and liveness rules can be
/// exercised without a Citrix installation, a real window, or a real process.
/// </remarks>
internal interface ICitrixSessionProcess : IDisposable
{
    int Id { get; }

    bool HasExited { get; }

    void Kill();
}

/// <summary>
/// The operating-system facts the handle consults, injected for the same reason.
/// </summary>
/// <param name="IsWindow">Whether a window handle still refers to an existing window.</param>
/// <param name="ResolveOwnerProcessId">
/// The process that owns a window right now, or null when it cannot be resolved.
/// </param>
/// <param name="OpenProcess">Opens a process by id, or returns null when it cannot be opened.</param>
internal sealed record CitrixSessionEnvironment(
    Func<IntPtr, bool> IsWindow,
    Func<IntPtr, int?> ResolveOwnerProcessId,
    Func<int, ICitrixSessionProcess?> OpenProcess)
{
    /// <summary>The real Win32 and <see cref="Process"/> implementations.</summary>
    internal static CitrixSessionEnvironment Default { get; } = new(
        NativeIsWindow,
        ResolveWindowOwnerProcessId,
        OpenSessionProcess);

    [DllImport("user32.dll", EntryPoint = "IsWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool NativeIsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    /// <summary>
    /// Reads the creator process of a window. A zero thread id means the call failed, which is how
    /// a destroyed window reports itself, so it is surfaced as "unresolved" rather than as zero.
    /// </summary>
    private static int? ResolveWindowOwnerProcessId(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return null;
        }

        uint threadId = GetWindowThreadProcessId(hwnd, out uint processId);
        if (threadId == 0 || processId == 0)
        {
            return null;
        }

        return (int)processId;
    }

    private static ICitrixSessionProcess? OpenSessionProcess(int processId)
    {
        try
        {
            return new CitrixSessionProcess(Process.GetProcessById(processId));
        }
        catch (ArgumentException)
        {
            // No process with that id is running any more.
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}

/// <summary><see cref="Process"/> behind <see cref="ICitrixSessionProcess"/>.</summary>
internal sealed class CitrixSessionProcess : ICitrixSessionProcess
{
    private readonly Process _process;

    internal CitrixSessionProcess(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        _process = process;
    }

    public int Id => _process.Id;

    public bool HasExited => _process.HasExited;

    public void Kill() => _process.Kill();

    public void Dispose() => _process.Dispose();
}

/// <summary>
/// The session's own lifecycle handle: the real ICA window and the process that owns it.
/// </summary>
/// <remarks>
/// This exists because the Citrix launcher is not the session. The launcher exits as soon as it has
/// handed the request to Workspace, and on a shared session it never owned the window at all, so
/// driving front, health or terminate from the launcher's <see cref="Process"/> steers something
/// that is not the thing the user sees.
/// <para>
/// Liveness is a conjunction of three facts, not one: the owning process has not exited, the window
/// still exists, and the window is still owned by the process it was captured from. The third is
/// not redundant - a window handle can be recycled, so an existing handle alone says nothing about
/// whose window it now is.
/// </para>
/// </remarks>
internal sealed class CitrixSessionHandle : IDisposable
{
    /// <summary>
    /// Class-name prefix of the Workspace shell, which hosts sign-in and is never an application
    /// session. It may legitimately be embedded, but it must never be adopted as the session.
    /// </summary>
    private const string WorkspaceShellWindowClassPrefix = "HwndWrapper[SelfService";

    private readonly CitrixSessionEnvironment _environment;
    private readonly ICitrixSessionProcess _process;
    private bool _disposed;

    private CitrixSessionHandle(
        CitrixSessionWindow window,
        ICitrixSessionProcess process,
        CitrixSessionEnvironment environment)
    {
        Hwnd = window.Hwnd;
        OwnerProcessId = window.OwnerProcessId;
        _process = process;
        _environment = environment;
    }

    /// <summary>The captured ICA window. Never the launcher's main window.</summary>
    internal IntPtr Hwnd { get; }

    /// <summary>The process that owned <see cref="Hwnd"/> at capture time.</summary>
    internal int OwnerProcessId { get; }

    /// <summary>
    /// Whether this handle still designates the session it was created for.
    /// </summary>
    internal bool IsAlive
    {
        get
        {
            if (_disposed || _process.HasExited || !_environment.IsWindow(Hwnd))
            {
                return false;
            }

            // The recycled-handle check. Without it, a handle reissued to an unrelated window
            // would keep reporting a session that no longer exists.
            return _environment.ResolveOwnerProcessId(Hwnd) == OwnerProcessId;
        }
    }

    /// <summary>
    /// Adopts a captured window as the session, or refuses.
    /// </summary>
    /// <remarks>
    /// Refuses an unresolved window, the Workspace shell, an owner id that is absent, zero or no
    /// longer the one captured, and a process that cannot be opened or has already exited. There is
    /// deliberately no fallback: a refusal leaves the caller without a session handle rather than
    /// with one pointing at the launcher.
    /// </remarks>
    /// <param name="window">The window and owner observed by the capture scan.</param>
    /// <param name="windowClassName">Its class name, used to reject the sign-in shell.</param>
    /// <param name="environment">The operating-system facts to consult.</param>
    /// <param name="handle">The adopted session, or null when this returns false.</param>
    /// <returns>True when the window was adopted.</returns>
    internal static bool TryCreate(
        CitrixSessionWindow window,
        string? windowClassName,
        CitrixSessionEnvironment environment,
        out CitrixSessionHandle? handle)
    {
        ArgumentNullException.ThrowIfNull(environment);

        handle = null;

        if (!window.IsResolved || IsWorkspaceShellWindowClass(windowClassName))
        {
            return false;
        }

        int? currentOwner = environment.ResolveOwnerProcessId(window.Hwnd);
        if (currentOwner is not int ownerProcessId
            || ownerProcessId <= 0
            || ownerProcessId != window.OwnerProcessId)
        {
            return false;
        }

        ICitrixSessionProcess? process = environment.OpenProcess(ownerProcessId);
        if (process is null)
        {
            return false;
        }

        if (process.HasExited)
        {
            process.Dispose();
            return false;
        }

        handle = new CitrixSessionHandle(window, process, environment);
        return true;
    }

    /// <summary>True for the Workspace sign-in shell, which is never an application session.</summary>
    internal static bool IsWorkspaceShellWindowClass(string? windowClassName)
        => windowClassName is not null
            && windowClassName.StartsWith(
                WorkspaceShellWindowClassPrefix,
                StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Yields the window to raise, or false when there is no live session to raise.
    /// </summary>
    internal bool TryGetSessionWindow(out IntPtr hwnd)
    {
        hwnd = IntPtr.Zero;
        if (!IsAlive)
        {
            return false;
        }

        hwnd = Hwnd;
        return true;
    }

    /// <summary>
    /// Ends the session by terminating the process that owns its window, never the launcher.
    /// </summary>
    /// <returns>True when a termination was actually issued.</returns>
    internal bool Terminate()
    {
        if (!IsAlive)
        {
            return false;
        }

        try
        {
            _process.Kill();
            return true;
        }
        catch (InvalidOperationException ex)
        {
            // The process ended between the liveness check and the call.
            Core.Logging.FileLogger.Warn($"[CitrixSessionHandle] terminate: {ex.Message}");
            return false;
        }
    }

    /// <summary>Releases the process object. Does not terminate anything.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _process.Dispose();
    }
}
