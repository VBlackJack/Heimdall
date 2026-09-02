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

using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Heimdall.Core.Logging;
using Microsoft.Win32.SafeHandles;

namespace Heimdall.Core.Security;

/// <summary>
/// Privilege levels for process launching.
/// </summary>
public enum PrivilegeLevel
{
    /// <summary>Standard UAC elevation (run as administrator).</summary>
    CurrentUserElevated,

    /// <summary>NT AUTHORITY\SYSTEM context (via winlogon.exe token).</summary>
    System,

    /// <summary>NT SERVICE\TrustedInstaller context (via TrustedInstaller service token).</summary>
    TrustedInstaller
}

/// <summary>
/// Result of a privilege-launched process.
/// </summary>
public sealed record PrivilegeLaunchResult(bool Success, int ProcessId, string? ErrorMessage = null)
{
    public static PrivilegeLaunchResult Failed(string message) => new(false, 0, message);
    public static PrivilegeLaunchResult Ok(int pid) => new(true, pid);
}

/// <summary>
/// Launches processes under elevated security contexts (SYSTEM, TrustedInstaller)
/// using Win32 token duplication and <c>CreateProcessWithTokenW</c>.
/// Requires the calling process to be elevated (administrator).
/// </summary>
[SupportedOSPlatform("windows")]
public static class PrivilegeLauncher
{
    // ── Win32 constants ───────────────────────────────────────────────

    private const uint PROCESS_QUERY_INFORMATION = 0x0400;
    private const uint TOKEN_DUPLICATE = 0x0002;
    private const uint TOKEN_QUERY = 0x0008;
    private const uint TOKEN_ASSIGN_PRIMARY = 0x0001;
    private const uint TOKEN_ADJUST_DEFAULT = 0x0080;
    private const uint TOKEN_ADJUST_SESSION_ID = 0x0100;
    private const uint MAXIMUM_ALLOWED = 0x02000000;

    private const int SECURITY_IMPERSONATION = 2; // SecurityImpersonation
    private const int TOKEN_PRIMARY = 1;          // TokenPrimary

    private const uint SE_PRIVILEGE_ENABLED = 0x00000002;
    private const uint LOGON_WITH_PROFILE = 0x00000001;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;

    private const uint SC_MANAGER_CONNECT = 0x0001;
    private const uint SERVICE_START = 0x0010;
    private const uint SERVICE_QUERY_STATUS = 0x0004;
    private const uint SERVICE_RUNNING = 0x00000004;

    private const int MAX_SERVICE_WAIT_MS = 10_000;
    private const int SERVICE_POLL_INTERVAL_MS = 250;
    private const string PrivLaunchArg = "--privlaunch";
    private const string PayloadArg = "--payload";

    private sealed class PrivilegeLaunchPayloadDto
    {
        public string? Exe { get; init; }
        public string[]? Args { get; init; }
    }

    internal sealed record PrivilegeLaunchPayload(string Exe, string[] Args);

    // ── Win32 structures ──────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID_AND_ATTRIBUTES
    {
        public LUID Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public LUID_AND_ATTRIBUTES Privileges;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        public bool bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize;
        public int dwXCountChars, dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_STATUS
    {
        public uint dwServiceType;
        public uint dwCurrentState;
        public uint dwControlsAccepted;
        public uint dwWin32ExitCode;
        public uint dwServiceSpecificExitCode;
        public uint dwCheckPoint;
        public uint dwWaitHint;
    }

    // ── Win32 imports ─────────────────────────────────────────────────

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle processHandle, uint desiredAccess, out SafeAccessTokenHandle tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateTokenEx(
        SafeAccessTokenHandle hExistingToken,
        uint dwDesiredAccess,
        IntPtr lpTokenAttributes,
        int impersonationLevel,
        int tokenType,
        out SafeAccessTokenHandle phNewToken);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessWithTokenW(
        SafeAccessTokenHandle hToken,
        uint dwLogonFlags,
        string? lpApplicationName,
        string? lpCommandLine,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValue(string? lpSystemName, string lpName, out LUID lpLuid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(
        SafeAccessTokenHandle tokenHandle,
        [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges,
        ref TOKEN_PRIVILEGES newState,
        int bufferLength,
        IntPtr previousState,
        IntPtr returnLength);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManager(string? lpMachineName, string? lpDatabaseName, uint dwDesiredAccess);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenService(IntPtr hSCManager, string lpServiceName, uint dwDesiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartService(IntPtr hService, int dwNumServiceArgs, IntPtr lpServiceArgVectors);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatus(IntPtr hService, out SERVICE_STATUS lpServiceStatus);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr hSCObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CommandLineToArgvW(string lpCmdLine, out int pNumArgs);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);

    // ── Public API ────────────────────────────────────────────────────

    /// <summary>
    /// Launches a process under the specified privilege level.
    /// When the current process is not elevated and SYSTEM/TrustedInstaller
    /// is requested, automatically re-launches itself elevated via UAC
    /// to perform the token work.
    /// </summary>
    public static PrivilegeLaunchResult Launch(string executablePath, string? arguments, PrivilegeLevel level)
    {
        try
        {
            return Launch(executablePath, ParseArguments(arguments), level);
        }
        catch (Exception ex)
        {
            return PrivilegeLaunchResult.Failed(ex.Message);
        }
    }

    /// <summary>
    /// Launches a process under the specified privilege level with structured
    /// argument tokens that preserve the target argv across elevation hops.
    /// </summary>
    public static PrivilegeLaunchResult Launch(
        string executablePath,
        IReadOnlyList<string>? arguments,
        PrivilegeLevel level)
    {
        PrivilegeLaunchPayload payload = new(
            executablePath,
            arguments?.ToArray() ?? []);

        if (!TryValidateLaunchPayload(payload, out string validationError, level))
            return PrivilegeLaunchResult.Failed(validationError);

        // UAC elevation is handled by ShellExecute directly
        if (level == PrivilegeLevel.CurrentUserElevated)
            return LaunchElevated(payload.Exe, payload.Args);

        // For SYSTEM/TI: if already elevated, do in-process; otherwise self-elevate
        if (IsCurrentProcessElevated())
        {
            return level switch
            {
                PrivilegeLevel.System => LaunchWithTokenProbe(payload.Exe, payload.Args, SystemProcessCandidates),
                PrivilegeLevel.TrustedInstaller => LaunchAsTrustedInstaller(payload.Exe, payload.Args),
                _ => PrivilegeLaunchResult.Failed($"Unknown privilege level: {level}")
            };
        }

        return LaunchViaSelfElevation(payload.Exe, payload.Args, level);
    }

    /// <summary>
    /// Checks whether the current process is running elevated (administrator).
    /// </summary>
    public static bool IsCurrentProcessElevated()
    {
        using System.Security.Principal.WindowsIdentity identity =
            System.Security.Principal.WindowsIdentity.GetCurrent();
        System.Security.Principal.WindowsPrincipal principal =
            new(identity);
        return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// Handles the <c>--privlaunch</c> command-line argument in the elevated child process.
    /// Call from <c>App.OnStartup</c> before any UI initialization.
    /// Returns null if args don't match; otherwise returns an exit code (0=success).
    /// </summary>
    public static int? HandlePrivilegeLaunchArgs(string[] args)
    {
        if (args.Length == 0 || !string.Equals(args[0], PrivLaunchArg, StringComparison.OrdinalIgnoreCase))
            return null;

        if (args.Length != 4 || !string.Equals(args[2], PayloadArg, StringComparison.Ordinal))
        {
            FileLogger.Warn("[PrivilegeLauncher] Rejected malformed self-elevation command line.");
            return 1;
        }

        string levelStr = args[1];
        if (!Enum.TryParse<PrivilegeLevel>(levelStr, ignoreCase: true, out PrivilegeLevel level))
        {
            FileLogger.Warn($"[PrivilegeLauncher] Rejected self-elevation request with invalid level '{levelStr}'.");
            return 1;
        }

        if (level == PrivilegeLevel.CurrentUserElevated)
        {
            FileLogger.Warn("[PrivilegeLauncher] Rejected self-elevation request for unsupported CurrentUserElevated level.");
            return 1;
        }

        PrivilegeLaunchPayload payload;
        try
        {
            payload = DecodeLaunchPayload(args[3]);
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"[PrivilegeLauncher] Rejected self-elevation payload decode: {ex.Message}");
            return 1;
        }

        if (!TryValidateLaunchPayload(payload, out string validationError, level))
        {
            FileLogger.Warn($"[PrivilegeLauncher] Rejected self-elevation payload validation: {validationError}");
            return 1;
        }

        if (!IsCurrentProcessElevated())
        {
            FileLogger.Warn(
                "[PrivilegeLauncher] Rejected self-elevation token work: process is not elevated.");
            return 1;
        }

        // We are now elevated - do the actual token work
        PrivilegeLaunchResult result;
        try
        {
            result = level switch
            {
                PrivilegeLevel.System => LaunchWithTokenProbe(payload.Exe, payload.Args, SystemProcessCandidates),
                PrivilegeLevel.TrustedInstaller => LaunchAsTrustedInstaller(payload.Exe, payload.Args),
                _ => PrivilegeLaunchResult.Failed($"Unsupported level for self-elevation: {level}")
            };
        }
        catch (Exception ex)
        {
            result = PrivilegeLaunchResult.Failed(ex.Message);
        }

        return result.Success ? 0 : 2;
    }

    // ── Elevated (UAC) ────────────────────────────────────────────────

    private static PrivilegeLaunchResult LaunchElevated(string exe, IReadOnlyList<string> args)
    {
        try
        {
            ProcessStartInfo psi = new()
            {
                FileName = exe,
                Arguments = BuildArgumentsString(args),
                UseShellExecute = true,
                Verb = "runas"
            };

            Process? process = Process.Start(psi);
            return process is not null
                ? PrivilegeLaunchResult.Ok(process.Id)
                : PrivilegeLaunchResult.Failed("Process.Start returned null.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED
        {
            return PrivilegeLaunchResult.Failed("UAC elevation was cancelled by the user.");
        }
        catch (Exception ex)
        {
            return PrivilegeLaunchResult.Failed(ex.Message);
        }
    }

    // ── Self-elevation (re-launch as admin via UAC) ─────────────────

    private static PrivilegeLaunchResult LaunchViaSelfElevation(
        string exe, IReadOnlyList<string> args, PrivilegeLevel level)
    {
        try
        {
            // Resolve the native host exe for self-elevation.
            // Environment.ProcessPath returns dotnet.exe when using "dotnet run",
            // so we derive the .exe path from the entry assembly's DLL location.
            (string? selfExe, string selfPrefix) = ResolveSelfExecutable();
            if (string.IsNullOrEmpty(selfExe))
                return PrivilegeLaunchResult.Failed("Cannot determine current executable path.");

            string payload = EncodeLaunchPayload(exe, args);

            // Verb="runas" requires UseShellExecute=true, so the UAC hop still
            // uses a raw Arguments string. The payload is base64-encoded JSON,
            // which keeps token boundaries stable across this relaunch.
            string childArguments = BuildSelfElevationArguments(level, payload, selfPrefix);

            ProcessStartInfo psi = new()
            {
                FileName = selfExe,
                Arguments = childArguments,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };

            Process? process = Process.Start(psi);
            if (process is null)
                return PrivilegeLaunchResult.Failed("Failed to start elevated process.");

            // Wait for the elevated child to finish (it just launches the target and exits)
            process.WaitForExit(15_000);
            int exitCode = process.HasExited ? process.ExitCode : -1;
            process.Dispose();

            return exitCode == 0
                ? PrivilegeLaunchResult.Ok(0)
                : PrivilegeLaunchResult.Failed("Elevated launch failed (child exit code: " + exitCode + ").");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED
        {
            return PrivilegeLaunchResult.Failed("UAC elevation was cancelled by the user.");
        }
        catch (Exception ex)
        {
            return PrivilegeLaunchResult.Failed(ex.Message);
        }
    }

    // ── Self-executable resolution ──────────────────────────────────

    /// <summary>
    /// Resolves the native host executable for self-elevation.
    /// Returns (exePath, argPrefix) where argPrefix is empty for direct exe
    /// or contains "exec &lt;dll&gt; " for dotnet-hosted scenarios.
    /// </summary>
    private static (string? ExePath, string ArgPrefix) ResolveSelfExecutable()
    {
        string? processPath = Environment.ProcessPath;

        // Direct exe launch (Release, Run.bat, etc.) - ProcessPath IS the app exe
        if (processPath is not null
            && !processPath.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            return (processPath, string.Empty);
        }

        // Running via "dotnet run" or "dotnet exec" - derive the .exe from the loaded DLL
        string? dllPath = Assembly.GetEntryAssembly()?.Location;
        if (!string.IsNullOrEmpty(dllPath))
        {
            string exePath = System.IO.Path.ChangeExtension(dllPath, ".exe") ?? string.Empty;
            if (System.IO.File.Exists(exePath))
                return (exePath, string.Empty);

            // No .exe found (single-file or framework-dependent without host) - use dotnet exec
            if (processPath is not null)
                return (processPath, $"exec \"{dllPath}\" ");
        }

        return (null, string.Empty);
    }

    // ── SYSTEM (probe multiple candidates) ──────────────────────────

    /// <summary>
    /// SYSTEM process candidates ordered by likelihood of being non-PPL.
    /// PPL (Protected Process Light) blocks OpenProcessToken even with SeDebugPrivilege.
    /// </summary>
    private static readonly string[] SystemProcessCandidates =
        ["spoolsv", "svchost", "services", "lsass", "winlogon"];

    private static SafeAccessTokenHandle? TryAcquireTokenFromProcesses(string[] processNames)
    {
        foreach (string name in processNames)
        {
            Process[] procs = Process.GetProcessesByName(name);
            try
            {
                foreach (Process proc in procs)
                {
                    SafeAccessTokenHandle? token = TryOpenAndDuplicateToken(proc.Id);
                    if (token is not null) return token;
                }
            }
            finally
            {
                foreach (Process p in procs) p.Dispose();
            }
        }

        return null;
    }

    private static SafeAccessTokenHandle? TryOpenAndDuplicateToken(int pid)
    {
        using SafeProcessHandle processHandle = OpenProcess(PROCESS_QUERY_INFORMATION, false, pid);
        if (processHandle.IsInvalid) return null;

        if (!OpenProcessToken(processHandle, TOKEN_DUPLICATE, out SafeAccessTokenHandle tokenHandle))
            return null;

        using (tokenHandle)
        {
            if (DuplicateTokenEx(
                    tokenHandle, MAXIMUM_ALLOWED, IntPtr.Zero,
                    SECURITY_IMPERSONATION, TOKEN_PRIMARY,
                    out SafeAccessTokenHandle duplicated))
                return duplicated;

            return null;
        }
    }

    // ── TrustedInstaller ──────────────────────────────────────────────

    private static PrivilegeLaunchResult LaunchAsTrustedInstaller(string exe, IReadOnlyList<string> args)
    {
        // Step 1: Ensure TrustedInstaller service is running
        PrivilegeLaunchResult serviceResult = EnsureTrustedInstallerServiceRunning();
        if (!serviceResult.Success)
            return serviceResult;

        // Step 2: Launch with TrustedInstaller's token
        return LaunchWithTokenProbe(exe, args, ["TrustedInstaller"]);
    }

    private static PrivilegeLaunchResult EnsureTrustedInstallerServiceRunning()
    {
        IntPtr scManager = OpenSCManager(null, null, SC_MANAGER_CONNECT);
        if (scManager == IntPtr.Zero)
            return PrivilegeLaunchResult.Failed($"OpenSCManager failed: {Marshal.GetLastPInvokeError()}");

        try
        {
            IntPtr service = OpenService(scManager, "TrustedInstaller", SERVICE_START | SERVICE_QUERY_STATUS);
            if (service == IntPtr.Zero)
                return PrivilegeLaunchResult.Failed($"OpenService(TrustedInstaller) failed: {Marshal.GetLastPInvokeError()}");

            try
            {
                // Check current status
                if (!QueryServiceStatus(service, out SERVICE_STATUS status))
                    return PrivilegeLaunchResult.Failed($"QueryServiceStatus failed: {Marshal.GetLastPInvokeError()}");

                if (status.dwCurrentState == SERVICE_RUNNING)
                    return PrivilegeLaunchResult.Ok(0); // Already running

                // Start the service
                if (!StartService(service, 0, IntPtr.Zero))
                {
                    int err = Marshal.GetLastPInvokeError();
                    if (err != 1056) // ERROR_SERVICE_ALREADY_RUNNING
                        return PrivilegeLaunchResult.Failed($"StartService(TrustedInstaller) failed: {err}");
                }

                // Wait for it to reach RUNNING state
                Stopwatch sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < MAX_SERVICE_WAIT_MS)
                {
                    if (QueryServiceStatus(service, out status) && status.dwCurrentState == SERVICE_RUNNING)
                        return PrivilegeLaunchResult.Ok(0);

                    Thread.Sleep(SERVICE_POLL_INTERVAL_MS);
                }

                return PrivilegeLaunchResult.Failed("TrustedInstaller service did not start within the timeout.");
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(scManager);
        }
    }

    // ── Token probe + process creation ──────────────────────────────

    /// <summary>
    /// Enables SeDebugPrivilege, probes candidate processes for an accessible token,
    /// duplicates it, and creates the target process under that token.
    /// </summary>
    private static PrivilegeLaunchResult LaunchWithTokenProbe(
        string exe, IReadOnlyList<string> args, string[] processCandidates)
    {
        try
        {
            EnableDebugPrivilege();

            using SafeAccessTokenHandle? token = TryAcquireTokenFromProcesses(processCandidates);
            if (token is null || token.IsInvalid)
                return PrivilegeLaunchResult.Failed(
                    $"No accessible token found. Probed: {string.Join(", ", processCandidates)}. " +
                    "Ensure Heimdall is running as administrator.");

            string commandLine = BuildCommandLine(exe, args);

            STARTUPINFO si = new() { cb = Marshal.SizeOf<STARTUPINFO>() };

            if (!CreateProcessWithTokenW(
                    token, LOGON_WITH_PROFILE, null, commandLine,
                    CREATE_UNICODE_ENVIRONMENT, IntPtr.Zero, null,
                    ref si, out PROCESS_INFORMATION pi))
                return PrivilegeLaunchResult.Failed(
                    $"CreateProcessWithTokenW failed: {Marshal.GetLastPInvokeError()}");

            CloseHandle(pi.hProcess);
            CloseHandle(pi.hThread);

            return PrivilegeLaunchResult.Ok(pi.dwProcessId);
        }
        catch (Exception ex)
        {
            return PrivilegeLaunchResult.Failed(ex.Message);
        }
    }

    internal static string EncodeLaunchPayload(string executablePath, IReadOnlyList<string>? arguments)
    {
        PrivilegeLaunchPayloadDto payload = new()
        {
            Exe = executablePath,
            Args = arguments?.ToArray() ?? []
        };

        string json = JsonSerializer.Serialize(payload);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    internal static PrivilegeLaunchPayload DecodeLaunchPayload(string encodedPayload)
    {
        if (string.IsNullOrWhiteSpace(encodedPayload))
            throw new FormatException("Privilege launch payload is missing.");

        byte[] payloadBytes;
        try
        {
            payloadBytes = Convert.FromBase64String(encodedPayload);
        }
        catch (FormatException ex)
        {
            throw new FormatException("Privilege launch payload is not valid base64.", ex);
        }

        PrivilegeLaunchPayloadDto? payload;
        try
        {
            payload = JsonSerializer.Deserialize<PrivilegeLaunchPayloadDto>(payloadBytes);
        }
        catch (JsonException ex)
        {
            throw new FormatException("Privilege launch payload is not valid JSON.", ex);
        }

        if (payload?.Args is null)
            throw new FormatException("Privilege launch payload is missing its args array.");

        if (payload.Args.Any(arg => arg is null))
            throw new FormatException("Privilege launch payload contains a null argument.");

        return new PrivilegeLaunchPayload(payload.Exe ?? string.Empty, payload.Args);
    }

    internal static bool TryValidateLaunchPayload(
        PrivilegeLaunchPayload? payload,
        out string errorMessage,
        PrivilegeLevel level = PrivilegeLevel.CurrentUserElevated)
    {
        if (payload is null)
        {
            errorMessage = "Privilege launch payload is missing.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(payload.Exe))
        {
            errorMessage = "Executable path is required.";
            return false;
        }

        if (payload.Exe.IndexOf('\0') >= 0)
        {
            errorMessage = "Executable path contains an embedded null byte.";
            return false;
        }

        bool requiresQualifiedPath =
            level is PrivilegeLevel.System or PrivilegeLevel.TrustedInstaller;
        if (requiresQualifiedPath && !Path.IsPathFullyQualified(payload.Exe))
        {
            errorMessage =
                "A fully-qualified executable path is required for SYSTEM or TrustedInstaller launches.";
            return false;
        }

        if (payload.Args.Any(arg => arg is null))
        {
            errorMessage = "Privilege launch payload contains a null argument.";
            return false;
        }

        if (Path.IsPathFullyQualified(payload.Exe) && !File.Exists(payload.Exe))
        {
            errorMessage = $"File not found: {payload.Exe}";
            return false;
        }

        errorMessage = string.Empty;
        return true;
    }

    internal static string[] ParseArguments(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
            return [];

        string commandLine = "heimdall.exe " + arguments;
        IntPtr argv = CommandLineToArgvW(commandLine, out int argc);
        if (argv == IntPtr.Zero)
            throw new Win32Exception(
                Marshal.GetLastPInvokeError(),
                "Failed to parse command-line arguments.");

        try
        {
            if (argc <= 1)
                return [];

            string[] parsedArgs = new string[argc - 1];
            for (int i = 1; i < argc; i++)
            {
                IntPtr argPtr = Marshal.ReadIntPtr(argv, i * IntPtr.Size);
                parsedArgs[i - 1] = Marshal.PtrToStringUni(argPtr) ?? string.Empty;
            }

            return parsedArgs;
        }
        finally
        {
            _ = LocalFree(argv);
        }
    }

    internal static string BuildSelfElevationArguments(
        PrivilegeLevel level,
        string encodedPayload,
        string selfPrefix)
    {
        ArgumentNullException.ThrowIfNull(encodedPayload);
        ArgumentNullException.ThrowIfNull(selfPrefix);

        return string.Concat(
            selfPrefix,
            BuildArgumentsString([PrivLaunchArg, level.ToString(), PayloadArg, encodedPayload]));
    }

    internal static string BuildCommandLine(
        string executablePath,
        IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(executablePath);
        ArgumentNullException.ThrowIfNull(arguments);

        string[] tokens = new string[arguments.Count + 1];
        tokens[0] = QuoteWindowsArgument(executablePath);
        for (int i = 0; i < arguments.Count; i++)
        {
            tokens[i + 1] = QuoteWindowsArgument(arguments[i]);
        }

        return string.Join(" ", tokens);
    }

    private static string BuildArgumentsString(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        if (arguments.Count == 0)
            return string.Empty;

        return string.Join(" ", arguments.Select(QuoteWindowsArgument));
    }

    private static string QuoteWindowsArgument(string argument)
    {
        ArgumentNullException.ThrowIfNull(argument);

        if (argument.Length == 0)
            return "\"\"";

        bool needsQuotes = argument.Any(ch => char.IsWhiteSpace(ch) || ch == '"');
        if (!needsQuotes)
            return argument;

        StringBuilder builder = new(argument.Length + 2);
        builder.Append('"');

        int backslashCount = 0;
        foreach (char ch in argument)
        {
            if (ch == '\\')
            {
                backslashCount++;
                continue;
            }

            if (ch == '"')
            {
                builder.Append('\\', (backslashCount * 2) + 1);
                builder.Append('"');
                backslashCount = 0;
                continue;
            }

            if (backslashCount > 0)
            {
                builder.Append('\\', backslashCount);
                backslashCount = 0;
            }

            builder.Append(ch);
        }

        if (backslashCount > 0)
            builder.Append('\\', backslashCount * 2);

        builder.Append('"');
        return builder.ToString();
    }

    private static void EnableDebugPrivilege()
    {
        const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
        using Process process = Process.GetCurrentProcess();
        using SafeProcessHandle processHandle = new(process.Handle, ownsHandle: false);

        if (!OpenProcessToken(processHandle, TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out SafeAccessTokenHandle tokenHandle))
            throw new Win32Exception(Marshal.GetLastPInvokeError(),
                "Failed to open current process token.");

        using (tokenHandle)
        {
            if (!LookupPrivilegeValue(null, "SeDebugPrivilege", out LUID luid))
                throw new Win32Exception(Marshal.GetLastPInvokeError(),
                    "SeDebugPrivilege lookup failed.");

            TOKEN_PRIVILEGES tp = new()
            {
                PrivilegeCount = 1,
                Privileges = new LUID_AND_ATTRIBUTES
                {
                    Luid = luid,
                    Attributes = SE_PRIVILEGE_ENABLED
                }
            };

            bool adjusted = AdjustTokenPrivileges(tokenHandle, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
            int lastError = Marshal.GetLastPInvokeError();
            if (!adjusted)
                throw new Win32Exception(
                    lastError, "AdjustTokenPrivileges failed while enabling SeDebugPrivilege.");
            if (lastError == 1300) // ERROR_NOT_ALL_ASSIGNED
                throw new InvalidOperationException(
                    "SeDebugPrivilege is not available. Run as administrator.");
        }
    }
}
