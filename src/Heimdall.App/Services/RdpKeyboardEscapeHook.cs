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

using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Threading;
using Heimdall.App.Views;
using Heimdall.Core.Logging;

namespace Heimdall.App.Services;

/// <summary>
/// Thread-local keyboard hook that lets users release focus from the embedded
/// RDP ActiveX surface and toggle fullscreen while the ActiveX owns keyboard input.
/// </summary>
internal static class RdpKeyboardEscapeHook
{
    private const int WhKeyboard = 2;
    private const int HcAction = 0;
    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;
    private const int VkLwin = 0x5B;
    private const int VkRwin = 0x5C;

    private static readonly object SyncRoot = new();
    private static readonly Dictionary<object, RegisteredView> RegisteredViews = new();
    private static readonly KeyboardHookProc HookProc = OnKeyboardHook;

    private static IntPtr _hookHandle;
    private static uint _hookThreadId;
    private static bool _probeInstalled;

    internal static Action<bool>? InstallProbe { get; set; }

    internal static int RegisteredViewCount
    {
        get
        {
            lock (SyncRoot)
            {
                return RegisteredViews.Count;
            }
        }
    }

    internal static bool IsRegisteredRdpViewFocused()
    {
        lock (SyncRoot)
        {
            if (RegisteredViews.Count == 0)
            {
                return false;
            }
        }

        return FindFocusedRdpView() is not null;
    }

    public static bool Register(EmbeddedRdpView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        return RegisterCore(view, view);
    }

    public static void Unregister(EmbeddedRdpView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        UnregisterCore(view);
    }

    internal static bool RegisterForTests(object viewKey)
    {
        ArgumentNullException.ThrowIfNull(viewKey);
        return RegisterCore(viewKey, null);
    }

    internal static void UnregisterForTests(object viewKey)
    {
        ArgumentNullException.ThrowIfNull(viewKey);
        UnregisterCore(viewKey);
    }

    internal static void ResetForTests()
    {
        lock (SyncRoot)
        {
            RegisteredViews.Clear();
            _hookHandle = IntPtr.Zero;
            _hookThreadId = 0;
            _probeInstalled = false;
            InstallProbe = null;
        }
    }

    private static bool RegisterCore(object viewKey, EmbeddedRdpView? view)
    {
        lock (SyncRoot)
        {
            if (RegisteredViews.ContainsKey(viewKey))
            {
                return true;
            }

            if (RegisteredViews.Count == 0 && !InstallHook())
            {
                return false;
            }

            RegisteredViews.Add(viewKey, new RegisteredView(view));
            return true;
        }
    }

    private static void UnregisterCore(object viewKey)
    {
        lock (SyncRoot)
        {
            if (!RegisteredViews.Remove(viewKey) || RegisteredViews.Count > 0)
            {
                return;
            }

            UninstallHook();
        }
    }

    private static bool InstallHook()
    {
        if (InstallProbe is not null)
        {
            InstallProbe(true);
            _probeInstalled = true;
            return true;
        }

        _hookThreadId = GetCurrentThreadId();
        _hookHandle = SetWindowsHookEx(WhKeyboard, HookProc, IntPtr.Zero, _hookThreadId);
        if (_hookHandle == IntPtr.Zero)
        {
            FileLogger.Error(
                $"RDP keyboard escape hook could not be installed. Win32 error={Marshal.GetLastWin32Error()}");
            _hookThreadId = 0;
            return false;
        }

        FileLogger.Info($"RDP keyboard escape hook installed on thread {_hookThreadId}.");
        return true;
    }

    private static void UninstallHook()
    {
        if (InstallProbe is not null)
        {
            if (_probeInstalled)
            {
                InstallProbe(false);
            }

            _probeInstalled = false;
            return;
        }

        if (_hookHandle == IntPtr.Zero)
        {
            _hookThreadId = 0;
            return;
        }

        var hook = _hookHandle;
        _hookHandle = IntPtr.Zero;
        _hookThreadId = 0;

        if (!UnhookWindowsHookEx(hook))
        {
            FileLogger.Error(
                $"RDP keyboard escape hook could not be uninstalled. Win32 error={Marshal.GetLastWin32Error()}");
            return;
        }

        FileLogger.Info("RDP keyboard escape hook uninstalled.");
    }

    private static IntPtr OnKeyboardHook(int code, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (code == HcAction && IsKeyDown(lParam))
            {
                var key = KeyInterop.KeyFromVirtualKey(wParam.ToInt32());
                var modifiers = ReadCurrentModifiers();
                var action = RdpKeyboardHookShortcutRouter.Resolve(
                    key,
                    modifiers,
                    RdpDefaultShortcuts.ReleaseFocus,
                    RdpDefaultShortcuts.Fullscreen);

                if (action != RdpKeyboardHookAction.None)
                {
                    var view = FindFocusedRdpView();
                    if (view is null)
                    {
                        return CallNextHookEx(_hookHandle, code, wParam, lParam);
                    }

                    switch (action)
                    {
                        case RdpKeyboardHookAction.ReleaseFocus:
                            _ = view.Dispatcher.BeginInvoke(
                                DispatcherPriority.Input,
                                new Action(view.FocusRdpToolbarFromEscapeHook));
                            break;

                        case RdpKeyboardHookAction.ToggleFullscreen:
                            _ = view.Dispatcher.BeginInvoke(
                                DispatcherPriority.Input,
                                new Action(view.ToggleFullscreen));
                            break;
                    }

                    return new IntPtr(1);
                }
            }
        }
        catch (Exception ex)
        {
            FileLogger.Error("RDP keyboard escape hook callback failed.", ex);
        }

        return CallNextHookEx(_hookHandle, code, wParam, lParam);
    }

    private static bool IsKeyDown(IntPtr lParam)
    {
        return ((lParam.ToInt64() >> 31) & 1) == 0;
    }

    private static ModifierKeys ReadCurrentModifiers()
    {
        var current = ModifierKeys.None;
        if (IsVirtualKeyPressed(VkControl))
        {
            current |= ModifierKeys.Control;
        }

        if (IsVirtualKeyPressed(VkMenu))
        {
            current |= ModifierKeys.Alt;
        }

        if (IsVirtualKeyPressed(VkShift))
        {
            current |= ModifierKeys.Shift;
        }

        if (IsVirtualKeyPressed(VkLwin) || IsVirtualKeyPressed(VkRwin))
        {
            current |= ModifierKeys.Windows;
        }

        return current;
    }

    private static bool IsVirtualKeyPressed(int virtualKey)
    {
        return (GetKeyState(virtualKey) & 0x8000) != 0;
    }

    private static EmbeddedRdpView? FindFocusedRdpView()
    {
        List<IntPtr> focusedHandles = GetFocusedWindowHandles();
        if (focusedHandles.Count == 0)
        {
            return null;
        }

        List<EmbeddedRdpView> views;
        lock (SyncRoot)
        {
            views = RegisteredViews.Values
                .Select(static registration => registration.View)
                .Where(static view => view is not null)
                .Cast<EmbeddedRdpView>()
                .ToList();
        }

        foreach (var view in views)
        {
            var hostHandle = view.GetRdpKeyboardInputHandle();
            if (hostHandle == IntPtr.Zero)
            {
                continue;
            }

            foreach (IntPtr focusedHwnd in focusedHandles)
            {
                if (focusedHwnd == hostHandle || IsChild(hostHandle, focusedHwnd))
                {
                    return view;
                }
            }
        }

        return null;
    }

    private static List<IntPtr> GetFocusedWindowHandles()
    {
        List<IntPtr> handles = new();
        IntPtr threadFocus = GetFocus();
        if (threadFocus != IntPtr.Zero)
        {
            handles.Add(threadFocus);
        }

        IntPtr foregroundWindow = GetForegroundWindow();
        if (foregroundWindow == IntPtr.Zero)
        {
            return handles;
        }

        uint foregroundThreadId = GetWindowThreadProcessId(foregroundWindow, out _);
        if (foregroundThreadId == 0)
        {
            return handles;
        }

        GuiThreadInfo guiThreadInfo = new()
        {
            CbSize = Marshal.SizeOf<GuiThreadInfo>()
        };
        if (!GetGUIThreadInfo(foregroundThreadId, ref guiThreadInfo))
        {
            return handles;
        }

        if (guiThreadInfo.HwndFocus != IntPtr.Zero && !handles.Contains(guiThreadInfo.HwndFocus))
        {
            handles.Add(guiThreadInfo.HwndFocus);
        }

        return handles;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public int CbSize;
        public uint Flags;
        public IntPtr HwndActive;
        public IntPtr HwndFocus;
        public IntPtr HwndCapture;
        public IntPtr HwndMenuOwner;
        public IntPtr HwndMoveSize;
        public IntPtr HwndCaret;
        public Rect RcCaret;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private sealed record RegisteredView(EmbeddedRdpView? View);

    private delegate IntPtr KeyboardHookProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(
        int idHook,
        KeyboardHookProc lpfn,
        IntPtr hmod,
        uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern IntPtr GetFocus();

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsChild(IntPtr hWndParent, IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetGUIThreadInfo(uint idThread, ref GuiThreadInfo lpgui);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
}

internal enum RdpKeyboardHookAction
{
    None,
    ReleaseFocus,
    ToggleFullscreen
}

internal readonly record struct RdpShortcut(ModifierKeys Modifiers, Key Key)
{
    public int VirtualKey => KeyInterop.VirtualKeyFromKey(Key);
}

internal static class RdpKeyboardHookShortcutRouter
{
    public static RdpKeyboardHookAction Resolve(
        Key key,
        ModifierKeys modifiers,
        RdpShortcut releaseFocusShortcut,
        RdpShortcut fullscreenShortcut)
    {
        if (MatchesShortcut(key, modifiers, releaseFocusShortcut))
        {
            return RdpKeyboardHookAction.ReleaseFocus;
        }

        if (MatchesShortcut(key, modifiers, fullscreenShortcut))
        {
            return RdpKeyboardHookAction.ToggleFullscreen;
        }

        return RdpKeyboardHookAction.None;
    }

    private static bool MatchesShortcut(Key key, ModifierKeys modifiers, RdpShortcut shortcut)
    {
        return key == shortcut.Key && modifiers == shortcut.Modifiers;
    }
}

/// <summary>
/// The two shortcuts the hook answers while the embedded RDP surface owns the keyboard. Fixed:
/// no setting ever reached the configurable path that once parsed replacements for them.
/// </summary>
internal static class RdpDefaultShortcuts
{
    /// <summary>Ctrl+Alt+Home: hands keyboard focus back to the RDP toolbar.</summary>
    public static RdpShortcut ReleaseFocus { get; } = new(ModifierKeys.Control | ModifierKeys.Alt, Key.Home);

    /// <summary>F11: toggles fullscreen.</summary>
    public static RdpShortcut Fullscreen { get; } = new(ModifierKeys.None, Key.F11);
}
