using System.Text;

namespace RocoPilot.Services.TextRecognition.Backends;

internal static class OnnxOcrV5TextDecoder
{
    public static string Decode(
        IReadOnlyList<string> labels,
        ReadOnlySpan<float> scores,
        int timeStepCount,
        int labelCount)
    {
        if (timeStepCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(timeStepCount));
        }

        if (labelCount <= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(labelCount));
        }

        if (scores.Length != checked(timeStepCount * labelCount))
        {
            throw new ArgumentException("Output size does not match OCR dimensions.", nameof(scores));
        }

        var text = new StringBuilder();
        var previousIndex = 0;
        for (var timeStep = 0; timeStep < timeStepCount; timeStep++)
        {
            var maximumValue = float.MinValue;
            var maximumIndex = 0;
            var offset = timeStep * labelCount;
            for (var labelIndex = 0; labelIndex < labelCount; labelIndex++)
            {
                var value = scores[offset + labelIndex];
                if (value > maximumValue)
                {
                    maximumValue = value;
                    maximumIndex = labelIndex;
                }
            }

            if (maximumIndex > 0
                && maximumIndex != previousIndex
                && maximumIndex <= labels.Count)
            {
                text.Append(labels[maximumIndex - 1]);
            }

            previousIndex = maximumIndex;
        }

        return text.ToString();
    }
}
