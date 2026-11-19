using System.Text;
using System.Text.RegularExpressions;

namespace MDViewer.Services;

/// <summary>
/// Quotes erDiagram identifiers with non-ASCII characters (e.g. Cyrillic).
/// </summary>
public static partial class MermaidErNormalizer
{
    [GeneratedRegex(@"```mermaid\s*\r?\n(.*?)```", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex MermaidFenceRegex();

    public static string NormalizeMarkdown(string markdown) =>
        MermaidFenceRegex().Replace(markdown, match =>
        {
            var body = match.Groups[1].Value;
            if (!body.Contains("erDiagram", StringComparison.OrdinalIgnoreCase))
            {
                return match.Value;
            }

            return "```mermaid\n" + NormalizeErDiagram(body).TrimEnd() + "\n```";
        });

    public static string NormalizeErDiagram(string source)
    {
        var lines = source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var sb = new StringBuilder(source.Length + 32);
        var inEr = false;

        foreach (var raw in lines)
        {
            var trimmed = raw.TrimStart();
            if (trimmed.StartsWith("erDiagram", StringComparison.OrdinalIgnoreCase))
            {
                inEr = true;
                sb.AppendLine(raw);
                continue;
            }

            sb.AppendLine(inEr ? QuoteIdentifiersOnLine(raw) : raw);
        }

        return sb.ToString();
    }

    private static string QuoteIdentifiersOnLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("%%", StringComparison.Ordinal))
        {
            return line;
        }

        return QuoteTokens(line);
    }

    private static string QuoteTokens(string line)
    {
        var sb = new StringBuilder(line.Length + 16);
        var i = 0;
        while (i < line.Length)
        {
            var c = line[i];
            if (c is '"')
            {
                var end = line.IndexOf('"', i + 1);
                if (end < 0)
                {
                    sb.Append(line.AsSpan(i));
                    break;
                }

                sb.Append(line.AsSpan(i, end - i + 1));
                i = end + 1;
                continue;
            }

            if (IsIdentStart(c))
            {
                var start = i;
                i++;
                while (i < line.Length && IsIdentPart(line[i]))
                {
                    i++;
                }

                var token = line[start..i];
                if (NeedsQuotes(token))
                {
                    sb.Append('"').Append(token).Append('"');
                }
                else
                {
                    sb.Append(token);
                }

                continue;
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }

    private static bool NeedsQuotes(string token)
    {
        foreach (var c in token)
        {
            if (c > 127)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsIdentStart(char c) =>
        char.IsLetter(c) || c is '_' or '#';

    private static bool IsIdentPart(char c) =>
        char.IsLetterOrDigit(c) || c is '_' or '-' or '#';
}
