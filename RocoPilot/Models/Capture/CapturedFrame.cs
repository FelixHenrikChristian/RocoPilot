using System.Buffers;

namespace RocoPilot.Models.Capture;

public sealed class CapturedFrame : IDisposable
{
    private readonly FrameBufferLease? _bufferLease;
    private int _disposed;

    public CapturedFrame(int width, int height, byte[] pixels)
        : this(width, height, pixels, checked(width * height * 4), DateTimeOffset.Now, null)
    {
    }

    private CapturedFrame(
        int width,
        int height,
        byte[] pixels,
        int pixelByteLength,
        DateTimeOffset capturedAt,
        FrameBufferLease? bufferLease)
    {
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Frame width must be greater than 0.");
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height), "Frame height must be greater than 0.");
        }

        ArgumentNullException.ThrowIfNull(pixels);
        if (pixelByteLength <= 0 || pixels.Length < pixelByteLength)
        {
            throw new ArgumentException("Pixel buffer is too small for the frame dimensions.", nameof(pixels));
        }

        Width = width;
        Height = height;
        Pixels = pixels;
        PixelByteLength = pixelByteLength;
        CapturedAt = capturedAt;
        _bufferLease = bufferLease;
    }

    public int Width
    {
        get;
    }

    public int Height
    {
        get;
    }

    public byte[] Pixels
    {
        get;
    }

    public int PixelByteLength
    {
        get;
    }

    public DateTimeOffset CapturedAt
    {
        get;
    }

    public static CapturedFrame RentBgra32(int width, int height)
    {
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Frame width must be greater than 0.");
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height), "Frame height must be greater than 0.");
        }

        var pixelByteLength = checked(width * height * 4);
        var pixels = ArrayPool<byte>.Shared.Rent(pixelByteLength);
        var bufferLease = new FrameBufferLease(pixels);
        return new CapturedFrame(width, height, pixels, pixelByteLength, DateTimeOffset.Now, bufferLease);
    }

    internal CapturedFrame AddReference()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _bufferLease?.AddReference();
        return new CapturedFrame(Width, Height, Pixels, PixelByteLength, CapturedAt, _bufferLease);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _bufferLease?.Release();
    }

    private sealed class FrameBufferLease
    {
        private readonly byte[] _pixels;
        private int _referenceCount = 1;

        public FrameBufferLease(byte[] pixels)
        {
            _pixels = pixels;
        }

        public void AddReference()
        {
            var referenceCount = Volatile.Read(ref _referenceCount);
            while (referenceCount > 0)
            {
                var nextReferenceCount = referenceCount + 1;
                var originalReferenceCount = Interlocked.CompareExchange(
                    ref _referenceCount,
                    nextReferenceCount,
                    referenceCount);
                if (originalReferenceCount == referenceCount)
                {
                    return;
                }

                referenceCount = originalReferenceCount;
            }

            throw new ObjectDisposedException(nameof(CapturedFrame));
        }

        public void Release()
        {
            var referenceCount = Interlocked.Decrement(ref _referenceCount);
            if (referenceCount == 0)
            {
                ArrayPool<byte>.Shared.Return(_pixels);
                return;
            }

            if (referenceCount < 0)
            {
                throw new ObjectDisposedException(nameof(CapturedFrame));
            }
        }
    }
}
