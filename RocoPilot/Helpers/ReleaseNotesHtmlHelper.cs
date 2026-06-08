using System.Net;

using Microsoft.UI.Xaml;

using Markdig;

namespace RocoPilot.Helpers;

internal static class ReleaseNotesHtmlHelper
{
    private static readonly MarkdownPipeline MarkdownPipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    public static bool ResolveIsDarkTheme(FrameworkElement element)
    {
        return element.ActualTheme switch
        {
            ElementTheme.Dark => true,
            ElementTheme.Light => false,
            _ => Application.Current.RequestedTheme == ApplicationTheme.Dark,
        };
    }

    public static string GenerateReleaseNotesHtml(string markdown, bool isDarkTheme)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return GenerateFallbackHtml(isDarkTheme);
        }

        var normalized = NormalizeMarkdown(markdown);
        var innerHtml = Markdown.ToHtml(normalized, MarkdownPipeline);
        return WrapHtml(isDarkTheme, $"<div class='markdown-body'>{innerHtml}</div>", "更新日志");
    }

    public static string GenerateFallbackHtml(bool isDarkTheme) =>
        WrapHtml(
            isDarkTheme,
            "<div class='message'>此版本没有提供更新日志。</div>",
            "更新日志",
            centered: true);

    private static string NormalizeMarkdown(string markdown) =>
        markdown
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();

    private static string WrapHtml(bool isDarkTheme, string bodyInner, string pageTitle, bool centered = false)
    {
        var foreground = isDarkTheme ? "#ffffff" : "#1f1f1f";
        var background = isDarkTheme ? "#2d2d30" : "#ffffff";
        var secondary = isDarkTheme ? "#cccccc" : "#605e5c";
        var accent = isDarkTheme ? "#4fc3f7" : "#0078d4";
        var emphasis = isDarkTheme ? "#81c784" : "#107c10";
        var codeBackground = isDarkTheme ? "#3c3c3c" : "#f3f2f1";
        var codeForeground = isDarkTheme ? "#d4d4d4" : "#323130";
        var divider = isDarkTheme ? "#484848" : "#edebe9";
        var scrollbarTrack = isDarkTheme ? "#2d2d30" : "#f1f1f1";
        var scrollbarThumb = isDarkTheme ? "#484848" : "#c1c1c1";
        var scrollbarThumbHover = isDarkTheme ? "#5a5a5a" : "#a8a8a8";
        var link = isDarkTheme ? "#58a6ff" : "#0969da";
        var borderMuted = isDarkTheme ? "#444444" : "#d0d7de";
        var centeredStyle = centered ? $"text-align: center; padding-top: 50px; color: {secondary};" : string.Empty;
        var titleSafe = WebUtility.HtmlEncode(pageTitle);

        return $@"<!DOCTYPE html>
<html lang='zh-CN'>
<head>
    <meta charset='UTF-8'>
    <meta name='viewport' content='width=device-width, initial-scale=1.0'>
    <meta http-equiv='Content-Security-Policy' content=""default-src 'none'; img-src https: data:; style-src 'unsafe-inline';"">
    <title>{titleSafe}</title>
    <style>
        body {{
            font-family: 'Segoe UI', 'Microsoft YaHei', Tahoma, Geneva, Verdana, sans-serif;
            line-height: 1.6;
            color: {foreground};
            margin: 12px;
            padding: 8px;
            background-color: {background};
            font-size: 14px;
            {centeredStyle}
        }}
        a {{ color: {link}; }}
        .markdown-body a {{ word-break: break-all; }}
        .markdown-body img {{ max-width: 100%; height: auto; }}
        .markdown-body table {{
            border-collapse: collapse;
            width: 100%;
            margin: 12px 0;
            font-size: 13px;
        }}
        .markdown-body table th,
        .markdown-body table td {{
            border: 1px solid {borderMuted};
            padding: 6px 10px;
        }}
        .markdown-body table tr:nth-child(2n) {{
            background-color: {(isDarkTheme ? "#252526" : "#f6f8fa")};
        }}
        h1, h2, h3, h4, h5, h6 {{
            color: {foreground};
            margin-top: 16px;
            margin-bottom: 8px;
            font-weight: 600;
        }}
        h1 {{ font-size: 20px; }}
        h2 {{ font-size: 18px; }}
        h3 {{ font-size: 16px; }}
        h4 {{ font-size: 15px; }}
        p {{ margin-bottom: 10px; margin-top: 0; }}
        .markdown-body ul, .markdown-body ol {{
            padding-left: 24px;
            margin-top: 8px;
            margin-bottom: 12px;
        }}
        .markdown-body li {{ margin-bottom: 4px; }}
        .markdown-body li > p {{ margin-bottom: 4px; }}
        strong {{ color: {accent}; font-weight: 600; }}
        em {{ color: {emphasis}; font-style: italic; }}
        code {{
            background-color: {codeBackground};
            color: {codeForeground};
            padding: 2px 4px;
            border-radius: 3px;
            font-family: 'Consolas', 'Courier New', monospace;
            font-size: 13px;
        }}
        pre {{
            background-color: {codeBackground};
            color: {codeForeground};
            padding: 12px;
            border-radius: 6px;
            overflow-x: auto;
            font-family: 'Consolas', 'Courier New', monospace;
            font-size: 13px;
            line-height: 1.45;
        }}
        pre code {{
            background: none;
            padding: 0;
            font-size: inherit;
        }}
        blockquote {{
            border-left: 3px solid {accent};
            margin: 12px 0;
            padding-left: 12px;
            color: {secondary};
        }}
        details {{
            margin: 12px 0;
        }}
        summary {{
            cursor: pointer;
            font-weight: 600;
        }}
        hr {{
            border: none;
            border-top: 1px solid {divider};
            margin: 16px 0;
        }}
        .message {{ font-size: 16px; color: {secondary}; }}
        .plain-fallback {{
            white-space: pre-wrap;
            word-wrap: break-word;
            font-family: inherit;
            margin: 0;
            background: transparent;
            color: {foreground};
        }}
        ::-webkit-scrollbar {{ width: 8px; }}
        ::-webkit-scrollbar-track {{ background: {scrollbarTrack}; }}
        ::-webkit-scrollbar-thumb {{ background: {scrollbarThumb}; border-radius: 4px; }}
        ::-webkit-scrollbar-thumb:hover {{ background: {scrollbarThumbHover}; }}
    </style>
</head>
<body>
{bodyInner}
</body>
</html>
";
    }
}
