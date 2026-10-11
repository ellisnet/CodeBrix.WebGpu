using System.Collections.Generic;

namespace CodeBrix.WebGpu.BindingGenerator.Model;

/// <summary>The fields every named specification item has.</summary>
public abstract class SpecItem
{
    /// <summary>The snake_case name as written in the YAML.</summary>
    public string Name { get; set; }

    /// <summary>The <c>namespace</c> property, or null.</summary>
    public string Namespace { get; set; }

    /// <summary>The <c>doc</c> text, or null.</summary>
    public string Doc { get; set; }

    /// <summary>The <c>extended</c> property.</summary>
    public bool Extended { get; set; }

    /// <summary>The specification file the item was declared in.</summary>
    public SpecFile Source { get; set; }

    /// <summary>The path of the item's node, for error messages.</summary>
    public string Path { get; set; }
}

/// <summary>A <c>constants</c> entry.</summary>
public sealed class SpecConstant : SpecItem
{
    /// <summary>The value: a decimal / hexadecimal integer text, or usize_max, uint32_max, uint64_max, nan.</summary>
    public string Value { get; set; }
}

/// <summary>A <c>typedefs</c> entry.</summary>
public sealed class SpecTypedef : SpecItem
{
    /// <summary>The primitive type aliased.</summary>
    public string Type { get; set; }
}

/// <summary>An <c>enums</c> entry.</summary>
public sealed class SpecEnum : SpecItem
{
    /// <summary>The entries; a null element reserves its index.</summary>
    public List<SpecEnumEntry> Entries { get; } = new List<SpecEnumEntry>();
}

/// <summary>An entry of an enum.</summary>
public sealed class SpecEnumEntry : SpecItem
{
    /// <summary>The explicit 16-bit value, or null to use the entry index.</summary>
    public ushort? Value { get; set; }

    /// <summary>The index of the entry within the enum block that declared it.</summary>
    public int Index { get; set; }

    /// <summary>The enum block the entry was declared in (its <c>extended</c> declaration, if any).</summary>
    public SpecEnum DeclaringBlock { get; set; }
}

/// <summary>A <c>bitflags</c> entry.</summary>
public sealed class SpecBitflag : SpecItem
{
    /// <summary>The entries.</summary>
    public List<SpecBitflagEntry> Entries { get; } = new List<SpecBitflagEntry>();
}

/// <summary>An entry of a bitflag type.</summary>
public sealed class SpecBitflagEntry : SpecItem
{
    /// <summary>The explicit value, or null.</summary>
    public ulong? Value { get; set; }

    /// <summary>The names of the entries OR-ed together, or empty.</summary>
    public List<string> ValueCombination { get; } = new List<string>();

    /// <summary>The index of the entry within its declaring block.</summary>
    public int Index { get; set; }
}

/// <summary>A struct member, function argument, callback argument or return value.</summary>
public sealed class SpecParameter
{
    /// <summary>The snake_case name.</summary>
    public string Name { get; set; }

    /// <summary>The doc text.</summary>
    public string Doc { get; set; }

    /// <summary>The schema type string (<c>uint32</c>, <c>struct.foo</c>, <c>array&lt;enum.bar&gt;</c>, ...).</summary>
    public string Type { get; set; }

    /// <summary><c>immutable</c>, <c>mutable</c> or null.</summary>
    public string Pointer { get; set; }

    /// <summary>The <c>optional</c> property.</summary>
    public bool Optional { get; set; }

    /// <summary>The <c>passed_with_ownership</c> property, or null.</summary>
    public bool? PassedWithOwnership { get; set; }

    /// <summary>The <c>default</c> property text, or null.</summary>
    public string Default { get; set; }

    /// <summary>The node path, for error messages.</summary>
    public string Path { get; set; }
}

/// <summary>A <c>structs</c> entry.</summary>
public sealed class SpecStruct : SpecItem
{
    /// <summary>extensible, extensible_callback_arg, extension or standalone.</summary>
    public string Type { get; set; }

    /// <summary>The structs an extension struct extends.</summary>
    public List<string> Extends { get; } = new List<string>();

    /// <summary>The <c>free_members</c> property.</summary>
    public bool FreeMembers { get; set; }

    /// <summary>The members, in C order.</summary>
    public List<SpecParameter> Members { get; } = new List<SpecParameter>();
}

/// <summary>A <c>callbacks</c> entry.</summary>
public sealed class SpecCallback : SpecItem
{
    /// <summary><c>callback_mode</c> or <c>immediate</c>.</summary>
    public string Style { get; set; }

    /// <summary>The declared arguments (userdata1/userdata2 are implied, see <see cref="IsLegacySingleUserdata" />).</summary>
    public List<SpecParameter> Args { get; } = new List<SpecParameter>();

    /// <summary>
    /// True when the last declared argument is named <c>userdata</c>: the callback follows the older
    /// one-userdata convention (wgpu.h's log callback) - no userdata1/userdata2 are appended and no
    /// callback-info struct exists for it.
    /// </summary>
    public bool IsLegacySingleUserdata => Args.Count > 0 && Args[Args.Count - 1].Name == "userdata";
}

/// <summary>A <c>functions</c> entry or an object method.</summary>
public sealed class SpecFunction : SpecItem
{
    /// <summary>The return value, or null for void.</summary>
    public SpecParameter Returns { get; set; }

    /// <summary>The <c>callback</c> property (<c>callback.x</c>), or null.</summary>
    public string Callback { get; set; }

    /// <summary>The arguments.</summary>
    public List<SpecParameter> Args { get; } = new List<SpecParameter>();
}

/// <summary>An <c>objects</c> entry (or the synthesized owner of a struct's free_members method).</summary>
public sealed class SpecObject : SpecItem
{
    /// <summary>The methods, in the order the generator emits them.</summary>
    public List<SpecFunction> Methods { get; } = new List<SpecFunction>();

    /// <summary>True for the synthesized owner of a <c>free_members</c> method of a struct.</summary>
    public bool IsStruct { get; set; }
}

/// <summary>One parsed specification file.</summary>
public sealed class SpecFile
{
    /// <summary>The file name (for messages).</summary>
    public string FileName { get; set; }

    /// <summary>The <c>name</c> property (webgpu, wgpu, codebrix).</summary>
    public string Name { get; set; }

    /// <summary>The <c>enum_prefix</c> property.</summary>
    public ushort EnumPrefix { get; set; }

    /// <summary>The <c>copyright</c> text.</summary>
    public string Copyright { get; set; }

    /// <summary>The <c>doc</c> text.</summary>
    public string Doc { get; set; }

    /// <summary>The constants.</summary>
    public List<SpecConstant> Constants { get; } = new List<SpecConstant>();

    /// <summary>The typedefs.</summary>
    public List<SpecTypedef> Typedefs { get; } = new List<SpecTypedef>();

    /// <summary>The enums.</summary>
    public List<SpecEnum> Enums { get; } = new List<SpecEnum>();

    /// <summary>The bitflags.</summary>
    public List<SpecBitflag> Bitflags { get; } = new List<SpecBitflag>();

    /// <summary>The structs.</summary>
    public List<SpecStruct> Structs { get; } = new List<SpecStruct>();

    /// <summary>The callbacks.</summary>
    public List<SpecCallback> Callbacks { get; } = new List<SpecCallback>();

    /// <summary>The global functions.</summary>
    public List<SpecFunction> Functions { get; } = new List<SpecFunction>();

    /// <summary>The objects.</summary>
    public List<SpecObject> Objects { get; } = new List<SpecObject>();
}
