using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace PRTSCN.Recording;

/// <summary>
/// The glue between Windows.Graphics.Capture (WinRT) and Direct3D 11 (Vortice):
/// a WinRT device wrapping our D3D11 device, capture items for a window or a monitor,
/// and the D3D11 texture behind a captured frame.
/// </summary>
internal static unsafe class CaptureInterop
{
    static readonly Guid IID_GraphicsCaptureItemInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    static readonly Guid IID_GraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    static readonly Guid IID_DxgiInterfaceAccess = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
    static readonly Guid IID_Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    [DllImport("d3d11.dll", ExactSpelling = true)]
    static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    public static bool IsSupported
    {
        get
        {
            try { return GraphicsCaptureSession.IsSupported(); }
            catch { return false; }
        }
    }

    public static (ID3D11Device device, ID3D11DeviceContext context) CreateDevice()
    {
        var levels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 };
        var flags = DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport;
        var hr = D3D11.D3D11CreateDevice(null, DriverType.Hardware, flags, levels, out ID3D11Device? device, out ID3D11DeviceContext? ctx);
        if (hr.Failure || device == null || ctx == null)
        {
            hr = D3D11.D3D11CreateDevice(null, DriverType.Warp, DeviceCreationFlags.BgraSupport, levels, out device, out ctx);
            hr.CheckError();
        }
        // The capture callback and the encoder thread both use the immediate context.
        using (var mt = device!.QueryInterface<ID3D11Multithread>()) mt.SetMultithreadProtected(true);
        return (device!, ctx!);
    }

    public static IDirect3DDevice CreateWinRtDevice(ID3D11Device device)
    {
        using var dxgi = device.QueryInterface<IDXGIDevice>();
        Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out var p));
        try { return MarshalInterface<IDirect3DDevice>.FromAbi(p); }
        finally { Marshal.Release(p); }
    }

    public static GraphicsCaptureItem ForWindow(IntPtr hwnd) => Create(hwnd, slot: 3);
    public static GraphicsCaptureItem ForMonitor(IntPtr hmon) => Create(hmon, slot: 4);

    static GraphicsCaptureItem Create(IntPtr handle, int slot)
    {
        var factory = ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(factory.ThisPtr, in IID_GraphicsCaptureItemInterop, out var interop));
        try
        {
            var vtbl = *(IntPtr**)interop;
            var create = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)vtbl[slot];
            Guid iid = IID_GraphicsCaptureItem;
            IntPtr item;
            Marshal.ThrowExceptionForHR(create(interop, handle, &iid, &item));
            try { return GraphicsCaptureItem.FromAbi(item); }
            finally { Marshal.Release(item); }
        }
        finally { Marshal.Release(interop); }
    }

    public static ID3D11Texture2D TextureOf(IDirect3DSurface surface)
    {
        IntPtr unk = MarshalInterface<IDirect3DSurface>.FromManaged(surface);
        try
        {
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unk, in IID_DxgiInterfaceAccess, out var access));
            try
            {
                var vtbl = *(IntPtr**)access;
                var getInterface = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)vtbl[3];
                Guid iid = IID_Texture2D;
                IntPtr tex;
                Marshal.ThrowExceptionForHR(getInterface(access, &iid, &tex));
                return new ID3D11Texture2D(tex);
            }
            finally { Marshal.Release(access); }
        }
        finally { Marshal.Release(unk); }
    }
}
