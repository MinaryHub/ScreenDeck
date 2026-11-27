using System;
using System.Runtime.InteropServices;

namespace ScreenDeck
{
    /// <summary>
    /// Captures one monitor with DXGI Desktop Duplication. Unlike GDI BitBlt this reads the composed desktop on
    /// the GPU and only hands out a frame when something on the monitor changed, so a still screen costs
    /// almost nothing. All calls must come from the thread that created the instance.
    /// <para>
    /// COM calls go through the vtables directly (no extra dependency). Slot numbers, IIDs and struct layouts
    /// follow the Windows SDK headers dxgi.h, dxgi1_2.h and d3d11.h.
    /// </para>
    /// </summary>
    internal sealed unsafe class DxgiDuplicator : IDisposable
    {
        public enum FrameResult { Frame, NoChange, Lost, Failed }

        public delegate void FrameHandler(IntPtr pixels, int width, int height, int stride);

        // ── HRESULTs ──
        private const int DXGI_ERROR_NOT_FOUND = unchecked((int)0x887A0002);
        private const int DXGI_ERROR_ACCESS_LOST = unchecked((int)0x887A0026);
        private const int DXGI_ERROR_WAIT_TIMEOUT = unchecked((int)0x887A0027);

        // ── Constants ──
        private const int DXGI_FORMAT_B8G8R8A8_UNORM = 87, DXGI_FORMAT_B8G8R8A8_UNORM_SRGB = 91;
        private const int DXGI_MODE_ROTATION_UNSPECIFIED = 0, DXGI_MODE_ROTATION_IDENTITY = 1;
        private const int D3D_DRIVER_TYPE_UNKNOWN = 0;
        private const uint D3D11_SDK_VERSION = 7;
        private const int D3D11_USAGE_STAGING = 3;
        private const uint D3D11_CPU_ACCESS_READ = 0x20000;
        private const int D3D11_MAP_READ = 1;

        // ── IIDs ──
        private static readonly Guid IID_IDXGIFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
        private static readonly Guid IID_IDXGIOutput1 = new("00cddea8-939b-4b83-a340-a685226666cc");
        private static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

        // ── Vtable slots ──
        private const int Slot_QueryInterface = 0, Slot_Release = 2;
        private const int Slot_Factory_EnumAdapters = 7;      // IDXGIFactory
        private const int Slot_Adapter_EnumOutputs = 7;       // IDXGIAdapter
        private const int Slot_Output_GetDesc = 7;            // IDXGIOutput
        private const int Slot_Output1_DuplicateOutput = 22;  // IDXGIOutput1
        private const int Slot_Dupl_AcquireNextFrame = 8;     // IDXGIOutputDuplication
        private const int Slot_Dupl_ReleaseFrame = 14;
        private const int Slot_Device_CreateTexture2D = 5;    // ID3D11Device
        private const int Slot_Context_Map = 14;              // ID3D11DeviceContext
        private const int Slot_Context_Unmap = 15;
        private const int Slot_Context_CopyResource = 47;
        private const int Slot_Texture2D_GetDesc = 10;        // ID3D11Texture2D

        // DXGI_OUTPUT_DESC
        [StructLayout(LayoutKind.Sequential)]
        private struct DXGI_OUTPUT_DESC
        {
            public fixed char DeviceName[32];
            public Native.RECT DesktopCoordinates;
            public int AttachedToDesktop;
            public int Rotation;
            public IntPtr Monitor;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DXGI_OUTDUPL_FRAME_INFO
        {
            public long LastPresentTime;
            public long LastMouseUpdateTime;
            public uint AccumulatedFrames;
            public int RectsCoalesced;
            public int ProtectedContentMaskedOut;
            public Native.POINT PointerPosition;
            public int PointerVisible;
            public uint TotalMetadataBufferSize;
            public uint PointerShapeBufferSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct D3D11_TEXTURE2D_DESC
        {
            public uint Width, Height, MipLevels, ArraySize;
            public int Format;
            public uint SampleCount, SampleQuality;
            public int Usage;
            public uint BindFlags, CPUAccessFlags, MiscFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct D3D11_MAPPED_SUBRESOURCE
        {
            public IntPtr pData;
            public uint RowPitch, DepthPitch;
        }

        [DllImport("dxgi.dll")]
        private static extern int CreateDXGIFactory1(in Guid riid, out IntPtr factory);

        [DllImport("d3d11.dll")]
        private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags,
            IntPtr featureLevels, uint featureLevelCount, uint sdkVersion, out IntPtr device, out int featureLevel,
            out IntPtr immediateContext);

        private IntPtr _device, _context, _duplication, _staging;
        private int _stagingWidth, _stagingHeight, _stagingFormat;

        private DxgiDuplicator() { }

        /// <summary>
        /// Starts duplicating the monitor with the given handle or device name. Returns null when it can't
        /// (secure desktop, rotated monitor, remote session, hybrid-GPU limits, too many duplications...);
        /// <paramref name="error"/> then says why.
        /// </summary>
        public static DxgiDuplicator? TryCreate(IntPtr monitor, string deviceName, out string? error)
        {
            var d = new DxgiDuplicator();
            try
            {
                error = d.Init(monitor, deviceName);
                if (error == null) return d;
            }
            catch (Exception ex) { error = ex.Message; }
            d.Dispose();
            return null;
        }

        private string? Init(IntPtr monitor, string deviceName)
        {
            int hr = CreateDXGIFactory1(IID_IDXGIFactory1, out IntPtr factory);
            if (hr < 0) return $"CreateDXGIFactory1 0x{hr:X8}";
            IntPtr adapter = IntPtr.Zero, output = IntPtr.Zero, output1 = IntPtr.Zero;
            try
            {
                // Find the adapter output that shows this monitor.
                int rotation = DXGI_MODE_ROTATION_UNSPECIFIED;
                for (uint a = 0; output == IntPtr.Zero; a++)
                {
                    IntPtr ad;
                    hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Vtbl(factory, Slot_Factory_EnumAdapters))(factory, a, &ad);
                    if (hr == DXGI_ERROR_NOT_FOUND) return "monitor not found on any adapter";
                    if (hr < 0) return $"EnumAdapters 0x{hr:X8}";

                    for (uint o = 0; ; o++)
                    {
                        IntPtr op;
                        hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Vtbl(ad, Slot_Adapter_EnumOutputs))(ad, o, &op);
                        if (hr < 0) break;   // DXGI_ERROR_NOT_FOUND: no more outputs on this adapter
                        DXGI_OUTPUT_DESC raw;
                        hr = ((delegate* unmanaged[Stdcall]<IntPtr, DXGI_OUTPUT_DESC*, int>)Vtbl(op, Slot_Output_GetDesc))(op, &raw);
                        string name = new string(raw.DeviceName);
                        int nul = name.IndexOf('\0');
                        if (nul >= 0) name = name[..nul];
                        if (hr >= 0 && (raw.Monitor == monitor || string.Equals(name, deviceName, StringComparison.OrdinalIgnoreCase)))
                        {
                            output = op;
                            rotation = raw.Rotation;
                            break;
                        }
                        Release(op);
                    }
                    if (output != IntPtr.Zero) adapter = ad;
                    else Release(ad);
                }

                // Frames of a rotated monitor arrive unrotated; leave those to GDI.
                if (rotation != DXGI_MODE_ROTATION_UNSPECIFIED && rotation != DXGI_MODE_ROTATION_IDENTITY)
                    return "rotated monitor";

                hr = D3D11CreateDevice(adapter, D3D_DRIVER_TYPE_UNKNOWN, IntPtr.Zero, 0, IntPtr.Zero, 0,
                    D3D11_SDK_VERSION, out _device, out _, out _context);
                if (hr < 0) return $"D3D11CreateDevice 0x{hr:X8}";

                fixed (Guid* iid = &IID_IDXGIOutput1)
                {
                    IntPtr o1;
                    hr = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)Vtbl(output, Slot_QueryInterface))(output, iid, &o1);
                    if (hr < 0) return $"IDXGIOutput1 0x{hr:X8}";   // Windows 7
                    output1 = o1;
                }

                IntPtr dup;
                hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)Vtbl(output1, Slot_Output1_DuplicateOutput))(output1, _device, &dup);
                if (hr < 0) return $"DuplicateOutput 0x{hr:X8}";
                _duplication = dup;
                return null;
            }
            finally
            {
                Release(output1);
                Release(output);
                Release(adapter);
                Release(factory);
            }
        }

        /// <summary>
        /// Waits up to <paramref name="timeoutMs"/> for the monitor to change. On a change, calls
        /// <paramref name="onFrame"/> with the new image (BGRA, valid only during the call) and returns
        /// <see cref="FrameResult.Frame"/>. <see cref="FrameResult.Lost"/> means the duplication has to be
        /// recreated (mode change, desktop switch, UAC prompt).
        /// </summary>
        public FrameResult TryGetFrame(int timeoutMs, FrameHandler onFrame)
        {
            if (_duplication == IntPtr.Zero) return FrameResult.Failed;

            DXGI_OUTDUPL_FRAME_INFO info;
            IntPtr resource;
            int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, DXGI_OUTDUPL_FRAME_INFO*, IntPtr*, int>)
                Vtbl(_duplication, Slot_Dupl_AcquireNextFrame))(_duplication, (uint)Math.Max(0, timeoutMs), &info, &resource);
            if (hr == DXGI_ERROR_WAIT_TIMEOUT) return FrameResult.NoChange;
            if (hr == DXGI_ERROR_ACCESS_LOST) return FrameResult.Lost;
            if (hr < 0) return FrameResult.Failed;

            bool copied = false;
            IntPtr texture = IntPtr.Zero;
            try
            {
                // LastPresentTime 0 = only the mouse pointer moved; the image is unchanged.
                if (info.LastPresentTime == 0) return FrameResult.NoChange;

                fixed (Guid* iid = &IID_ID3D11Texture2D)
                {
                    IntPtr t;
                    hr = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)Vtbl(resource, Slot_QueryInterface))(resource, iid, &t);
                    if (hr < 0) return FrameResult.Failed;
                    texture = t;
                }

                D3D11_TEXTURE2D_DESC desc;
                ((delegate* unmanaged[Stdcall]<IntPtr, D3D11_TEXTURE2D_DESC*, void>)Vtbl(texture, Slot_Texture2D_GetDesc))(texture, &desc);
                if (desc.Format != DXGI_FORMAT_B8G8R8A8_UNORM && desc.Format != DXGI_FORMAT_B8G8R8A8_UNORM_SRGB)
                    return FrameResult.Failed;
                if (!EnsureStaging((int)desc.Width, (int)desc.Height, desc.Format)) return FrameResult.Failed;

                ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, void>)Vtbl(_context, Slot_Context_CopyResource))(_context, _staging, texture);
                copied = true;
            }
            finally
            {
                Release(texture);
                Release(resource);
                // Give the frame back as soon as the copy is queued, so the system can keep updating it.
                ((delegate* unmanaged[Stdcall]<IntPtr, int>)Vtbl(_duplication, Slot_Dupl_ReleaseFrame))(_duplication);
            }
            if (!copied) return FrameResult.Failed;

            D3D11_MAPPED_SUBRESOURCE mapped;
            hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int, uint, D3D11_MAPPED_SUBRESOURCE*, int>)
                Vtbl(_context, Slot_Context_Map))(_context, _staging, 0, D3D11_MAP_READ, 0, &mapped);
            if (hr < 0) return FrameResult.Failed;
            try
            {
                onFrame(mapped.pData, _stagingWidth, _stagingHeight, (int)mapped.RowPitch);
            }
            finally
            {
                ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void>)Vtbl(_context, Slot_Context_Unmap))(_context, _staging, 0);
            }
            return FrameResult.Frame;
        }

        /// <summary>CPU-readable texture the desktop image is copied into.</summary>
        private bool EnsureStaging(int width, int height, int format)
        {
            if (_staging != IntPtr.Zero && width == _stagingWidth && height == _stagingHeight && format == _stagingFormat)
                return true;
            Release(_staging);
            _staging = IntPtr.Zero;

            var desc = new D3D11_TEXTURE2D_DESC
            {
                Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1, Format = format,
                SampleCount = 1, SampleQuality = 0, Usage = D3D11_USAGE_STAGING, BindFlags = 0,
                CPUAccessFlags = D3D11_CPU_ACCESS_READ, MiscFlags = 0,
            };
            IntPtr tex;
            int hr = ((delegate* unmanaged[Stdcall]<IntPtr, D3D11_TEXTURE2D_DESC*, IntPtr, IntPtr*, int>)
                Vtbl(_device, Slot_Device_CreateTexture2D))(_device, &desc, IntPtr.Zero, &tex);
            if (hr < 0) return false;
            _staging = tex;
            _stagingWidth = width;
            _stagingHeight = height;
            _stagingFormat = format;
            return true;
        }

        private static void* Vtbl(IntPtr obj, int slot) => (*(void***)obj)[slot];

        private static void Release(IntPtr obj)
        {
            if (obj != IntPtr.Zero)
                ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Vtbl(obj, Slot_Release))(obj);
        }

        public void Dispose()
        {
            Release(_staging);
            Release(_duplication);
            Release(_context);
            Release(_device);
            _staging = _duplication = _context = _device = IntPtr.Zero;
        }
    }
}
