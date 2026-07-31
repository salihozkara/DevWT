using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Devwt.Service;

public interface IProcessCurrentDirectoryReader
{
    string? TryRead(int processId);
}

public sealed class WindowsProcessCurrentDirectoryReader : IProcessCurrentDirectoryReader
{
    private const int ProcessQueryLimitedInformation = 0x1000;
    private const int ProcessVmRead = 0x0010;
    private const int ProcessBasicInformation = 0;
    private const int ProcessWow64Information = 26;

    public string? TryRead(int processId)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return null;
        }

        var handle = OpenProcess(ProcessQueryLimitedInformation | ProcessVmRead, inheritHandle: false, processId);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return TryReadCore(handle);
        }
        catch (Win32Exception)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static string? TryReadCore(IntPtr processHandle)
    {
        try
        {
            return TryReadNativeCurrentDirectory(processHandle);
        }
        catch (Win32Exception)
        {
            return TryReadWow64CurrentDirectory(processHandle);
        }
        catch (InvalidOperationException)
        {
            return TryReadWow64CurrentDirectory(processHandle);
        }
    }

    private static string? TryReadNativeCurrentDirectory(IntPtr processHandle)
    {
        var basicInformation = new ProcessBasicInformationData();
        var status = NtQueryInformationProcess(
            processHandle,
            ProcessBasicInformation,
            ref basicInformation,
            Marshal.SizeOf<ProcessBasicInformationData>(),
            out _);
        if (status != 0 || basicInformation.PebBaseAddress == IntPtr.Zero)
        {
            return null;
        }

        var processParametersAddress = ReadIntPtr(
            processHandle,
            basicInformation.PebBaseAddress + ProcessParametersPebOffset);
        if (processParametersAddress == IntPtr.Zero)
        {
            return null;
        }

        var currentDirectoryUnicodeStringAddress = processParametersAddress + CurrentDirectoryOffset;
        var length = ReadUInt16(processHandle, currentDirectoryUnicodeStringAddress);
        if (length == 0)
        {
            return null;
        }

        var bufferAddress = ReadIntPtr(processHandle, currentDirectoryUnicodeStringAddress + UnicodeStringBufferOffset);
        if (bufferAddress == IntPtr.Zero)
        {
            return null;
        }

        var buffer = ReadBytes(processHandle, bufferAddress, length);
        return System.Text.Encoding.Unicode.GetString(buffer).TrimEnd('\0');
    }

    private static string? TryReadWow64CurrentDirectory(IntPtr processHandle)
    {
        var wow64Peb = IntPtr.Zero;
        var status = NtQueryInformationProcess(
            processHandle,
            ProcessWow64Information,
            ref wow64Peb,
            IntPtr.Size,
            out _);
        if (status != 0 || wow64Peb == IntPtr.Zero)
        {
            return null;
        }

        var processParametersAddress = ReadIntPtr32(processHandle, wow64Peb + 0x10);
        if (processParametersAddress == IntPtr.Zero)
        {
            return null;
        }

        var currentDirectoryUnicodeStringAddress = processParametersAddress + 0x24;
        var length = ReadUInt16(processHandle, currentDirectoryUnicodeStringAddress);
        if (length == 0)
        {
            return null;
        }

        var bufferAddress = ReadIntPtr32(processHandle, currentDirectoryUnicodeStringAddress + 0x04);
        if (bufferAddress == IntPtr.Zero)
        {
            return null;
        }

        var buffer = ReadBytes(processHandle, bufferAddress, length);
        return System.Text.Encoding.Unicode.GetString(buffer).TrimEnd('\0');
    }

    private static int ProcessParametersPebOffset => IntPtr.Size == 8 ? 0x20 : 0x10;

    private static int CurrentDirectoryOffset => IntPtr.Size == 8 ? 0x38 : 0x24;

    private static int UnicodeStringBufferOffset => IntPtr.Size == 8 ? 0x08 : 0x04;

    private static ushort ReadUInt16(IntPtr processHandle, IntPtr address)
    {
        var bytes = ReadBytes(processHandle, address, sizeof(ushort));
        return BitConverter.ToUInt16(bytes, 0);
    }

    private static IntPtr ReadIntPtr(IntPtr processHandle, IntPtr address)
    {
        var bytes = ReadBytes(processHandle, address, IntPtr.Size);
        return IntPtr.Size == 8
            ? new IntPtr(BitConverter.ToInt64(bytes, 0))
            : new IntPtr(BitConverter.ToInt32(bytes, 0));
    }

    private static IntPtr ReadIntPtr32(IntPtr processHandle, IntPtr address)
    {
        var bytes = ReadBytes(processHandle, address, sizeof(uint));
        return new IntPtr(unchecked((long)BitConverter.ToUInt32(bytes, 0)));
    }

    private static byte[] ReadBytes(IntPtr processHandle, IntPtr address, int length)
    {
        var buffer = new byte[length];
        if (!ReadProcessMemory(processHandle, address, buffer, buffer.Length, out var bytesRead)
            || bytesRead.ToInt64() != length)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return buffer;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(
        IntPtr processHandle,
        IntPtr baseAddress,
        byte[] buffer,
        int size,
        out IntPtr numberOfBytesRead);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        ref ProcessBasicInformationData processInformation,
        int processInformationLength,
        out int returnLength);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        ref IntPtr processInformation,
        int processInformationLength,
        out int returnLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformationData
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2A;
        public IntPtr Reserved2B;
        public IntPtr UniqueProcessId;
        public IntPtr Reserved3;
    }
}
