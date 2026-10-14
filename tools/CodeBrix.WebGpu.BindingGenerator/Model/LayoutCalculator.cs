using System;
using System.Collections.Generic;
using System.Linq;
using CodeBrix.WebGpu.BindingGenerator.Yaml;

namespace CodeBrix.WebGpu.BindingGenerator.Model;

/// <summary>A struct or field layout on a 64-bit target: size and alignment in bytes.</summary>
/// <param name="Size">The size.</param>
/// <param name="Alignment">The alignment.</param>
public readonly record struct TypeLayout(int Size, int Alignment);

/// <summary>
/// Computes C struct layouts for 64-bit targets (LP64 and LLP64 agree for every type the specifications use) from
/// the model's <see cref="TypeRef" /> types: natural alignment, members in order, size rounded up to the
/// alignment. The results are emitted as compile-time assertions into the C and Rust outputs; a test compares
/// them with the C# structs, which the native library checks at load time - so all four agree.
/// </summary>
public sealed class LayoutCalculator
{
    private readonly Dictionary<string, ApiStruct> structs;
    private readonly Dictionary<string, ApiTypedef> typedefs;
    private readonly Dictionary<string, TypeLayout> cache = new Dictionary<string, TypeLayout>(StringComparer.Ordinal);
    private readonly HashSet<string> computing = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Creates a calculator over a model.</summary>
    /// <param name="model">The model.</param>
    public LayoutCalculator(ApiModel model)
    {
        structs = model.LayoutStructs.ToDictionary(s => s.CName, StringComparer.Ordinal);
        typedefs = model.Typedefs.ToDictionary(t => t.CName, StringComparer.Ordinal);
    }

    /// <summary>The pointer size of the targets the layouts are for.</summary>
    public const int PointerSize = 8;

    /// <summary>The layout of a model struct, by C name.</summary>
    /// <param name="cName">The C name.</param>
    /// <returns>The layout.</returns>
    /// <exception cref="SpecException">The name is unknown or the struct contains itself.</exception>
    public TypeLayout Struct(string cName)
    {
        if (cache.TryGetValue(cName, out TypeLayout known)) return known;
        if (!structs.TryGetValue(cName, out ApiStruct value)) throw new SpecException("layout", "unknown struct '" + cName + "'");
        if (!computing.Add(cName)) throw new SpecException("layout", "struct '" + cName + "' contains itself");
        TypeLayout layout = Fields(value.Fields.Select(f => f.Type), out _);
        computing.Remove(cName);
        cache[cName] = layout;
        return layout;
    }

    /// <summary>Lays out a sequence of members.</summary>
    /// <param name="members">The member types, in order.</param>
    /// <param name="offsets">The offset of every member.</param>
    /// <returns>The layout of the whole.</returns>
    public TypeLayout Fields(IEnumerable<TypeRef> members, out List<int> offsets)
    {
        offsets = new List<int>();
        int offset = 0;
        int alignment = 1;
        foreach (TypeRef member in members)
        {
            TypeLayout layout = Of(member);
            offset = Align(offset, layout.Alignment);
            offsets.Add(offset);
            offset += layout.Size;
            alignment = Math.Max(alignment, layout.Alignment);
        }

        return new TypeLayout(Align(offset, alignment), alignment);
    }

    /// <summary>The layout of one type.</summary>
    /// <param name="type">The type.</param>
    /// <returns>The layout.</returns>
    public TypeLayout Of(TypeRef type)
    {
        if (type.IsPointer) return new TypeLayout(PointerSize, PointerSize);
        switch (type.Kind)
        {
            case "handle":
            case "callback":
            case "proc":
                return new TypeLayout(PointerSize, PointerSize);
            case "enum":
                return new TypeLayout(4, 4);
            case "bitflag":
                return new TypeLayout(8, 8);
            case "struct":
                return Struct(type.CName);
            case "typedef":
                if (type.CName == "WGPUBool") return new TypeLayout(4, 4);
                if (typedefs.TryGetValue(type.CName, out ApiTypedef typedef)) return Of(typedef.Type);
                throw new SpecException("layout", "unknown typedef '" + type.CName + "'");
            case "scalar":
                int size = type.CName switch
                {
                    "char" => 1,
                    "uint16_t" or "int16_t" => 2,
                    "uint32_t" or "int32_t" or "float" => 4,
                    "uint64_t" or "double" or "size_t" => 8,
                    _ => throw new SpecException("layout", "unknown scalar '" + type.CName + "'"),
                };
                return new TypeLayout(size, size);
            default:
                throw new SpecException("layout", "type '" + type.CName + "' has no size by value");
        }
    }

    private static int Align(int value, int alignment) => (value + alignment - 1) / alignment * alignment;
}
