using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ShipTime4x4.Hotfolder.Services;

// Low-integrity child with stripped privileges; a kill-on-close job limits it to
// 1 GiB and one process. Original label paths are never passed to the child.
// This is an integrity boundary, not an AppContainer or a network sandbox.
internal sealed class RestrictedPdfProcess : IDisposable
{
    private readonly AnonymousPipeServerStream _input = new(PipeDirection.Out, HandleInheritability.Inheritable);
    private readonly AnonymousPipeServerStream _output = new(PipeDirection.In, HandleInheritability.Inheritable);
    private SafeFileHandle? _job;
    public Stream Input => _input;
    public Stream Output => _output;

    public RestrictedPdfProcess(string executable)
    {
        nint original = 0, token = 0, adminSid = 0, lowSid = 0, attributes = 0, handles = 0, environment = 0;
        ProcessInformation process = default;
        var initialized = false;
        try
        {
            Check(OpenProcessToken(GetCurrentProcess(), 0x8B, out original));
            Check(ConvertStringSidToSid("S-1-5-32-544", out adminSid));
            var disabled = new SidAndAttributes { Sid = adminSid };
            Check(CreateRestrictedToken(original, 1, 1, ref disabled, 0, 0, 0, 0, out token));
            Check(ConvertStringSidToSid("S-1-16-4096", out lowSid));
            var mandatory = new SidAndAttributes { Sid = lowSid, Attributes = 0x20 };
            Check(SetTokenInformation(token, 25, ref mandatory,
                Marshal.SizeOf<SidAndAttributes>() + checked((int)GetLengthSid(lowSid))));
            _job = CreateJobObject(0, null);
            if (_job.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            var limits = new ExtendedLimits
            {
                Basic = new BasicLimits { Flags = 0x2000 | 0x400 | 0x100 | 8, ActiveProcessLimit = 1 },
                ProcessMemoryLimit = 1024UL * 1024 * 1024
            };
            Check(SetInformationJobObject(_job, 9, ref limits, Marshal.SizeOf<ExtendedLimits>()));
            var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), InheritHandle = true };
            using var error = CreateFile("NUL", 0x40000000, 3, ref security, 3, 0, 0);
            if (error.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            var stdin = nint.Parse(_input.GetClientHandleAsString());
            var stdout = nint.Parse(_output.GetClientHandleAsString());
            nuint bytes = 0;
            _ = InitializeProcThreadAttributeList(0, 1, 0, ref bytes);
            attributes = Marshal.AllocHGlobal(checked((int)bytes));
            Check(InitializeProcThreadAttributeList(attributes, 1, 0, ref bytes));
            initialized = true;
            handles = Marshal.AllocHGlobal(3 * nint.Size);
            Marshal.WriteIntPtr(handles, 0, stdin);
            Marshal.WriteIntPtr(handles, nint.Size, stdout);
            Marshal.WriteIntPtr(handles, 2 * nint.Size, error.DangerousGetHandle());
            Check(UpdateProcThreadAttribute(attributes, 0, 0x20002, handles, (nuint)(3 * nint.Size), 0, 0));
            var startup = new StartupInfoEx
            {
                Startup = new StartupInfo
                {
                    Size = Marshal.SizeOf<StartupInfoEx>(), Flags = 0x100,
                    StandardInput = stdin, StandardOutput = stdout, StandardError = error.DangerousGetHandle()
                },
                Attributes = attributes
            };
            var vars = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["SystemRoot"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                ["DOTNET_EnableDiagnostics"] = "0",
                ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
                ["DOTNET_MULTILEVEL_LOOKUP"] = "0",
                ["PATH"] = Environment.GetFolderPath(Environment.SpecialFolder.System)
            };
            var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
            if (!string.IsNullOrWhiteSpace(dotnetRoot)) vars["DOTNET_ROOT"] = dotnetRoot;
            environment = Marshal.StringToHGlobalUni(string.Join('\0', vars.Select(x => x.Key + "=" + x.Value)) + "\0\0");
            var command = new StringBuilder("\"" + executable + "\" --pdf-worker");
            Check(CreateProcessAsUser(token, executable, command, 0, 0, true,
                4 | 0x08000000 | 0x00080000 | 0x400, environment,
                Path.GetDirectoryName(executable)!, ref startup, out process));
            Check(AssignProcessToJobObject(_job, process.Process));
            if (ResumeThread(process.Thread) == uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error());
            _input.DisposeLocalCopyOfClientHandle();
            _output.DisposeLocalCopyOfClientHandle();
        }
        catch
        {
            if (process.Process != 0) _ = TerminateProcess(process.Process, 1);
            Dispose();
            throw;
        }
        finally
        {
            if (process.Thread != 0) CloseHandle(process.Thread);
            if (process.Process != 0) CloseHandle(process.Process);
            if (original != 0) CloseHandle(original);
            if (token != 0) CloseHandle(token);
            if (adminSid != 0) LocalFree(adminSid);
            if (lowSid != 0) LocalFree(lowSid);
            if (attributes != 0)
            {
                if (initialized) DeleteProcThreadAttributeList(attributes);
                Marshal.FreeHGlobal(attributes);
            }
            if (handles != 0) Marshal.FreeHGlobal(handles);
            if (environment != 0) Marshal.FreeHGlobal(environment);
        }
    }

    internal static bool IsLowIntegrity()
    {
        Check(OpenProcessToken(GetCurrentProcess(), 8, out var token));
        try
        {
            _ = GetTokenInformation(token, 25, 0, 0, out var size);
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                Check(GetTokenInformation(token, 25, buffer, size, out _));
                var label = Marshal.PtrToStructure<SidAndAttributes>(buffer);
                Check(ConvertStringSidToSid("S-1-16-4096", out var low));
                try { return EqualSid(label.Sid, low); }
                finally { LocalFree(low); }
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        finally { CloseHandle(token); }
    }

    public void Dispose()
    {
        _job?.Dispose();
        _input.Dispose();
        _output.Dispose();
    }

    private static void Check(bool success)
    {
        if (!success) throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to establish the restricted PDF worker.");
    }

    [StructLayout(LayoutKind.Sequential)] private struct SidAndAttributes { public nint Sid; public uint Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes
    { public int Length; public nint Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle; }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
    {
        public long ProcessTime, JobTime; public uint Flags;
        public nuint MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcessLimit; public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters
    { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits
    { public BasicLimits Basic; public IoCounters Io; public ulong ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemory, PeakJobMemory; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo
    {
        public int Size; public nint Reserved, Desktop, Title;
        public uint X, Y, Width, Height, XChars, YChars, Fill, Flags;
        public ushort ShowWindow, ReservedCount;
        public nint ReservedBytes, StandardInput, StandardOutput, StandardError;
    }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public StartupInfo Startup; public nint Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation
    { public nint Process, Thread; public int ProcessId, ThreadId; }

    [DllImport("kernel32.dll")] private static extern nint GetCurrentProcess();
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(nint process, uint access, out nint token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool CreateRestrictedToken(nint token, uint flags, uint disableCount, ref SidAndAttributes disable, uint privilegeCount, nint privileges, uint restrictCount, nint restrictions, out nint result);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool ConvertStringSidToSid(string sid, out nint result);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool SetTokenInformation(nint token, int type, ref SidAndAttributes info, int length);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(nint token, int type, nint info, int length, out int needed);
    [DllImport("advapi32.dll")] private static extern uint GetLengthSid(nint sid);
    [DllImport("advapi32.dll")] private static extern bool EqualSid(nint first, nint second);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateJobObject(nint attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(SafeFileHandle job, int type, ref ExtendedLimits info, int length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(SafeFileHandle job, nint process);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string path, uint access, uint share, ref SecurityAttributes attributes, uint creation, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(nint list, int count, int flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(nint list, uint flags, nuint attribute, nint value, nuint size, nint previous, nint returned);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(nint list);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcessAsUser(nint token, string application, StringBuilder command, nint processAttributes, nint threadAttributes, bool inherit, uint flags, nint environment, string directory, ref StartupInfoEx startup, out ProcessInformation process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(nint thread);
    [DllImport("kernel32.dll")] private static extern bool TerminateProcess(nint process, uint code);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint memory);
}
