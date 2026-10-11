using System;
using System.Collections.Generic;
using System.Text;

namespace CodeBrix.WebGpu.BindingGenerator.Model;

/// <summary>
/// Name conversions, character-for-character the same as the reference Go generator's (gen/utils.go):
/// PascalCase upper-cases the first character and every character that follows an underscore, drops
/// those underscores and keeps every other character's case; a double underscore therefore yields one
/// literal underscore.
/// </summary>
public static class Names
{
    private static readonly HashSet<string> Keywords = new HashSet<string>(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const",
        "continue", "decimal", "default", "delegate", "do", "double", "else", "enum", "event", "explicit",
        "extern", "false", "finally", "fixed", "float", "for", "foreach", "goto", "if", "implicit", "in", "int",
        "interface", "internal", "is", "lock", "long", "namespace", "new", "null", "object", "operator", "out",
        "override", "params", "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed",
        "short", "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true", "try",
        "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual", "void", "volatile",
        "while",
    };

    /// <summary>The reference generator's PascalCase.</summary>
    /// <param name="value">A snake_case name.</param>
    /// <returns>The PascalCase name.</returns>
    public static string Pascal(string value)
    {
        StringBuilder builder = new StringBuilder(value.Length);
        bool nextUpper = true;
        foreach (char c in value)
        {
            if (nextUpper)
            {
                builder.Append(char.ToUpperInvariant(c));
                nextUpper = false;
            }
            else if (c == '_')
            {
                nextUpper = true;
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    /// <summary>The reference generator's CamelCase (C member and parameter names).</summary>
    /// <param name="value">A snake_case name.</param>
    /// <returns>The camelCase name.</returns>
    public static string Camel(string value)
    {
        StringBuilder builder = new StringBuilder(value.Length);
        bool nextUpper = false;
        foreach (char c in value)
        {
            if (nextUpper)
            {
                builder.Append(char.ToUpperInvariant(c));
                nextUpper = false;
            }
            else if (c == '_')
            {
                nextUpper = true;
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    /// <summary>The reference generator's Singularize (array count member names).</summary>
    /// <param name="value">A plural snake_case name.</param>
    /// <returns>The singular form: "entries" becomes "entry", otherwise one trailing "s" is removed.</returns>
    public static string Singularize(string value) =>
        value == "entries" ? "entry" : value.EndsWith("s", StringComparison.Ordinal) ? value.Substring(0, value.Length - 1) : value;

    /// <summary>The reference generator's constant case (upper-cased snake_case).</summary>
    /// <param name="value">A snake_case name.</param>
    /// <returns>The CONSTANT_CASE name.</returns>
    public static string Constant(string value) => value.ToUpperInvariant();

    /// <summary>Escapes a C# keyword with <c>@</c>.</summary>
    /// <param name="identifier">The identifier.</param>
    /// <returns>The identifier, escaped when it is a keyword.</returns>
    public static string Escape(string identifier) => Keywords.Contains(identifier) ? "@" + identifier : identifier;

    /// <summary>The last capitalised word of a PascalCase name ("TextureViewDimension" gives "Dimension").</summary>
    /// <param name="pascal">The PascalCase name.</param>
    /// <returns>The last word.</returns>
    public static string LastWord(string pascal)
    {
        for (int index = pascal.Length - 1; index > 0; index--)
        {
            if (char.IsUpper(pascal[index]) && char.IsLower(pascal[index - 1]) || char.IsUpper(pascal[index]) && index + 1 < pascal.Length && char.IsLower(pascal[index + 1]) && char.IsUpper(pascal[index - 1]))
            {
                return pascal.Substring(index);
            }
        }

        return pascal;
    }
}
