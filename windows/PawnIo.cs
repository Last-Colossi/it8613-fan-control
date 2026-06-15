// Minimal, self-contained PawnIO client.
//
// PawnIO (https://pawnio.eu) is a signed, HVCI-compatible kernel driver that runs
// signed bytecode "modules". This talks to the driver directly via DeviceIoControl,
// exactly like LibreHardwareMonitor's PawnIo.cs (so it needs no PawnIOLib.dll).
//
// We load the signed "LpcIO" module (the same one LHM/FanControl uses) and call its
// ioctls to do Super-I/O and port reads/writes against the IT8613 EC.

using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace FanControl.IT8613
{
    internal sealed class PawnIo : IDisposable
    {
        private const uint DEVICE_TYPE = 41394u << 16;
        private const int FN_NAME_LENGTH = 32;
        private const uint IOCTL_PIO_EXECUTE_FN = 0x841u << 2;
        private const uint IOCTL_PIO_LOAD_BINARY = 0x821u << 2;
        private const uint CTL_LOAD_BINARY = DEVICE_TYPE | IOCTL_PIO_LOAD_BINARY;
        private const uint CTL_EXECUTE = DEVICE_TYPE | IOCTL_PIO_EXECUTE_FN;

        private static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

        private IntPtr _handle = INVALID_HANDLE_VALUE;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeviceIoControl(IntPtr hDevice, uint dwIoControlCode,
            byte[] lpInBuffer, uint nInBufferSize, byte[] lpOutBuffer, uint nOutBufferSize,
            out uint lpBytesReturned, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);

        public bool IsLoaded => _handle != INVALID_HANDLE_VALUE && _handle != IntPtr.Zero;

        public bool LoadModuleFromResource(Assembly assembly, string resourceName)
        {
            const uint GENERIC_READ_WRITE = 0xC0000000u;
            const uint FILE_SHARE_RW = 0x00000003u;
            const uint OPEN_EXISTING = 3u;
            const uint FILE_ATTRIBUTE_NORMAL = 0x80u;

            _handle = CreateFileW(@"\\?\GLOBALROOT\Device\PawnIO", GENERIC_READ_WRITE, FILE_SHARE_RW,
                IntPtr.Zero, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);

            if (_handle == INVALID_HANDLE_VALUE)
                return false;

            byte[] bin;
            using (Stream stream = assembly.GetManifestResourceStream(resourceName))
            {
                if (stream == null)
                {
                    Dispose();
                    return false;
                }

                using (MemoryStream memory = new MemoryStream())
                {
                    stream.CopyTo(memory);
                    bin = memory.ToArray();
                }
            }

            if (!DeviceIoControl(_handle, CTL_LOAD_BINARY, bin, (uint)bin.Length, null, 0, out _, IntPtr.Zero))
            {
                Dispose();
                return false;
            }

            return true;
        }

        // Calls a named function in the loaded module. Input buffer is a 32-byte ASCII
        // name followed by the 64-bit arguments; output is up to outLength 64-bit values.
        public long[] Execute(string name, long[] input, int outLength)
        {
            if (!IsLoaded)
                return new long[outLength];

            byte[] output = new byte[outLength * sizeof(long)];
            byte[] totalInput = new byte[(input.Length * sizeof(long)) + FN_NAME_LENGTH];

            byte[] nameBytes = Encoding.ASCII.GetBytes(name);
            Buffer.BlockCopy(nameBytes, 0, totalInput, 0, Math.Min(FN_NAME_LENGTH - 1, nameBytes.Length));
            Buffer.BlockCopy(input, 0, totalInput, FN_NAME_LENGTH, input.Length * sizeof(long));

            if (DeviceIoControl(_handle, CTL_EXECUTE, totalInput, (uint)totalInput.Length,
                    output, (uint)output.Length, out uint read, IntPtr.Zero))
            {
                long[] result = new long[read / sizeof(long)];
                Buffer.BlockCopy(output, 0, result, 0, (int)read);
                return result;
            }

            return new long[outLength];
        }

        public void Dispose()
        {
            if (_handle != INVALID_HANDLE_VALUE && _handle != IntPtr.Zero)
                CloseHandle(_handle);

            _handle = INVALID_HANDLE_VALUE;
        }
    }
}
