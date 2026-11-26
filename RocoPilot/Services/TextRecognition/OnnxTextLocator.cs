using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using RocoPilot.Contracts.Services.TextRecognition;
using RocoPilot.Models.Capture;
using RocoPilot.Models.Recognition;
using RocoPilot.Models.TextRecognition;
using RocoPilot.Services.Recognition;

namespace RocoPilot.Services.TextRecognition;

// 全客户区检测文字，再复用现有单行 OCR；坐标始终返回到原捕获帧。
public sealed class OnnxTextLocator(ITextRecognitionService ocr) : IDisposable
{
    private readonly Lazy<InferenceSession> _detector = new(() =>
    {
        using var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        return new InferenceSession(Path.Combine(AppContext.BaseDirectory, "Assets", "Models", "OCR", "Onnx",
            "PaddleOcrV5", "detection.onnx"), options);
    });

    public async Task<RecognitionRegion?> FindAsync(CapturedFrame frame, CaptureTargetWindow window,
        string text, CancellationToken token)
    {
        var regions = await Task.Run(() => Detect(frame, window, token), token);
        RecognitionRegion? found = null;
        foreach (var region in regions)
        {
            token.ThrowIfCancellationRequested();
            var result = await ocr.RecognizeAsync(frame, region, TextRecognitionMethod.OnnxOcr, token);
            if (Regex.Replace(result.Text, @"\s", "") != text) continue;
            if (found is not null) return null; // 多个同名文字时不猜点击目标。
            found = region;
        }
        return found;
    }

    private RecognitionRegion[] Detect(CapturedFrame frame, CaptureTargetWindow window, CancellationToken token)
    {
        RecognitionRegionImageHelper.TryGetClientAreaInCapturedFrame(frame, window, out var x, out var y, out var width, out var height);
        var handle = GCHandle.Alloc(frame.Pixels, GCHandleType.Pinned);
        try
        {
            using var source = Mat.FromPixelData(frame.Height, frame.Width, MatType.CV_8UC4, handle.AddrOfPinnedObject());
            using var client = new Mat(source, new Rect(x, y, width, height));
            using var bgr = new Mat();
            Cv2.CvtColor(client, bgr, ColorConversionCodes.BGRA2BGR);
            var scale = Math.Min(1d, 960d / Math.Max(width, height));
            using var resized = new Mat();
            Cv2.Resize(bgr, resized, new Size((int)Math.Round(width * scale), (int)Math.Round(height * scale)));
            using var padded = new Mat();
            Cv2.CopyMakeBorder(resized, padded, 0, (32 - resized.Height % 32) % 32,
                0, (32 - resized.Width % 32) % 32, BorderTypes.Constant, Scalar.Black);
            var pixels = new byte[padded.Width * padded.Height * 3];
            Marshal.Copy(padded.Data, pixels, 0, pixels.Length);
            var plane = padded.Width * padded.Height;
            var input = new float[plane * 3];
            float[] means = [.485f, .456f, .406f], deviations = [.229f, .224f, .225f];
            for (var c = 0; c < 3; c++)
                for (var i = 0; i < plane; i++)
                    input[c * plane + i] = (pixels[i * 3 + c] / 255f - means[c]) / deviations[c];
            token.ThrowIfCancellationRequested();
            var session = _detector.Value;
            using var outputs = session.Run([NamedOnnxValue.CreateFromTensor(session.InputNames[0],
                new DenseTensor<float>(input, [1, 3, padded.Height, padded.Width]))]);
            var tensor = outputs.First().AsTensor<float>();
            using var probability = new Mat(tensor.Dimensions[2], tensor.Dimensions[3], MatType.CV_32FC1);
            Marshal.Copy(tensor.ToArray(), 0, probability.Data, (int)probability.Total());
            using var valid = new Mat(probability, new Rect(0, 0, resized.Width, resized.Height));
            using var binary = new Mat();
            Cv2.Threshold(valid, binary, .3, 255, ThresholdTypes.Binary);
            binary.ConvertTo(binary, MatType.CV_8UC1);
            Cv2.FindContours(binary, out Point[][] contours, out _, RetrievalModes.List, ContourApproximationModes.ApproxSimple);
            var regions = new List<RecognitionRegion>();
            foreach (var contour in contours)
            {
                token.ThrowIfCancellationRequested();
                var rect = Cv2.BoundingRect(contour);
                using var mask = new Mat(rect.Size, MatType.CV_8UC1, Scalar.Black);
                Cv2.FillPoly(mask, [contour.Select(p => new Point(p.X - rect.X, p.Y - rect.Y)).ToArray()], Scalar.White);
                using var scores = new Mat(valid, rect);
                if (Cv2.Mean(scores, mask).Val0 < .7) continue;
                var perimeter = Cv2.ArcLength(contour, true);
                if (perimeter <= 0) continue;
                var rotated = Cv2.MinAreaRect(contour);
                var margin = (float)(1.5 * Math.Abs(Cv2.ContourArea(contour)) / perimeter);
                var expanded = new RotatedRect(rotated.Center,
                    new Size2f(rotated.Size.Width + 2 * margin, rotated.Size.Height + 2 * margin), rotated.Angle).BoundingRect();
                var left = Math.Clamp((int)Math.Floor(expanded.Left * width / (double)resized.Width), 0, width);
                var top = Math.Clamp((int)Math.Floor(expanded.Top * height / (double)resized.Height), 0, height);
                var right = Math.Clamp((int)Math.Ceiling(expanded.Right * width / (double)resized.Width), 0, width);
                var bottom = Math.Clamp((int)Math.Ceiling(expanded.Bottom * height / (double)resized.Height), 0, height);
                if (right - left < 3 || bottom - top < 3) continue;
                regions.Add(new() { X = x + left, Y = y + top, Width = right - left, Height = bottom - top });
            }
            return regions.ToArray();
        }
        finally { handle.Free(); }
    }

    public void Dispose()
    {
        if (_detector.IsValueCreated) _detector.Value.Dispose();
    }
}
