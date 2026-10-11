using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace CodeBrix.WebGpu.BindingGenerator.Yaml;

/// <summary>The kind of a <see cref="SpecNode" />, after YAML 1.2 core-schema scalar resolution.</summary>
public enum SpecNodeKind
{
    /// <summary>A mapping.</summary>
    Mapping,

    /// <summary>A sequence.</summary>
    Sequence,

    /// <summary>A plain <c>null</c>, <c>~</c> or empty scalar.</summary>
    Null,

    /// <summary>A plain <c>true</c> / <c>false</c> scalar.</summary>
    Boolean,

    /// <summary>A plain integer scalar (decimal, <c>0x</c> hexadecimal or <c>0o</c> octal).</summary>
    Integer,

    /// <summary>A plain floating-point scalar.</summary>
    Number,

    /// <summary>Any other scalar, including every quoted or block scalar.</summary>
    String,
}

/// <summary>
/// A node of a loaded specification file, independent of the YAML library: a mapping (ordered), a
/// sequence, or a typed scalar. Every node knows its path (<c>$.structs[3].members[0].type</c>) so that
/// validation and model errors can name the offending node.
/// </summary>
public sealed class SpecNode
{
    private static readonly Regex DecimalInteger = new Regex("^[-+]?[0-9]+$", RegexOptions.CultureInvariant);
    private static readonly Regex HexInteger = new Regex("^0x[0-9a-fA-F]+$", RegexOptions.CultureInvariant);
    private static readonly Regex OctalInteger = new Regex("^0o[0-7]+$", RegexOptions.CultureInvariant);
    private static readonly Regex Float = new Regex(
        @"^[-+]?(\.[0-9]+|[0-9]+(\.[0-9]*)?)([eE][-+]?[0-9]+)?$|^[-+]?\.(inf|Inf|INF)$|^\.(nan|NaN|NAN)$",
        RegexOptions.CultureInvariant);

    private SpecNode(SpecNodeKind kind, string path, string text)
    {
        Kind = kind;
        Path = path;
        Text = text;
    }

    /// <summary>The node kind.</summary>
    public SpecNodeKind Kind { get; }

    /// <summary>The path of the node from the document root, JSONPath style.</summary>
    public string Path { get; }

    /// <summary>The scalar text (for scalar kinds); null for mappings and sequences.</summary>
    public string Text { get; }

    /// <summary>The entries of a mapping, in document order.</summary>
    public IReadOnlyList<KeyValuePair<string, SpecNode>> Entries { get; private set; } =
        Array.Empty<KeyValuePair<string, SpecNode>>();

    /// <summary>The items of a sequence, in document order.</summary>
    public IReadOnlyList<SpecNode> Items { get; private set; } = Array.Empty<SpecNode>();

    /// <summary>True for every scalar kind.</summary>
    public bool IsScalar => Kind != SpecNodeKind.Mapping && Kind != SpecNodeKind.Sequence;

    /// <summary>Creates a mapping node.</summary>
    /// <param name="path">The node path.</param>
    /// <param name="entries">The entries, in document order.</param>
    /// <returns>The node.</returns>
    public static SpecNode Mapping(string path, IReadOnlyList<KeyValuePair<string, SpecNode>> entries) =>
        new SpecNode(SpecNodeKind.Mapping, path, null) { Entries = entries };

    /// <summary>Creates a sequence node.</summary>
    /// <param name="path">The node path.</param>
    /// <param name="items">The items, in document order.</param>
    /// <returns>The node.</returns>
    public static SpecNode Sequence(string path, IReadOnlyList<SpecNode> items) =>
        new SpecNode(SpecNodeKind.Sequence, path, null) { Items = items };

    /// <summary>Creates a scalar node, resolving its type as the YAML 1.2 core schema does.</summary>
    /// <param name="path">The node path.</param>
    /// <param name="text">The scalar text.</param>
    /// <param name="plain">True for a plain (unquoted, non-block) scalar; only plain scalars are typed.</param>
    /// <returns>The node.</returns>
    public static SpecNode Scalar(string path, string text, bool plain)
    {
        text ??= string.Empty;
        if (!plain) return new SpecNode(SpecNodeKind.String, path, text);
        if (text.Length == 0 || text == "~" || text == "null" || text == "Null" || text == "NULL")
        {
            return new SpecNode(SpecNodeKind.Null, path, text);
        }

        if (text is "true" or "True" or "TRUE" or "false" or "False" or "FALSE")
        {
            return new SpecNode(SpecNodeKind.Boolean, path, text);
        }

        if (DecimalInteger.IsMatch(text) || HexInteger.IsMatch(text) || OctalInteger.IsMatch(text))
        {
            return new SpecNode(SpecNodeKind.Integer, path, text);
        }

        if (Float.IsMatch(text)) return new SpecNode(SpecNodeKind.Number, path, text);
        return new SpecNode(SpecNodeKind.String, path, text);
    }

    /// <summary>Looks up a mapping entry by key.</summary>
    /// <param name="key">The key.</param>
    /// <returns>The value node, or null when the key is absent (or this is not a mapping).</returns>
    public SpecNode this[string key]
    {
        get
        {
            foreach (KeyValuePair<string, SpecNode> entry in Entries)
            {
                if (string.Equals(entry.Key, key, StringComparison.Ordinal)) return entry.Value;
            }

            return null;
        }
    }

    /// <summary>The value of an integer scalar.</summary>
    /// <returns>The value; hexadecimal and octal forms are accepted.</returns>
    /// <exception cref="SpecException">The node is not an integer scalar or does not fit 64 bits.</exception>
    public ulong AsUInt64()
    {
        if (Kind != SpecNodeKind.Integer) throw new SpecException(Path, "expected an integer, found " + Describe());
        try
        {
            if (Text.StartsWith("0x", StringComparison.Ordinal))
            {
                return ulong.Parse(Text.Substring(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
            }

            if (Text.StartsWith("0o", StringComparison.Ordinal)) return Convert.ToUInt64(Text.Substring(2), 8);
            return ulong.Parse(Text.TrimStart('+'), NumberStyles.None, CultureInfo.InvariantCulture);
        }
        catch (Exception exception) when (exception is FormatException or OverflowException)
        {
            throw new SpecException(Path, "integer '" + Text + "' does not fit an unsigned 64-bit value");
        }
    }

    /// <summary>A short description of the node for error messages.</summary>
    /// <returns>The description.</returns>
    public string Describe() =>
        Kind switch
        {
            SpecNodeKind.Mapping => "a mapping",
            SpecNodeKind.Sequence => "a sequence",
            SpecNodeKind.Null => "null",
            _ => Kind.ToString().ToLowerInvariant() + " '" + Text + "'",
        };
}
