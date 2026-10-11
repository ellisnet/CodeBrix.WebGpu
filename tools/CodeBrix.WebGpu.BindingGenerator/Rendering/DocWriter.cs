using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using CodeBrix.WebGpu.BindingGenerator.Model;

namespace CodeBrix.WebGpu.BindingGenerator.Rendering;

/// <summary>
/// Turns YAML doc text into XML documentation comment lines: <c>@ref X</c> / <c>\ref X</c> become
/// <c>&lt;see cref&gt;</c> where X is a generated symbol (otherwise plain text), markdown code spans become
/// <c>&lt;c&gt;</c>, markdown links and emphasis are reduced to their text, XML special characters are
/// escaped, blank-line paragraphs become <c>&lt;para&gt;</c>, and empty or "TODO" docs are replaced by a
/// one-line summary naming the C symbol.
/// </summary>
public sealed class DocWriter
{
    private static readonly Regex AutoLink = new Regex(@"<(https?://[^>\s]+)>", RegexOptions.CultureInvariant);
    private static readonly Regex MarkdownLink = new Regex(@"\[([^\]]+)\]\(([^)]+)\)", RegexOptions.CultureInvariant);
    private static readonly Regex CodeSpan = new Regex("`([^`]+)`", RegexOptions.CultureInvariant);
    private static readonly Regex Reference = new Regex(@"[@\\]ref\s+([A-Za-z0-9_:\-]+)", RegexOptions.CultureInvariant);
    private static readonly Regex Bold = new Regex(@"\*\*([^*]+)\*\*", RegexOptions.CultureInvariant);
    private static readonly Regex Italic = new Regex(@"(?<![\w*])\*([^*\s][^*]*)\*(?![\w*])", RegexOptions.CultureInvariant);

    private readonly Dictionary<string, string> symbols = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Creates a writer whose cross references resolve against the model's generated symbols.</summary>
    /// <param name="model">The model.</param>
    public DocWriter(ApiModel model)
    {
        foreach (ApiEnum value in model.Enums)
        {
            symbols[value.CName] = value.CsName;
            foreach (ApiEnumValue entry in value.Values) symbols[entry.CName] = value.CsName + "." + entry.CsName;
        }

        foreach (ApiBitflag value in model.Bitflags)
        {
            symbols[value.CName] = value.CsName;
            foreach (ApiBitflagValue entry in value.Values) symbols[entry.CName] = value.CsName + "." + entry.CsName;
        }

        foreach (ApiStruct value in model.LayoutStructs)
        {
            symbols[value.CName] = value.CsName;
            foreach (ApiField field in value.Fields) symbols[value.CName + "::" + field.CName] = value.CsName + "." + field.CsName;
        }

        foreach (ApiCallback value in model.Callbacks.Where(c => c.Info != null)) symbols[value.CName] = value.Info.CsName;
        foreach (ApiHandle value in model.Handles) symbols[value.CName] = value.CsName;
        foreach (ApiFunction value in model.Functions) symbols[value.CName] = "WebGpu." + value.CsName;
        foreach (ApiConstant value in model.Constants) symbols[value.CName] = "Constants." + value.CsName;
    }

    /// <summary>True when a YAML doc carries no content (empty or the placeholder "TODO").</summary>
    /// <param name="doc">The doc text.</param>
    /// <returns>True when it is empty.</returns>
    public static bool IsEmpty(string doc) => string.IsNullOrWhiteSpace(doc) || doc.Trim() == "TODO";

    /// <summary>A summary block.</summary>
    /// <param name="doc">The YAML doc.</param>
    /// <param name="fallback">The XML text used when the doc is empty (already XML).</param>
    /// <param name="extra">Optional further paragraphs (already XML).</param>
    /// <returns>The <c>///</c> lines.</returns>
    public List<string> Summary(string doc, string fallback, params string[] extra)
    {
        List<string> lines = new List<string> { "/// <summary>" };
        List<string> paragraphs = IsEmpty(doc) ? new List<string> { fallback } : Paragraphs(doc);
        paragraphs.AddRange(extra.Where(e => !string.IsNullOrEmpty(e)));
        AppendParagraphs(lines, paragraphs);
        lines.Add("/// </summary>");
        return lines;
    }

    /// <summary>A one-line <c>remarks</c> element.</summary>
    /// <param name="xml">The remark (already XML).</param>
    /// <returns>The <c>///</c> lines.</returns>
    public static List<string> Remarks(string xml) => new List<string> { "/// <remarks>" + xml + "</remarks>" };

    /// <summary>A <c>param</c> element.</summary>
    /// <param name="name">The parameter name (without any <c>@</c> escape).</param>
    /// <param name="doc">The YAML doc.</param>
    /// <param name="fallback">The XML text used when the doc is empty.</param>
    /// <returns>The <c>///</c> lines.</returns>
    public List<string> Param(string name, string doc, string fallback) =>
        Element("param name=\"" + name + "\"", "param", doc, fallback);

    /// <summary>A <c>returns</c> element.</summary>
    /// <param name="doc">The YAML doc.</param>
    /// <param name="fallback">The XML text used when the doc is empty.</param>
    /// <returns>The <c>///</c> lines.</returns>
    public List<string> Returns(string doc, string fallback) => Element("returns", "returns", doc, fallback);

    /// <summary>Converts one YAML doc (no fallback) to XML paragraphs.</summary>
    /// <param name="doc">The doc text.</param>
    /// <returns>One XML string per paragraph (lines joined with newlines).</returns>
    public List<string> Paragraphs(string doc)
    {
        List<string> paragraphs = new List<string>();
        foreach (string block in Regex.Split(doc.Replace("\r\n", "\n").Trim(), @"\n[ \t]*\n"))
        {
            string text = string.Join("\n", block.Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0));
            if (text.Length > 0) paragraphs.Add(Convert(text));
        }

        return paragraphs;
    }

    /// <summary>Escapes plain text for XML.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The escaped text.</returns>
    public static string Escape(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private List<string> Element(string open, string close, string doc, string fallback)
    {
        List<string> paragraphs = IsEmpty(doc) ? new List<string> { fallback } : Paragraphs(doc);
        string joined = string.Join("\n", paragraphs);
        List<string> lines = new List<string>();
        string[] split = joined.Split('\n');
        if (split.Length == 1)
        {
            lines.Add("/// <" + open + ">" + split[0] + "</" + close + ">");
            return lines;
        }

        lines.Add("/// <" + open + ">");
        foreach (string line in split) lines.Add("/// " + line);
        lines.Add("/// </" + close + ">");
        return lines;
    }

    private static void AppendParagraphs(List<string> lines, List<string> paragraphs)
    {
        if (paragraphs.Count == 1)
        {
            foreach (string line in paragraphs[0].Split('\n')) lines.Add("/// " + line);
            return;
        }

        foreach (string paragraph in paragraphs)
        {
            lines.Add("/// <para>");
            foreach (string line in paragraph.Split('\n')) lines.Add("/// " + line);
            lines.Add("/// </para>");
        }
    }

    private string Convert(string text)
    {
        text = AutoLink.Replace(text, "$1");
        text = MarkdownLink.Replace(text, m => m.Groups[2].Value.StartsWith("http", StringComparison.Ordinal)
            ? m.Groups[1].Value + " (" + m.Groups[2].Value + ")"
            : m.Groups[1].Value);

        StringBuilder output = new StringBuilder();
        int position = 0;
        foreach (Match code in CodeSpan.Matches(text))
        {
            output.Append(ConvertProse(text.Substring(position, code.Index - position)));
            output.Append("<c>").Append(Escape(code.Groups[1].Value)).Append("</c>");
            position = code.Index + code.Length;
        }

        output.Append(ConvertProse(text.Substring(position)));
        return output.ToString();
    }

    private string ConvertProse(string text)
    {
        StringBuilder output = new StringBuilder();
        int position = 0;
        foreach (Match reference in Reference.Matches(text))
        {
            output.Append(Plain(text.Substring(position, reference.Index - position)));
            output.Append(Cref(reference.Groups[1].Value));
            position = reference.Index + reference.Length;
        }

        output.Append(Plain(text.Substring(position)));
        return output.ToString();
    }

    private static string Plain(string text)
    {
        text = Bold.Replace(text, "$1");
        text = Italic.Replace(text, "$1");
        return Escape(text);
    }

    /// <summary>A cross reference to a C symbol: a cref when it is generated, otherwise readable text.</summary>
    /// <param name="cSymbol">The C symbol (or a documentation page name).</param>
    /// <returns>The XML.</returns>
    public string Cref(string cSymbol)
    {
        if (symbols.TryGetValue(cSymbol, out string cref)) return "<see cref=\"" + cref + "\" />";
        if (cSymbol.StartsWith("WGPU", StringComparison.Ordinal) || cSymbol.StartsWith("wgpu", StringComparison.Ordinal))
        {
            return "<c>" + Escape(cSymbol) + "</c>";
        }

        return Escape(cSymbol.Replace('-', ' '));
    }
}
