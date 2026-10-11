using System;
using CodeBrix.WebGpu.Native;

namespace CodeBrix.WebGpu.Graphics;

/// <summary>
/// Marks a field of a vertex struct as a shader input for <see cref="VertexLayout.FromStruct{T}" />: its
/// <c>@location</c> (relative to the layout's first location) and, optionally, its format. When a struct marks any
/// field, unmarked fields are skipped (treated as padding); when it marks none, every field is an input, in
/// memory order, with consecutive locations.
/// </summary>
[AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
public sealed class VertexElementAttribute : Attribute
{
    /// <summary>Marks the field with a location; the format is inferred from the field type.</summary>
    /// <param name="location">The location, relative to the layout's first location.</param>
    public VertexElementAttribute(int location)
    {
        Location = location;
    }

    /// <summary>Marks the field with a location and an explicit format (for example <see cref="VertexFormat.Unorm8x4" /> for a packed <see cref="uint" /> colour).</summary>
    /// <param name="location">The location, relative to the layout's first location.</param>
    /// <param name="format">The format.</param>
    public VertexElementAttribute(int location, VertexFormat format)
    {
        Location = location;
        Format = format;
        HasFormat = true;
    }

    /// <summary>The location, relative to the layout's first location.</summary>
    public int Location { get; }

    /// <summary>The explicit format (meaningful when <see cref="HasFormat" />).</summary>
    public VertexFormat Format { get; }

    /// <summary>True when the format was given explicitly.</summary>
    public bool HasFormat { get; }
}
