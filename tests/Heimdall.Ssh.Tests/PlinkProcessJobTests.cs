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
using Heimdall.Ssh.Plink;
using Microsoft.Win32.SafeHandles;

namespace Heimdall.Ssh.Tests;

/// <summary>
/// The job object that ends every plink when Heimdall dies is created kill-on-close and
/// really receives the processes handed to it.
/// </summary>
/// <remarks>
/// The kill on close itself is not exercised: the job handle is process-wide and static, and
/// closing it would break every later test.
/// </remarks>
public sealed class PlinkProcessJobTests
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    [Fact]
    public void TryAssign_HarmlessChild_PutsItInTheKillOnCloseJob()
    {
        using Process child = StartChild();
        try
        {
            Assert.True(PlinkProcessJob.TryAssign(child));

            SafeFileHandle job = RequireJob();
            Assert.True(IsProcessInJob(child.SafeHandle, job, out bool inJob));
            Assert.True(inJob);
        }
        finally
        {
            KillQuietly(child);
        }
    }

    [Fact]
    public void JobHandle_ExtendedLimits_CarryKillOnJobClose()
    {
        SafeFileHandle job = RequireJob();

        Assert.True(QueryInformationJobObject(
            job,
            JobObjectExtendedLimitInformation,
            out ExtendedLimit limits,
            (uint)Marshal.SizeOf<ExtendedLimit>(),
            IntPtr.Zero));

        Assert.Equal(JobObjectLimitKillOnJobClose, limits.Basic.LimitFlags & JobObjectLimitKillOnJobClose);
    }

    [Fact]
    public void TryAssign_NullProcess_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => PlinkProcessJob.TryAssign(null!));
    }

    private static SafeFileHandle RequireJob()
    {
        SafeFileHandle? job = PlinkProcessJob.JobHandleForTests;
        Assert.NotNull(job);
        Assert.False(job.IsInvalid);
        return job;
    }

    private static Process StartChild()
    {
        string cmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        Process? child = Process.Start(new ProcessStartInfo(cmd, "/c pause")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            CreateNoWindow = true
        });
        return child ?? throw new InvalidOperationException("The child process did not start.");
    }

    private static void KillQuietly(Process child)
    {
        try
        {
            child.Kill(entireProcessTree: true);
            child.WaitForExit();
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(
        SafeProcessHandle process,
        SafeFileHandle job,
        [MarshalAs(UnmanagedType.Bool)] out bool result);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(
        SafeFileHandle job,
        int informationClass,
        out ExtendedLimit information,
        uint informationLength,
        IntPtr returnLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimit
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
    private struct IoCountersInfo
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimit
    {
        public BasicLimit Basic;
        public IoCountersInfo IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}
