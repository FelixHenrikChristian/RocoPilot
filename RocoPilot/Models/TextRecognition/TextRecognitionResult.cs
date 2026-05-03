namespace RocoPilot.Models.TextRecognition;

public sealed class TextRecognitionResult
{
    public TextRecognitionResult(
        TextRecognitionMethod method,
        string methodName,
        string? languageName,
        IReadOnlyList<string> lines,
        int wordCount)
    {
        Method = method;
        MethodName = methodName;
        LanguageName = languageName;
        Lines = lines;
        WordCount = wordCount;
        Text = string.Join(Environment.NewLine, lines);
    }

    public TextRecognitionMethod Method
    {
        get;
    }

    public string MethodName
    {
        get;
    }

    public string? LanguageName
    {
        get;
    }

    public IReadOnlyList<string> Lines
    {
        get;
    }

    public int WordCount
    {
        get;
    }

    public string Text
    {
        get;
    }
}
