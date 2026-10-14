using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using CodeBrix.WebGpu.BindingGenerator.Rendering;

namespace CodeBrix.WebGpu.BindingGenerator.Tests;

/// <summary>
/// A deliberately small C scanner for webgpu.h / wgpu.h: function declarations (name + parameter count)
/// and <c>typedef struct</c> definitions (name + member names, a nested union counting as one member).
/// </summary>
internal static class CHeaderScanner
{
    internal sealed class Header
    {
        public Dictionary<string, int> Functions { get; } = new Dictionary<string, int>(StringComparer.Ordinal);

        public Dictionary<string, List<string>> Structs { get; } = new Dictionary<string, List<string>>(StringComparer.Ordinal);
    }

    private static readonly Regex Comments = new Regex(@"/\*.*?\*/|//[^\n]*", RegexOptions.Singleline);
    private static readonly Regex Preprocessor = new Regex(@"^\s*#.*?(?<!\\)$", RegexOptions.Multiline | RegexOptions.Singleline);
    private static readonly Regex Function = new Regex(@"\b(wgpu[A-Z]\w*)\s*\(([^()]*)\)\s*(WGPU_FUNCTION_ATTRIBUTE)?\s*;");
    private static readonly Regex StructStart = new Regex(@"typedef\s+struct\s+(WGPU\w+)\s*\{");

    public static Header Scan(params string[] sources)
    {
        Header header = new Header();
        foreach (string source in sources)
        {
            string text = StripPreprocessor(Comments.Replace(source, " "));
            foreach (Match match in Function.Matches(text))
            {
                string parameters = match.Groups[2].Value.Trim();
                int count = parameters.Length == 0 || parameters == "void" ? 0 : parameters.Split(',').Length;
                header.Functions[match.Groups[1].Value] = count;
            }

            foreach (Match match in StructStart.Matches(text))
            {
                int open = match.Index + match.Length - 1;
                int close = MatchingBrace(text, open);
                header.Structs[match.Groups[1].Value] = Members(text.Substring(open + 1, close - open - 1));
            }
        }

        return header;
    }

    private static readonly Regex Declaration = new Regex(@"([A-Za-z_][\w\s\*]*?)\b(wgpu[A-Z]\w*)\s*\(([^()]*)\)\s*(?:WGPU_FUNCTION_ATTRIBUTE)?\s*;");

    /// <summary>Every function declaration of a header as <see cref="TypesOnly" /> text, by name.</summary>
    public static Dictionary<string, string> Declarations(string source)
    {
        Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.Ordinal);
        string text = StripPreprocessor(Comments.Replace(source, " "));
        foreach (Match match in Declaration.Matches(text))
        {
            string returns = match.Groups[1].Value.Replace("WGPU_EXPORT", " ");
            result[match.Groups[2].Value] = TypesOnly(returns + " " + match.Groups[2].Value + "(" + match.Groups[3].Value + ")");
        }

        return result;
    }

    /// <summary>A declaration <c>ret name(type a, type b)</c> reduced to normalized <c>ret name(type, type)</c> (parameter names dropped).</summary>
    public static string TypesOnly(string declaration)
    {
        int open = declaration.IndexOf('(');
        string head = CodebrixHeaderCheck.Normalize(declaration.Substring(0, open));
        string parameters = declaration.Substring(open + 1, declaration.LastIndexOf(')') - open - 1);
        List<string> types = new List<string>();
        foreach (string parameter in parameters.Split(','))
        {
            string normalized = CodebrixHeaderCheck.Normalize(parameter);
            if (normalized.Length == 0 || normalized == "void") continue;
            types.Add(Regex.Replace(normalized, @"\s*\b\w+$", string.Empty).Trim());
        }

        return head + "(" + string.Join(", ", types) + ")";
    }

    // Drops #define / #if lines (including continued #define bodies, which hold the INIT macros).
    private static string StripPreprocessor(string text)
    {
        List<string> kept = new List<string>();
        bool continuing = false;
        foreach (string line in text.Split('\n'))
        {
            bool directive = continuing || line.TrimStart().StartsWith("#", StringComparison.Ordinal);
            continuing = directive && line.TrimEnd().EndsWith("\\", StringComparison.Ordinal);
            kept.Add(directive ? string.Empty : line);
        }

        return string.Join("\n", kept);
    }

    private static List<string> Members(string body)
    {
        List<string> members = new List<string>();
        int depth = 0;
        int start = 0;
        for (int index = 0; index < body.Length; index++)
        {
            char c = body[index];
            if (c == '{') depth++;
            else if (c == '}') depth--;
            else if (c == ';' && depth == 0)
            {
                string declaration = body.Substring(start, index - start).Trim();
                Match name = Regex.Match(declaration, @"(\w+)\s*$");
                if (name.Success) members.Add(name.Groups[1].Value);
                start = index + 1;
            }
        }

        return members;
    }

    private static int MatchingBrace(string text, int open)
    {
        int depth = 0;
        for (int index = open; index < text.Length; index++)
        {
            if (text[index] == '{') depth++;
            else if (text[index] == '}' && --depth == 0) return index;
        }

        throw new InvalidOperationException("Unbalanced braces after position " + open);
    }
}
