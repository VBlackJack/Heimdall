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
using Microsoft.Win32.SafeHandles;

namespace Heimdall.Core.Recipes;

/// <summary>Owns the handle of a Windows job object; closing it ends every process still in the job.</summary>
internal sealed class RecipeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public RecipeJobHandle()
        : base(ownsHandle: true)
    {
    }

    protected override bool ReleaseHandle() => RecipeJobNative.CloseHandle(handle);
}

/// <summary>
/// A Windows job object that kills every process in it when it is closed, so that a program a recipe
/// starts cannot leave a child running after the run is over.
/// </summary>
internal sealed class RecipeJob : IDisposable
{
    /// <summary><c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>.</summary>
    private const uint KillOnJobClose = 0x00002000;

    /// <summary><c>JobObjectExtendedLimitInformation</c>.</summary>
    private const int ExtendedLimitInformationClass = 9;

    /// <summary>The exit code given to a process that is ended with the job.</summary>
    private const uint TerminatedExitCode = 1;

    private readonly RecipeJobHandle _handle;

    private RecipeJob(RecipeJobHandle handle)
    {
        _handle = handle;
    }

    /// <summary>A new job that kills its processes when closed, or null when none can be made.</summary>
    public static RecipeJob? TryCreate()
    {
        if (!OperatingSystem.IsWindows())
            return null;

        RecipeJobHandle handle = RecipeJobNative.CreateJobObjectW(IntPtr.Zero, null);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }

        RecipeJobNative.ExtendedLimits limits = default;
        limits.Basic.LimitFlags = KillOnJobClose;
        bool configured = RecipeJobNative.SetInformationJobObject(handle, ExtendedLimitInformationClass, ref limits, (uint)Marshal.SizeOf<RecipeJobNative.ExtendedLimits>());
        if (!configured)
        {
            handle.Dispose();
            return null;
        }

        return new RecipeJob(handle);
    }

    /// <summary>
    /// Puts a started process in the job. Returns false when Windows refuses, for example because
    /// Heimdall itself runs in a job that does not allow another one, or because the process is
    /// already gone.
    /// </summary>
    /// <param name="process">A process this job's owner just started.</param>
    public bool TryAssign(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        try
        {
            return RecipeJobNative.AssignProcessToJobObject(_handle, process.SafeHandle);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>Ends every process still in the job. Best effort.</summary>
    public void Terminate()
    {
        if (!_handle.IsClosed)
            _ = RecipeJobNative.TerminateJobObject(_handle, TerminatedExitCode);
    }

    /// <inheritdoc />
    public void Dispose() => _handle.Dispose();
}

/// <summary>The Windows calls that make and use a job object.</summary>
internal static class RecipeJobNative
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct BasicLimits
    {
        public long PerProcessUserTimeLimit;

        public long PerJobUserTimeLimit;

        public uint LimitFlags;

        public nuint MinimumWorkingSetSize;

        public nuint MaximumWorkingSetSize;

        public uint ActiveProcessLimit;

        public nuint Affinity;

        public uint PriorityClass;

        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct IoCounters
    {
        public ulong ReadOperationCount;

        public ulong WriteOperationCount;

        public ulong OtherOperationCount;

        public ulong ReadTransferCount;

        public ulong WriteTransferCount;

        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ExtendedLimits
    {
        public BasicLimits Basic;

        public IoCounters Io;

        public nuint ProcessMemoryLimit;

        public nuint JobMemoryLimit;

        public nuint PeakProcessMemoryUsed;

        public nuint PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern RecipeJobHandle CreateJobObjectW(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetInformationJobObject(RecipeJobHandle job, int informationClass, ref ExtendedLimits information, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AssignProcessToJobObject(RecipeJobHandle job, SafeHandle process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool TerminateJobObject(RecipeJobHandle job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr handle);
}
