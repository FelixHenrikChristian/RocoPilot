using System.Collections.Concurrent;
using System.Runtime.InteropServices.WindowsRuntime;

using RocoPilot.Models.Capture;

using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Foundation.Metadata;
using Windows.Security.Authorization.AppCapabilityAccess;

namespace RocoPilot.Services.Capture.Backends;

public sealed class WindowsGraphicsCaptureBackend : ICaptureBackend, IDisposable
{
    private readonly ConcurrentDictionary<IntPtr, CaptureSessionState> _sessions = new();
    private readonly object _deviceLock = new();

    private IDirect3DDevice? _device;
    private bool _disposed;

    public CaptureMethod Method => CaptureMethod.WindowsGraphicsCapture;

    public CapturedFrame? Capture(CaptureTargetWindow targetWindow)
    {
        if (_disposed || targetWindow.Hwnd == IntPtr.Zero || !GraphicsCaptureSession.IsSupported())
        {
            return null;
        }

        try
        {
            var session = _sessions.GetOrAdd(
                targetWindow.Hwnd,
                hwnd => new CaptureSessionState(hwnd, GetOrCreateDevice(), RemoveSession));

            return session.GetLatestFrame();
        }
        catch
        {
            return null;
        }
    }

    public void Release(CaptureTargetWindow targetWindow)
    {
        if (targetWindow.Hwnd != IntPtr.Zero && _sessions.TryRemove(targetWindow.Hwnd, out var session))
        {
            session.Dispose();
        }
    }

    public void Dispose()
    {
        _disposed = true;

        foreach (var session in _sessions.Values)
        {
            session.Dispose();
        }

        _sessions.Clear();
        _device?.Dispose();
        _device = null;
    }

    private IDirect3DDevice GetOrCreateDevice()
    {
        lock (_deviceLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _device ??= Direct3D11DeviceFactory.CreateDevice();
        }
    }

    private void RemoveSession(IntPtr hwnd, CaptureSessionState session)
    {
        if (_sessions.TryGetValue(hwnd, out var current)
            && ReferenceEquals(current, session)
            && _sessions.TryRemove(hwnd, out var removed))
        {
            removed.Dispose();
        }
    }

    private sealed class CaptureSessionState : IDisposable
    {
        private static readonly TimeSpan FirstFrameTimeout = TimeSpan.FromMilliseconds(800);
        private static readonly object BorderlessAccessLock = new();
        private static AppCapabilityAccessStatus? _borderlessAccessStatus;

        private readonly IntPtr _hwnd;
        private readonly Action<IntPtr, CaptureSessionState> _removeSession;
        private readonly Direct3D11CaptureFramePool _framePool;
        private readonly GraphicsCaptureItem _item;
        private readonly GraphicsCaptureSession _session;
        private readonly ManualResetEventSlim _firstFrameReady = new(false);
        private readonly object _frameLock = new();

        private CapturedFrame? _latestFrame;
        private int _conversionPending;
        private bool _disposed;

        public CaptureSessionState(
            IntPtr hwnd,
            IDirect3DDevice device,
            Action<IntPtr, CaptureSessionState> removeSession)
        {
            _hwnd = hwnd;
            _removeSession = removeSession;
            _item = GraphicsCaptureItemInterop.CreateForWindow(hwnd);

            _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                device,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                2,
                _item.Size);

            _framePool.FrameArrived += FramePool_FrameArrived;
            _item.Closed += Item_Closed;

            _session = _framePool.CreateCaptureSession(_item);
            TryDisableCaptureBorder(_session);
            _session.StartCapture();
        }

        public CapturedFrame? GetLatestFrame()
        {
            if (_disposed)
            {
                return null;
            }

            lock (_frameLock)
            {
                if (_latestFrame is not null)
                {
                    return _latestFrame;
                }
            }

            try
            {
                _ = _firstFrameReady.Wait(FirstFrameTimeout);
            }
            catch (ObjectDisposedException)
            {
                return null;
            }

            lock (_frameLock)
            {
                return _latestFrame;
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _firstFrameReady.Set();
            _framePool.FrameArrived -= FramePool_FrameArrived;
            _item.Closed -= Item_Closed;
            _session.Dispose();
            _framePool.Dispose();
            _firstFrameReady.Dispose();
        }

        private void Item_Closed(GraphicsCaptureItem sender, object args)
        {
            _removeSession(_hwnd, this);
        }

        private async void FramePool_FrameArrived(Direct3D11CaptureFramePool sender, object args)
        {
            if (_disposed)
            {
                return;
            }

            Direct3D11CaptureFrame? frame = null;
            var ownsConversion = false;

            try
            {
                frame = sender.TryGetNextFrame();
                if (frame is null)
                {
                    return;
                }

                ownsConversion = Interlocked.Exchange(ref _conversionPending, 1) == 0;
                if (!ownsConversion)
                {
                    return;
                }

                var capturedFrame = await CreateCapturedFrameAsync(frame).ConfigureAwait(false);

                lock (_frameLock)
                {
                    _latestFrame = capturedFrame;
                }

                _firstFrameReady.Set();
            }
            catch
            {
                _removeSession(_hwnd, this);
            }
            finally
            {
                frame?.Dispose();

                if (ownsConversion)
                {
                    _ = Interlocked.Exchange(ref _conversionPending, 0);
                }
            }
        }

        private static async Task<CapturedFrame> CreateCapturedFrameAsync(Direct3D11CaptureFrame frame)
        {
            using var sourceBitmap = await SoftwareBitmap
                .CreateCopyFromSurfaceAsync(frame.Surface, BitmapAlphaMode.Ignore)
                .AsTask()
                .ConfigureAwait(false);

            SoftwareBitmap? convertedBitmap = null;
            try
            {
                var bitmap = sourceBitmap;
                if (bitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8
                    || bitmap.BitmapAlphaMode != BitmapAlphaMode.Ignore)
                {
                    convertedBitmap = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
                    bitmap = convertedBitmap;
                }

                var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
                bitmap.CopyToBuffer(pixels.AsBuffer());
                ForceOpaqueAlpha(pixels);
                return new CapturedFrame(bitmap.PixelWidth, bitmap.PixelHeight, pixels);
            }
            finally
            {
                convertedBitmap?.Dispose();
            }
        }

        private static void ForceOpaqueAlpha(byte[] pixels)
        {
            for (var i = 3; i < pixels.Length; i += 4)
            {
                pixels[i] = 255;
            }
        }

        private static void TryDisableCaptureBorder(GraphicsCaptureSession session)
        {
            try
            {
                if (!IsBorderlessCaptureApiPresent())
                {
                    return;
                }

                var accessStatus = GetBorderlessAccessStatus();
                if (accessStatus == AppCapabilityAccessStatus.Allowed)
                {
                    session.IsBorderRequired = false;
                }
            }
            catch
            {
            }
        }

        private static bool IsBorderlessCaptureApiPresent()
        {
            return ApiInformation.IsTypePresent("Windows.Graphics.Capture.GraphicsCaptureAccess")
                && ApiInformation.IsEnumNamedValuePresent(
                    "Windows.Graphics.Capture.GraphicsCaptureAccessKind",
                    nameof(GraphicsCaptureAccessKind.Borderless))
                && ApiInformation.IsPropertyPresent(
                    "Windows.Graphics.Capture.GraphicsCaptureSession",
                    nameof(GraphicsCaptureSession.IsBorderRequired));
        }

        private static AppCapabilityAccessStatus GetBorderlessAccessStatus()
        {
            lock (BorderlessAccessLock)
            {
                if (_borderlessAccessStatus.HasValue)
                {
                    return _borderlessAccessStatus.Value;
                }

                _borderlessAccessStatus = GraphicsCaptureAccess
                    .RequestAccessAsync(GraphicsCaptureAccessKind.Borderless)
                    .AsTask()
                    .GetAwaiter()
                    .GetResult();

                return _borderlessAccessStatus.Value;
            }
        }
    }
}
