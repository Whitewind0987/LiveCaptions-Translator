using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using LiveCaptionsTranslator.ipc;

namespace LiveCaptionsTranslator.worker
{
    internal sealed class BoundedLineBuffer
    {
        private const int Capacity = 64;
        private const int MaximumLineLength = 1024;
        private readonly Queue<string> lines = [];
        private readonly object gate = new();
        public void Add(string? line)
        {
            if (line == null) return;
            if (line.Length > MaximumLineLength) line = line[..MaximumLineLength];
            lock (gate) { if (lines.Count == Capacity) lines.Dequeue(); lines.Enqueue(line); }
        }
        public IReadOnlyList<string> Snapshot() { lock (gate) return lines.ToArray(); }
    }

    public sealed class WorkerProcessLauncher : IWorkerProcessLauncher
    {
        public Task<IWorkerProcess> LaunchAsync(WorkerLaunchRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(request.ExecutablePath)) throw new FileNotFoundException("The ASR worker executable does not exist.", request.ExecutablePath);
            var info = new ProcessStartInfo { FileName = Path.GetFullPath(request.ExecutablePath), UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            info.ArgumentList.Add("--control-pipe"); info.ArgumentList.Add(request.ControlPipeName);
            info.ArgumentList.Add("--audio-pipe"); info.ArgumentList.Add(request.AudioPipeName);
            info.ArgumentList.Add("--session"); info.ArgumentList.Add(request.SessionId.ToString("D"));
            info.ArgumentList.Add("--parent-pid"); info.ArgumentList.Add(request.ParentPid.ToString(System.Globalization.CultureInfo.InvariantCulture));
            info.Environment[IpcProtocol.NonceEnvironmentVariable] = Convert.ToHexString(request.AuthenticationNonce);
            var process = new Process { StartInfo = info, EnableRaisingEvents = true };
            try
            {
                if (!process.Start()) throw new InvalidOperationException("Worker process start returned false.");
                return Task.FromResult<IWorkerProcess>(new OwnedWorkerProcess(process));
            }
            catch { process.Dispose(); throw; }
        }
    }

    internal sealed class OwnedWorkerProcess : IWorkerProcess
    {
        private readonly Process process;
        private readonly BoundedLineBuffer stdout = new();
        private readonly BoundedLineBuffer stderr = new();
        private readonly Task stdoutTask;
        private readonly Task stderrTask;
        private readonly Task completion;
        private int disposed;
        public OwnedWorkerProcess(Process process) { this.process = process; stdoutTask = DrainAsync(process.StandardOutput, stdout); stderrTask = DrainAsync(process.StandardError, stderr); completion = CompleteAsync(); }
        public int Id => process.Id;
        public nint NativeHandle => process.Handle;
        public bool HasExited { get { try { return process.HasExited; } catch { return true; } } }
        public int? ExitCode { get { try { return process.HasExited ? process.ExitCode : null; } catch { return null; } } }
        public Task Completion => completion;
        public IReadOnlyList<string> RecentStdout => stdout.Snapshot();
        public IReadOnlyList<string> RecentStderr => stderr.Snapshot();
        private static async Task DrainAsync(StreamReader reader, BoundedLineBuffer target) { while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line) target.Add(line); }
        private async Task CompleteAsync() { await process.WaitForExitAsync().ConfigureAwait(false); await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false); }
        public Task TerminateTreeAsync(CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); if (!process.HasExited) process.Kill(entireProcessTree: true); return completion.WaitAsync(cancellationToken); }
        public async ValueTask DisposeAsync() { if (Interlocked.Exchange(ref disposed, 1) != 0) return; if (!process.HasExited) await TerminateTreeAsync().ConfigureAwait(false); await completion.ConfigureAwait(false); process.Dispose(); }
    }

    public sealed class WindowsWorkerJobFactory : IWorkerJobFactory { public IWorkerJob Create() => new WindowsWorkerJob(); }

    internal sealed class WindowsWorkerJob : IWorkerJob
    {
        private readonly SafeJobHandle handle;
        public WindowsWorkerJob()
        {
            handle = NativeMethods.CreateJobObjectW(nint.Zero, null);
            if (handle.IsInvalid) { FailureReason = $"CreateJobObject failed: {Marshal.GetLastWin32Error()}."; return; }
            var info = new NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION(); info.BasicLimitInformation.LimitFlags = 0x00002000;
            var size = Marshal.SizeOf<NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>(); var pointer = Marshal.AllocHGlobal(size);
            try { Marshal.StructureToPtr(info, pointer, false); if (!NativeMethods.SetInformationJobObject(handle, 9, pointer, (uint)size)) FailureReason = $"SetInformationJobObject failed: {Marshal.GetLastWin32Error()}."; }
            finally { Marshal.FreeHGlobal(pointer); }
        }
        public bool AssignmentSucceeded { get; private set; }
        public string? FailureReason { get; private set; }
        public Task AssignAsync(IWorkerProcess process, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); if (handle.IsInvalid || FailureReason != null) return Task.CompletedTask; AssignmentSucceeded = NativeMethods.AssignProcessToJobObject(handle, process.NativeHandle); if (!AssignmentSucceeded) FailureReason = $"AssignProcessToJobObject failed: {Marshal.GetLastWin32Error()}."; return Task.CompletedTask; }
        public ValueTask DisposeAsync() { handle.Dispose(); return ValueTask.CompletedTask; }
    }

    internal sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid { private SafeJobHandle() : base(true) { } protected override bool ReleaseHandle() => NativeMethods.CloseHandle(handle); }

    internal static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)] internal struct IO_COUNTERS { internal ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
        [StructLayout(LayoutKind.Sequential)] internal struct JOBOBJECT_BASIC_LIMIT_INFORMATION { internal long PerProcessUserTimeLimit, PerJobUserTimeLimit; internal uint LimitFlags; internal nuint MinimumWorkingSetSize, MaximumWorkingSetSize; internal uint ActiveProcessLimit; internal nuint Affinity; internal uint PriorityClass, SchedulingClass; }
        [StructLayout(LayoutKind.Sequential)] internal struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION { internal JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation; internal IO_COUNTERS IoInfo; internal nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed; }
        [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true, CharSet = CharSet.Unicode)] internal static extern SafeJobHandle CreateJobObjectW(nint attributes, string? name);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetInformationJobObject(SafeJobHandle job, int infoClass, nint info, uint length);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool AssignProcessToJobObject(SafeJobHandle job, nint process);
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CloseHandle(nint handle);
    }
}
