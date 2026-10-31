using Microsoft.Win32.SafeHandles;
using Smart_Tools.WPF.Models;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Talking to the drives directly (Windows only). Every call here
    /// returns null instead of throwing - a drive that won't answer is a
    /// normal outcome (USB bridges, RAID, no admin rights).
    /// </summary>
    public static partial class DriveHealthManager
    {
        /// <summary>Whether this process runs elevated (ATA SMART needs it).</summary>
        public static bool IsRunningAsAdministrator()
        {
            if (!OperatingSystem.IsWindows()) return false;
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch (Exception)
            {
                // Can't tell - assume not, so the view offers the restart.
                return false;
            }
        }

        // =================================================================
        //  Win32
        // =================================================================

        private const uint GenericRead = 0x80000000;
        private const uint GenericWrite = 0x40000000;
        private const uint FileShareRead = 0x00000001;
        private const uint FileShareWrite = 0x00000002;
        private const uint OpenExisting = 3;

        internal const uint IoctlAtaPassThrough = 0x0004D02C;
        internal const uint IoctlStorageQueryProperty = 0x002D1400;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateFileW")]
        private static extern SafeFileHandle CreateFile(
            string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwCreationDisposition,
            uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeviceIoControl(
            SafeFileHandle hDevice, uint dwIoControlCode,
            IntPtr lpInBuffer, uint nInBufferSize,
            IntPtr lpOutBuffer, uint nOutBufferSize,
            out uint lpBytesReturned, IntPtr lpOverlapped);

        private static SafeFileHandle? OpenDisk(int diskNumber, uint access)
        {
            var handle = CreateFile($@"\\.\PhysicalDrive{diskNumber}", access, FileShareRead | FileShareWrite,
                IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (!handle.IsInvalid) return handle;
            handle.Dispose();
            return null;
        }

        // =================================================================
        //  ATA (SATA drives)
        // =================================================================

        [StructLayout(LayoutKind.Sequential)]
        internal struct AtaPassThroughEx
        {
            public ushort Length;
            public ushort AtaFlags;
            public byte PathId;
            public byte TargetId;
            public byte Lun;
            public byte ReservedAsUchar;
            public uint DataTransferLength;
            public uint TimeOutValue;
            public uint ReservedAsUlong;
            public IntPtr DataBufferOffset;   // ULONG_PTR
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
            public byte[] PreviousTaskFile;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
            public byte[] CurrentTaskFile;
        }

        private const ushort AtaFlagsDrdyRequired = 0x01;
        private const ushort AtaFlagsDataIn = 0x02;
        private const byte SmartReadData = 0xD0;
        private const byte SmartReadThresholds = 0xD1;

        /// <summary>Attributes + thresholds from a SATA drive, or null. Needs admin.</summary>
        private static List<SmartAttribute>? TryReadAtaSmart(int diskNumber)
        {
            if (!OperatingSystem.IsWindows()) return null;

            using var handle = OpenDisk(diskNumber, GenericRead | GenericWrite);
            if (handle == null) return null;

            byte[]? data = AtaSmartCommand(handle, SmartReadData);
            if (data == null) return null;

            // Thresholds are optional - without them the table still shows, just no "failing" check.
            byte[]? thresholdPage = AtaSmartCommand(handle, SmartReadThresholds);
            var thresholds = thresholdPage != null ? ParseAtaThresholds(thresholdPage) : null;

            var attributes = ParseAtaAttributes(data, thresholds);
            return attributes.Count > 0 ? attributes : null;
        }

        /// <summary>Sends SMART (B0h) with the given feature and returns the 512-byte page.</summary>
        private static byte[]? AtaSmartCommand(SafeFileHandle handle, byte feature)
        {
            int headerSize = Marshal.SizeOf<AtaPassThroughEx>();
            const int DataSize = 512;
            int totalSize = headerSize + DataSize;

            IntPtr buffer = Marshal.AllocHGlobal(totalSize);
            try
            {
                // Zeroed, so stale heap bytes can't pass for SMART data.
                Marshal.Copy(new byte[totalSize], 0, buffer, totalSize);

                var header = new AtaPassThroughEx
                {
                    Length = (ushort)headerSize,
                    AtaFlags = AtaFlagsDataIn | AtaFlagsDrdyRequired,
                    DataTransferLength = DataSize,
                    TimeOutValue = 10,
                    DataBufferOffset = (IntPtr)headerSize,
                    PreviousTaskFile = new byte[8],
                    CurrentTaskFile = new byte[8]
                    {
                        feature, // Features: SMART subcommand
                        0x01,    // Sector count
                        0x00,    // Sector number
                        0x4F,    // LBA mid  - SMART signature
                        0xC2,    // LBA high - SMART signature
                        0xA0,    // Device/head
                        0xB0,    // Command: SMART
                        0x00,
                    },
                };
                Marshal.StructureToPtr(header, buffer, false);

                if (!DeviceIoControl(handle, IoctlAtaPassThrough, buffer, (uint)totalSize, buffer, (uint)totalSize, out _, IntPtr.Zero))
                    return null;

                var data = new byte[DataSize];
                Marshal.Copy(buffer + headerSize, data, 0, DataSize);
                return data.Any(b => b != 0) ? data : null;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        // =================================================================
        //  NVMe
        // =================================================================

        // STORAGE_PROPERTY_QUERY header up to AdditionalParameters: PropertyId + QueryType.
        internal const int StoragePropertyQueryHeaderSize = 8;

        // STORAGE_PROTOCOL_SPECIFIC_DATA: ten ULONGs.
        internal const int ProtocolSpecificDataSize = 40;

        // STORAGE_PROTOCOL_DATA_DESCRIPTOR: Version + Size, then the protocol data.
        internal const int ProtocolDataDescriptorHeaderSize = 8;

        internal const int StorageAdapterProtocolSpecificProperty = 49;
        internal const int StorageDeviceProtocolSpecificProperty = 50;
        private const int PropertyStandardQuery = 0;
        private const int ProtocolTypeNvme = 3;
        private const int NvmeDataTypeLogPage = 2;
        private const int NvmeLogPageHealthInfo = 0x02;

        /// <summary>Builds the IOCTL_STORAGE_QUERY_PROPERTY input for the NVMe health log (pure - tested).</summary>
        internal static byte[] BuildNvmeHealthQuery(int propertyId = StorageDeviceProtocolSpecificProperty)
        {
            var buffer = new byte[StoragePropertyQueryHeaderSize + ProtocolSpecificDataSize + NvmeHealthLogSize];
            var span = buffer.AsSpan();

            static void Put(Span<byte> target, int offset, uint value) => System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(target.Slice(offset, 4), value);

            Put(span, 0, (uint)propertyId);
            Put(span, 4, PropertyStandardQuery);

            int p = StoragePropertyQueryHeaderSize;
            Put(span, p + 0, ProtocolTypeNvme);                   // ProtocolType
            Put(span, p + 4, NvmeDataTypeLogPage);                // DataType
            Put(span, p + 8, NvmeLogPageHealthInfo);              // ProtocolDataRequestValue (log page ID)
            Put(span, p + 12, 0);                                 // ProtocolDataRequestSubValue (log offset, low)
            Put(span, p + 16, ProtocolSpecificDataSize);          // ProtocolDataOffset (from the start of this struct)
            Put(span, p + 20, NvmeHealthLogSize);                 // ProtocolDataLength
            return buffer;
        }

        /// <summary>Pulls the 512-byte log out of the IOCTL output (pure - tested). Null when the reply is short.</summary>
        internal static byte[]? ExtractNvmeLog(ReadOnlySpan<byte> output)
        {
            // Output is STORAGE_PROTOCOL_DATA_DESCRIPTOR: Version, Size, then STORAGE_PROTOCOL_SPECIFIC_DATA.
            int spd = ProtocolDataDescriptorHeaderSize;
            if (output.Length < spd + ProtocolSpecificDataSize) return null;

            uint dataOffset = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(output.Slice(spd + 16, 4));
            uint dataLength = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(output.Slice(spd + 20, 4));
            if (dataLength < NvmeHealthLogSize) return null;

            long start = spd + (long)dataOffset;
            if (start + NvmeHealthLogSize > output.Length) return null;
            return output.Slice((int)start, NvmeHealthLogSize).ToArray();
        }

        /// <summary>
        /// Reads the NVMe health log (Microsoft's "Working with NVMe drives"
        /// recipe). Tries a no-access handle first (enough on many systems
        /// without admin), then read/write; the device property first, then
        /// the adapter one some drivers answer instead.
        /// </summary>
        private static NvmeHealthLog? TryReadNvmeHealth(int diskNumber)
        {
            if (!OperatingSystem.IsWindows()) return null;

            foreach (uint access in new[] { 0u, GenericRead | GenericWrite })
            foreach (int property in new[] { StorageDeviceProtocolSpecificProperty, StorageAdapterProtocolSpecificProperty })
            {
                using var handle = OpenDisk(diskNumber, access);
                if (handle == null) break;

                byte[] query = BuildNvmeHealthQuery(property);
                IntPtr buffer = Marshal.AllocHGlobal(query.Length);
                try
                {
                    Marshal.Copy(query, 0, buffer, query.Length);
                    if (!DeviceIoControl(handle, IoctlStorageQueryProperty, buffer, (uint)query.Length, buffer, (uint)query.Length, out uint returned, IntPtr.Zero))
                        continue;

                    var output = new byte[Math.Min(returned, (uint)query.Length)];
                    Marshal.Copy(buffer, output, 0, output.Length);

                    byte[]? log = ExtractNvmeLog(output);
                    if (log != null && log.Any(b => b != 0)) return ParseNvmeHealthLog(log);
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            return null;
        }
    }
}
