using System.Runtime.InteropServices;

using Windows.Graphics.DirectX.Direct3D11;

namespace RocoPilot.Services.Capture.Backends;

internal static class Direct3D11DeviceFactory
{
    private const uint D3D11CreateDeviceBgraSupport = 0x20;
    private const uint D3D11SdkVersion = 7;
    private const int D3DDriverTypeHardware = 1;
    private const int D3DDriverTypeWarp = 5;

    private static readonly Guid DxgiDeviceGuid = new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");

    public static IDirect3DDevice CreateDevice()
    {
        var result = CreateNativeDevice(D3DDriverTypeHardware, out var d3dDevice, out var immediateContext);
        if (result < 0)
        {
            ReleaseIfNeeded(immediateContext);
            ReleaseIfNeeded(d3dDevice);
            result = CreateNativeDevice(D3DDriverTypeWarp, out d3dDevice, out immediateContext);
        }

        Marshal.ThrowExceptionForHR(result);

        try
        {
            var dxgiDeviceGuid = DxgiDeviceGuid;
            result = Marshal.QueryInterface(d3dDevice, in dxgiDeviceGuid, out var dxgiDevice);
            Marshal.ThrowExceptionForHR(result);

            try
            {
                result = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out var graphicsDevice);
                Marshal.ThrowExceptionForHR(result);
                try
                {
                    return WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(graphicsDevice);
                }
                finally
                {
                    ReleaseIfNeeded(graphicsDevice);
                }
            }
            finally
            {
                ReleaseIfNeeded(dxgiDevice);
            }
        }
        finally
        {
            ReleaseIfNeeded(immediateContext);
            ReleaseIfNeeded(d3dDevice);
        }
    }

    private static int CreateNativeDevice(int driverType, out IntPtr d3dDevice, out IntPtr immediateContext)
    {
        return D3D11CreateDevice(
            IntPtr.Zero,
            driverType,
            IntPtr.Zero,
            D3D11CreateDeviceBgraSupport,
            IntPtr.Zero,
            0,
            D3D11SdkVersion,
            out d3dDevice,
            out _,
            out immediateContext);
    }

    private static void ReleaseIfNeeded(IntPtr value)
    {
        if (value != IntPtr.Zero)
        {
            _ = Marshal.Release(value);
        }
    }

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int D3D11CreateDevice(
        IntPtr adapter,
        int driverType,
        IntPtr software,
        uint flags,
        IntPtr featureLevels,
        uint featureLevelsCount,
        uint sdkVersion,
        out IntPtr device,
        out int featureLevel,
        out IntPtr immediateContext);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(
        IntPtr dxgiDevice,
        out IntPtr graphicsDevice);
}
