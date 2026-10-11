using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CodeBrix.WebGpu.Native;

namespace CodeBrix.WebGpu.Graphics;

/// <summary>One vertex attribute: its shader location, format and byte offset within the vertex.</summary>
/// <param name="Location">The <c>@location</c>.</param>
/// <param name="Format">The format.</param>
/// <param name="Offset">The byte offset within one vertex.</param>
public readonly record struct VertexElement(int Location, VertexFormat Format, uint Offset);

/// <summary>
/// The layout of one vertex buffer: stride, step mode (per vertex or per instance) and attributes. Derive one from a
/// blittable struct (<see cref="FromStruct{T}" />) or describe it fluently (<see cref="Describe" />). Pure data: no
/// GPU needed.
/// </summary>
public sealed class VertexLayout
{
    /// <summary>Creates a layout.</summary>
    /// <param name="arrayStride">The bytes from one vertex to the next.</param>
    /// <param name="stepMode">Per vertex or per instance.</param>
    /// <param name="elements">The attributes.</param>
    /// <exception cref="ArgumentNullException"><paramref name="elements" /> is null.</exception>
    public VertexLayout(uint arrayStride, VertexStepMode stepMode, IEnumerable<VertexElement> elements)
    {
        ArgumentNullException.ThrowIfNull(elements);
        ArrayStride = arrayStride;
        StepMode = stepMode;
        Elements = elements.ToArray();
    }

    /// <summary>The bytes from one vertex (or instance) to the next.</summary>
    public uint ArrayStride { get; }

    /// <summary>Per vertex or per instance.</summary>
    public VertexStepMode StepMode { get; }

    /// <summary>The attributes, in declaration order.</summary>
    public IReadOnlyList<VertexElement> Elements { get; }

    /// <summary>One past the highest location used (the first free location for another buffer).</summary>
    public int NextLocation => Elements.Count == 0 ? 0 : Elements.Max(element => element.Location) + 1;

    /// <summary>
    /// Derives a layout from a blittable struct: stride = the struct size; attributes from its fields in memory order
    /// (see <see cref="VertexElementAttribute" />). Inferred formats: <see cref="float" /> Float32, <see cref="Vector2" />
    /// Float32x2, <see cref="Vector3" /> Float32x3, <see cref="Vector4" /> and <see cref="Quaternion" /> Float32x4,
    /// <see cref="int" /> Sint32, <see cref="uint" /> Uint32, and <see cref="Matrix4x4" /> as four Float32x4 attributes
    /// on consecutive locations (its rows, which a WGSL <c>mat4x4</c> built from them reads as columns - so
    /// <c>m * v</c> in WGSL equals <c>Vector4.Transform(v, m)</c>).
    /// </summary>
    /// <typeparam name="T">The vertex (or instance) struct.</typeparam>
    /// <param name="stepMode">Per vertex (the default) or per instance.</param>
    /// <param name="firstLocation">Added to every location (for a second buffer that continues after the first).</param>
    /// <returns>The layout.</returns>
    /// <exception cref="ArgumentException">A field has a type with no inferred format and no explicit one.</exception>
    public static VertexLayout FromStruct<T>(VertexStepMode stepMode = VertexStepMode.Vertex, int firstLocation = 0)
        where T : unmanaged
    {
        Type type = typeof(T);
        FieldInfo[] fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .OrderBy(field => (long)Marshal.OffsetOf(type, field.Name))
            .ToArray();
        bool anyMarked = fields.Any(field => field.GetCustomAttribute<VertexElementAttribute>() != null);
        List<VertexElement> elements = new List<VertexElement>();
        int nextLocation = 0;
        foreach (FieldInfo field in fields)
        {
            VertexElementAttribute marker = field.GetCustomAttribute<VertexElementAttribute>();
            if (anyMarked && marker == null) continue;
            uint offset = (uint)(long)Marshal.OffsetOf(type, field.Name);
            int location = marker != null ? marker.Location : nextLocation;
            if (field.FieldType == typeof(Matrix4x4) && (marker == null || !marker.HasFormat))
            {
                for (int row = 0; row < 4; row++) elements.Add(new VertexElement(firstLocation + location + row, VertexFormat.Float32x4, offset + (uint)(row * 16)));
                nextLocation = location + 4;
                continue;
            }

            VertexFormat format = marker != null && marker.HasFormat ? marker.Format : InferFormat(type, field);
            elements.Add(new VertexElement(firstLocation + location, format, offset));
            nextLocation = location + 1;
        }

        return new VertexLayout((uint)Unsafe.SizeOf<T>(), stepMode, elements);
    }

    /// <summary>Starts a fluent description: attributes are appended at increasing offsets and locations.</summary>
    /// <param name="stepMode">Per vertex (the default) or per instance.</param>
    /// <param name="firstLocation">The location of the first attribute.</param>
    /// <returns>The builder.</returns>
    public static VertexLayoutBuilder Describe(VertexStepMode stepMode = VertexStepMode.Vertex, int firstLocation = 0) => new VertexLayoutBuilder(stepMode, firstLocation);

    /// <summary>Describes the layout.</summary>
    /// <returns>For example <c>stride 32 Vertex: @0 Float32x3 +0, @1 Float32x3 +12, @2 Float32x2 +24</c>.</returns>
    public override string ToString() =>
        "stride " + ArrayStride + " " + StepMode + ": " + string.Join(", ", Elements.Select(element => "@" + element.Location + " " + element.Format + " +" + element.Offset));

    private static VertexFormat InferFormat(Type owner, FieldInfo field)
    {
        Type type = field.FieldType;
        if (type == typeof(float)) return VertexFormat.Float32;
        if (type == typeof(Vector2)) return VertexFormat.Float32x2;
        if (type == typeof(Vector3)) return VertexFormat.Float32x3;
        if (type == typeof(Vector4) || type == typeof(Quaternion)) return VertexFormat.Float32x4;
        if (type == typeof(int)) return VertexFormat.Sint32;
        if (type == typeof(uint)) return VertexFormat.Uint32;
        throw new ArgumentException("Field '" + field.Name + "' of " + owner.Name + " has type " + type.Name + ", which has no inferred vertex format; give it [VertexElement(location, format)].", nameof(owner));
    }
}

/// <summary>A fluent description of a <see cref="VertexLayout" /> (from <see cref="VertexLayout.Describe" />).</summary>
public sealed class VertexLayoutBuilder
{
    private readonly List<VertexElement> elements = new List<VertexElement>();
    private readonly VertexStepMode stepMode;
    private uint offset;
    private int location;

    internal VertexLayoutBuilder(VertexStepMode stepMode, int firstLocation)
    {
        this.stepMode = stepMode;
        location = firstLocation;
    }

    /// <summary>Appends an attribute at the current offset and the next location.</summary>
    /// <param name="format">The format.</param>
    /// <returns>This builder.</returns>
    public VertexLayoutBuilder Add(VertexFormat format)
    {
        elements.Add(new VertexElement(location++, format, offset));
        offset += (uint)VertexFormats.SizeOf(format);
        return this;
    }

    /// <summary>Appends an attribute at the current offset with an explicit location (later attributes continue after it).</summary>
    /// <param name="format">The format.</param>
    /// <param name="shaderLocation">The location.</param>
    /// <returns>This builder.</returns>
    public VertexLayoutBuilder Add(VertexFormat format, int shaderLocation)
    {
        location = shaderLocation;
        return Add(format);
    }

    /// <summary>Skips padding bytes.</summary>
    /// <param name="bytes">The number of bytes.</param>
    /// <returns>This builder.</returns>
    public VertexLayoutBuilder Skip(int bytes)
    {
        offset += (uint)bytes;
        return this;
    }

    /// <summary>Finishes the layout.</summary>
    /// <param name="arrayStride">The stride; 0 (the default) uses the bytes described so far.</param>
    /// <returns>The layout.</returns>
    public VertexLayout Build(uint arrayStride = 0) => new VertexLayout(arrayStride == 0 ? offset : arrayStride, stepMode, elements);
}
