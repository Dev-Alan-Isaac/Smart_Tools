using Smart_Tools.WPF.Models;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// The graphics cards as DirectX lists them (dxgi.dll, part of Windows):
    /// name, adapter id (LUID - the id Windows' GPU counters use), dedicated
    /// and shared memory sizes. Same source as Task Manager; no package, no
    /// driver. Read once when monitoring starts, on the loop's thread.
    /// </summary>
    public sealed partial class SystemMonitorManager
    {
        private const uint DXGI_ADAPTER_FLAG_SOFTWARE = 2;   // "Microsoft Basic Render Driver"
        private const int DXGI_ERROR_NOT_FOUND = unchecked((int)0x887A0002);

        /// <summary>Every real graphics adapter (software renderers left out). Empty if DirectX can't be asked.</summary>
        private static List<SystemMonitorGpuAdapter> ReadGpuAdapters()
        {
            var list = new List<SystemMonitorGpuAdapter>();
            IDXGIFactory1? factory = null;
            try
            {
                var iid = typeof(IDXGIFactory1).GUID;
                if (CreateDXGIFactory1(ref iid, out factory) < 0 || factory == null) return list;
                for (uint i = 0; i < 16; i++)
                {
                    int hr = factory.EnumAdapters1(i, out IDXGIAdapter1? adapter);
                    if (hr == DXGI_ERROR_NOT_FOUND || hr < 0 || adapter == null) break;
                    try
                    {
                        if (adapter.GetDesc1(out DXGI_ADAPTER_DESC1 desc) < 0 || (desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) != 0) continue;
                        string luid = FormatLuid(desc.LuidLowPart, desc.LuidHighPart);
                        if (list.Any(a => a.Luid.Equals(luid, StringComparison.OrdinalIgnoreCase))) continue;
                        list.Add(new SystemMonitorGpuAdapter(
                            luid,
                            CleanSpecValue(desc.Description) ?? "Graphics card",
                            (long)desc.DedicatedVideoMemory,
                            (long)desc.SharedSystemMemory));
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(adapter);
                    }
                }
            }
            catch (Exception ex) when (ex is COMException or DllNotFoundException or EntryPointNotFoundException or InvalidCastException)
            {
                Log.Info($"System Monitor: DirectX adapter list unavailable: {ex.Message}");
            }
            finally
            {
                if (factory != null) Marshal.ReleaseComObject(factory);
            }
            return list;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DXGI_ADAPTER_DESC1
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string Description;
            public uint VendorId;
            public uint DeviceId;
            public uint SubSysId;
            public uint Revision;
            public nuint DedicatedVideoMemory;
            public nuint DedicatedSystemMemory;
            public nuint SharedSystemMemory;
            public uint LuidLowPart;
            public int LuidHighPart;
            public uint Flags;
        }

        // COM interfaces list every method in vtable order (base interfaces
        // first); only EnumAdapters1 and GetDesc1 are called, the rest just
        // hold their places.

        [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDXGIFactory1
        {
            // IDXGIObject
            [PreserveSig] int SetPrivateData();
            [PreserveSig] int SetPrivateDataInterface();
            [PreserveSig] int GetPrivateData();
            [PreserveSig] int GetParent();

            // IDXGIFactory
            [PreserveSig] int EnumAdapters();
            [PreserveSig] int MakeWindowAssociation();
            [PreserveSig] int GetWindowAssociation();
            [PreserveSig] int CreateSwapChain();
            [PreserveSig] int CreateSoftwareAdapter();

            // IDXGIFactory1
            [PreserveSig] int EnumAdapters1(uint index, out IDXGIAdapter1? adapter);
            [PreserveSig] int IsCurrent();
        }

        [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDXGIAdapter1
        {
            // IDXGIObject
            [PreserveSig] int SetPrivateData();
            [PreserveSig] int SetPrivateDataInterface();
            [PreserveSig] int GetPrivateData();
            [PreserveSig] int GetParent();

            // IDXGIAdapter
            [PreserveSig] int EnumOutputs();
            [PreserveSig] int GetDesc();
            [PreserveSig] int CheckInterfaceSupport();

            // IDXGIAdapter1
            [PreserveSig] int GetDesc1(out DXGI_ADAPTER_DESC1 desc);
        }

        [DllImport("dxgi.dll")]
        private static extern int CreateDXGIFactory1(ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IDXGIFactory1? factory);
    }
}
