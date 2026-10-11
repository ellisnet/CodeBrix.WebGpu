using System.Collections.Generic;
using System.Text;

namespace CodeBrix.WebGpu.BindingGenerator.Rendering;

/// <summary>
/// Normalizes rendered text so the templates can be written for readability: LF line endings, no
/// trailing whitespace, no runs of blank lines, no blank line just inside a brace pair, exactly one final
/// newline. Deterministic: the same input always gives the same bytes.
/// </summary>
public static class OutputNormalizer
{
    /// <summary>Normalizes text.</summary>
    /// <param name="text">The rendered text.</param>
    /// <returns>The normalized text.</returns>
    public static string Normalize(string text)
    {
        string[] raw = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        List<string> lines = new List<string>(raw.Length);
        foreach (string line in raw)
        {
            string trimmed = line.TrimEnd();
            bool blank = trimmed.Length == 0;
            if (blank)
            {
                if (lines.Count == 0) continue;
                string previous = lines[lines.Count - 1];
                if (previous.Length == 0 || previous.TrimEnd().EndsWith("{") || previous.TrimEnd().EndsWith("[")) continue;
            }
            else if (IsCloser(trimmed) && lines.Count > 0 && lines[lines.Count - 1].Length == 0)
            {
                lines.RemoveAt(lines.Count - 1);
            }

            lines.Add(trimmed);
        }

        while (lines.Count > 0 && lines[lines.Count - 1].Length == 0) lines.RemoveAt(lines.Count - 1);

        StringBuilder builder = new StringBuilder();
        foreach (string line in lines) builder.Append(line).Append('\n');
        return builder.ToString();
    }

    private static bool IsCloser(string line)
    {
        string trimmed = line.TrimStart();
        return trimmed.StartsWith("}") || trimmed.StartsWith("];");
    }
}
