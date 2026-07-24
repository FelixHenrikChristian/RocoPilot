namespace RocoPilot.Services.TextRecognition.Backends;

internal static class OnnxOcrV5LabelDictionary
{
    public static IReadOnlyList<string> Load(string configurationPath)
    {
        var labels = new List<string>();
        var readingCharacterDictionary = false;
        foreach (var line in File.ReadLines(configurationPath))
        {
            if (line.Trim() == "character_dict:")
            {
                readingCharacterDictionary = true;
                continue;
            }

            if (!readingCharacterDictionary)
            {
                continue;
            }

            if (!line.StartsWith("  - ", StringComparison.Ordinal))
            {
                break;
            }

            labels.Add(ParseYamlScalar(line[4..]));
        }

        if (labels.Count == 0)
        {
            throw new InvalidDataException("The ONNX OCR label dictionary is empty.");
        }

        return labels;
    }

    private static string ParseYamlScalar(string value)
    {
        if (value.Length >= 2
            && value[0] == '\''
            && value[^1] == '\'')
        {
            return value[1..^1].Replace("''", "'", StringComparison.Ordinal);
        }

        return value;
    }
}
