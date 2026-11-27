using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MediaTaxi.Media;

/// <summary>Launches with an explicit handle allowlist; never changes the console's stdin.</summary>
public sealed class WindowsInputProcess : IDisposable
{
    public Process Process { get; }
    public StreamReader Output { get; }
    public StreamReader Error { get; }
    private WindowsInputProcess(Process process, SafeFileHandle output, SafeFileHandle error)
    { Process = process; Output = new(new FileStream(output, FileAccess.Read)); Error = new(new FileStream(error, FileAccess.Read)); }

    public static WindowsInputProcess Start(string executable, IEnumerable<string> arguments, string source)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        // handle: requires a synchronous regular-file handle (not FILE_FLAG_OVERLAPPED).
        using var input = File.OpenHandle(source, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, FileOptions.SequentialScan);
        var sa = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Inherit = true };
        Check(CreatePipe(out var outRead, out var outWrite, ref sa, 0));
        using var outWriter = outWrite;
        using var outReader = outRead;
        Check(SetHandleInformation(outRead, 1, 0));
        Check(CreatePipe(out var errRead, out var errWrite, ref sa, 0));
        using var errWriter = errWrite;
        using var errReader = errRead;
        Check(SetHandleInformation(errRead, 1, 0));
        Check(DuplicateHandle(GetCurrentProcess(), input, GetCurrentProcess(), out var inheritedInput, 0, true, 2));
        using var childInput = inheritedInput;
        nuint bytes = 0; InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref bytes);
        var attributes = Marshal.AllocHGlobal(checked((int)bytes));
        var handles = Marshal.AllocHGlobal(3 * IntPtr.Size);
        var initialized = false;
        try
        {
            Check(InitializeProcThreadAttributeList(attributes, 1, 0, ref bytes)); initialized = true;
            Marshal.Copy(new[] { childInput.DangerousGetHandle(), outWrite.DangerousGetHandle(), errWrite.DangerousGetHandle() }, 0, handles, 3);
            Check(UpdateProcThreadAttribute(attributes, 0, (IntPtr)0x20002, handles, (nuint)(3 * IntPtr.Size), IntPtr.Zero, IntPtr.Zero));
            var info = new StartupInfoEx { Attributes = attributes, Startup = new() { Size = Marshal.SizeOf<StartupInfoEx>(), Flags = 0x100,
                Input = childInput.DangerousGetHandle(), Output = outWrite.DangerousGetHandle(), Error = errWrite.DangerousGetHandle() } };
            var command = new StringBuilder(string.Join(' ', new[] { executable }.Concat(arguments).Select(Quote)));
            Check(CreateProcessW(executable, command, IntPtr.Zero, IntPtr.Zero, true, 0x08080000, IntPtr.Zero, null, ref info, out var pi));
            using var processHandle = new SafeFileHandle(pi.Process, true);
            using var threadHandle = new SafeFileHandle(pi.Thread, true);
            // Duplicate the parent read handles into independently owned wrappers.
            Check(DuplicateHandle(GetCurrentProcess(), outRead, GetCurrentProcess(), out var output, 0, false, 2));
            try
            {
                Check(DuplicateHandle(GetCurrentProcess(), errRead, GetCurrentProcess(), out var error, 0, false, 2));
                return new(Process.GetProcessById(pi.ProcessId), output, error);
            }
            catch { output.Dispose(); throw; }
        }
        finally
        {
            if (initialized) DeleteProcThreadAttributeList(attributes);
            Marshal.FreeHGlobal(attributes); Marshal.FreeHGlobal(handles);
        }
    }
    private static string Quote(string value)
    {
        // CommandLineToArgvW/CRT escaping, including trailing backslashes and quotes.
        var result = new StringBuilder("\""); var slashes = 0;
        foreach (var c in value)
        {
            if (c == '\\') { slashes++; continue; }
            result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes); result.Append(c); slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }
    private static void Check(bool success) { if (!success) throw new Win32Exception(Marshal.GetLastWin32Error()); }
    public void Dispose() { Output.Dispose(); Error.Dispose(); Process.Dispose(); }

    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { public int Length; public IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool Inherit; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo
    {
        public int Size; public string? Reserved, Desktop, Title;
        public int X, Y, XSize, YSize, XChars, YChars, Fill, Flags;
        public short Show, ReservedBytes; public IntPtr ReservedPointer, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public StartupInfo Startup; public IntPtr Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public IntPtr Process, Thread; public int ProcessId, ThreadId; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SecurityAttributes attributes, int size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetHandleInformation(SafeFileHandle handle, int mask, int flags);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool DuplicateHandle(IntPtr sourceProcess, SafeFileHandle source, IntPtr targetProcess, out SafeFileHandle target, int access, bool inherit, int options);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(IntPtr list, int flags, IntPtr attribute, IntPtr value, nuint size, IntPtr previous, IntPtr returnedSize);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool CreateProcessW(string application, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes, bool inherit, int flags, IntPtr environment, string? directory, ref StartupInfoEx startup, out ProcessInformation process);
}
