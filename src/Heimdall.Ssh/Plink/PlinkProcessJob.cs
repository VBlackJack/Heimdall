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
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Heimdall.Ssh.Plink;

/// <summary>
/// Ties the plink.exe processes that carry tunnels to Heimdall's own lifetime.
/// </summary>
/// <remarks>
/// A normal exit stops every plink through the tunnel manager. A crash, a kill from the task
/// manager or a power-user "End task" did not: plink kept the authenticated forward open on
/// loopback with nobody left to close it. Each plink is placed in one Windows job object marked
/// kill-on-close. The job handle is held until the process ends, at which point Windows closes it
/// and ends every plink still in the job. Off Windows, and wherever the job cannot be set up, the
/// process simply runs as before and the reason is logged.
/// </remarks>
internal static class PlinkProcessJob
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    private static readonly Lazy<SafeFileHandle?> Job = new(CreateKillOnCloseJob);

    /// <summary>
    /// Places <paramref name="process"/> in the kill-on-close job; false when it could not be.
    /// </summary>
    public static bool TryAssign(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            // Inside the try: plink is already running when this is called, and nothing that
            // goes wrong here may escape and leave it without an owner.
            SafeFileHandle? job = Job.Value;
            if (job is null || job.IsInvalid)
            {
                return false;
            }

            if (AssignProcessToJobObject(job, process.SafeHandle))
            {
                return true;
            }

            Core.Logging.FileLogger.Warn(
                $"[PlinkProcessJob] Could not tie plink pid={process.Id} to Heimdall's lifetime: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"[PlinkProcessJob] Could not tie plink to Heimdall's lifetime: {ex.Message}");
        }

        return false;
    }

    private static SafeFileHandle? CreateKillOnCloseJob()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        SafeFileHandle job = CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid)
        {
            Core.Logging.FileLogger.Warn(
                $"[PlinkProcessJob] CreateJobObject failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
            return null;
        }

        JobObjectExtendedLimit limits = new()
        {
            BasicLimitInformation = new JobObjectBasicLimit { LimitFlags = JobObjectLimitKillOnJobClose }
        };
        if (!SetInformationJobObject(
                job,
                JobObjectExtendedLimitInformation,
                ref limits,
                (uint)Marshal.SizeOf<JobObjectExtendedLimit>()))
        {
            Core.Logging.FileLogger.Warn(
                $"[PlinkProcessJob] SetInformationJobObject failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
            job.Dispose();
            return null;
        }

        return job;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        SafeFileHandle job,
        int jobObjectInformationClass,
        ref JobObjectExtendedLimit jobObjectInformation,
        uint jobObjectInformationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimit
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimit
    {
        public JobObjectBasicLimit BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}
