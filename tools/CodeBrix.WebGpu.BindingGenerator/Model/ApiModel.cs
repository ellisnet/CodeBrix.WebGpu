using System.Collections.Generic;

namespace CodeBrix.WebGpu.BindingGenerator.Model;

/// <summary>The merged, resolved model of all specification files - what the templates render.</summary>
public sealed class ApiModel
{
    /// <summary>The specification files, in input order.</summary>
    public List<SpecFile> Files { get; } = new List<SpecFile>();

    /// <summary>The constants, in declaration order.</summary>
    public List<ApiConstant> Constants { get; } = new List<ApiConstant>();

    /// <summary>The enums, sorted by C# name.</summary>
    public List<ApiEnum> Enums { get; } = new List<ApiEnum>();

    /// <summary>The bitflag types, sorted by C# name.</summary>
    public List<ApiBitflag> Bitflags { get; } = new List<ApiBitflag>();

    /// <summary>Every struct (specification structs plus the built-in string view and chain header), sorted by C# name.</summary>
    public List<ApiStruct> Structs { get; } = new List<ApiStruct>();

    /// <summary>The callbacks, sorted by C# name.</summary>
    public List<ApiCallback> Callbacks { get; } = new List<ApiCallback>();

    /// <summary>Every exported function, sorted by C name.</summary>
    public List<ApiFunction> Functions { get; } = new List<ApiFunction>();

    /// <summary>The object handle types, sorted by C# name.</summary>
    public List<ApiHandle> Handles { get; } = new List<ApiHandle>();

    /// <summary>Every struct whose layout is checked against the native library: plain structs and callback-info structs.</summary>
    public List<ApiStruct> LayoutStructs { get; } = new List<ApiStruct>();

    /// <summary>The typedefs (C name and underlying type), sorted by C name.</summary>
    public List<ApiTypedef> Typedefs { get; } = new List<ApiTypedef>();
}

/// <summary>A typedef of a scalar type (<c>WGPUSubmissionIndex</c>).</summary>
public sealed class ApiTypedef
{
    /// <summary>The C type name.</summary>
    public string CName { get; set; }

    /// <summary>The underlying type.</summary>
    public TypeRef Type { get; set; }

    /// <summary>The YAML doc.</summary>
    public string Doc { get; set; }
}

/// <summary>
/// A type as the C ABI sees it: a base type (scalar, enum, bitflag, struct, object handle, typedef, callback)
/// plus an optional pointer level - what the C and Rust renderers (and the layout calculator) need, since the
/// C# spelling loses constness and the category of a name.
/// </summary>
public sealed class TypeRef
{
    /// <summary>scalar, void, enum, bitflag, struct, handle, typedef, callback or proc.</summary>
    public string Kind { get; set; }

    /// <summary>The C base type (<c>uint32_t</c>, <c>WGPUDevice</c>, <c>void</c>, <c>char</c>).</summary>
    public string CName { get; set; }

    /// <summary>The Rust base type (<c>u32</c>, <c>WGPUDevice</c>, <c>c_void</c>, <c>c_char</c>).</summary>
    public string RustName { get; set; }

    /// <summary>null for a value, <c>immutable</c> for <c>T const *</c>, <c>mutable</c> for <c>T *</c>.</summary>
    public string Pointer { get; set; }

    /// <summary>True when this is a pointer.</summary>
    public bool IsPointer => Pointer != null;

    /// <summary>The C spelling, in webgpu.h's style (<c>WGPUBufferDescriptor const *</c>).</summary>
    public string C => Pointer switch
    {
        "immutable" => CName + " const *",
        "mutable" => CName + " *",
        _ => CName,
    };

    /// <summary>The Rust spelling (<c>*const WGPUBufferDescriptor</c>).</summary>
    public string Rust => Pointer switch
    {
        "immutable" => "*const " + RustName,
        "mutable" => "*mut " + RustName,
        _ => RustName,
    };

    /// <summary>A copy with another pointer level.</summary>
    /// <param name="pointer">The pointer level.</param>
    /// <returns>The copy.</returns>
    public TypeRef WithPointer(string pointer) => new TypeRef { Kind = Kind, CName = CName, RustName = RustName, Pointer = pointer };

    /// <summary>A scalar.</summary>
    /// <param name="cName">The C spelling.</param>
    /// <param name="rustName">The Rust spelling.</param>
    /// <param name="pointer">The pointer level.</param>
    /// <returns>The type.</returns>
    public static TypeRef Scalar(string cName, string rustName, string pointer = null) =>
        new TypeRef { Kind = "scalar", CName = cName, RustName = rustName, Pointer = pointer };

    /// <summary>A named type of the API.</summary>
    /// <param name="kind">The kind.</param>
    /// <param name="cName">The C name, also used in Rust.</param>
    /// <param name="pointer">The pointer level.</param>
    /// <returns>The type.</returns>
    public static TypeRef Named(string kind, string cName, string pointer = null) =>
        new TypeRef { Kind = kind, CName = cName, RustName = cName, Pointer = pointer };

    /// <summary><c>void *</c>.</summary>
    public static TypeRef VoidPointer => new TypeRef { Kind = "void", CName = "void", RustName = "c_void", Pointer = "mutable" };
}

/// <summary>A constant.</summary>
public sealed class ApiConstant
{
    /// <summary>The C name (<c>WGPU_WHOLE_SIZE</c>).</summary>
    public string CName { get; set; }

    /// <summary>The C# name.</summary>
    public string CsName { get; set; }

    /// <summary>The C# type.</summary>
    public string CsType { get; set; }

    /// <summary>The C# value expression.</summary>
    public string CsValue { get; set; }

    /// <summary>True when the value can be a C# <c>const</c> (false for <c>nuint</c>).</summary>
    public bool IsConst { get; set; }

    /// <summary>The YAML doc.</summary>
    public string Doc { get; set; }
}

/// <summary>An enum, with the entries of every <c>extended</c> declaration merged in.</summary>
public sealed class ApiEnum
{
    /// <summary>The C type name.</summary>
    public string CName { get; set; }

    /// <summary>The C# type name.</summary>
    public string CsName { get; set; }

    /// <summary>The YAML doc of the base declaration.</summary>
    public string Doc { get; set; }

    /// <summary>The values, base declaration first.</summary>
    public List<ApiEnumValue> Values { get; } = new List<ApiEnumValue>();
}

/// <summary>An enum value.</summary>
public sealed class ApiEnumValue
{
    /// <summary>The C name (<c>WGPUSType_InstanceExtras</c>).</summary>
    public string CName { get; set; }

    /// <summary>The C# member name.</summary>
    public string CsName { get; set; }

    /// <summary>The 32-bit value: enum block &lt;&lt; 16 | 16-bit value.</summary>
    public uint Value { get; set; }

    /// <summary>The YAML doc.</summary>
    public string Doc { get; set; }

    /// <summary>The name of the specification that declared it.</summary>
    public string SpecName { get; set; }
}

/// <summary>A bitflag type.</summary>
public sealed class ApiBitflag
{
    /// <summary>The C type name.</summary>
    public string CName { get; set; }

    /// <summary>The C# type name.</summary>
    public string CsName { get; set; }

    /// <summary>The YAML doc.</summary>
    public string Doc { get; set; }

    /// <summary>The values.</summary>
    public List<ApiBitflagValue> Values { get; } = new List<ApiBitflagValue>();
}

/// <summary>A bitflag value.</summary>
public sealed class ApiBitflagValue
{
    /// <summary>The C name.</summary>
    public string CName { get; set; }

    /// <summary>The C# member name.</summary>
    public string CsName { get; set; }

    /// <summary>The 64-bit value.</summary>
    public ulong Value { get; set; }

    /// <summary>The YAML doc.</summary>
    public string Doc { get; set; }
}

/// <summary>A struct (C layout).</summary>
public sealed class ApiStruct
{
    /// <summary>The C type name.</summary>
    public string CName { get; set; }

    /// <summary>The C# type name.</summary>
    public string CsName { get; set; }

    /// <summary>extensible, extensible_callback_arg, extension, standalone, builtin or callback_info.</summary>
    public string Kind { get; set; }

    /// <summary>The YAML doc.</summary>
    public string Doc { get; set; }

    /// <summary>The fields, in C order (array members expanded into count + pointer).</summary>
    public List<ApiField> Fields { get; } = new List<ApiField>();

    /// <summary>For an extension struct: the C# name of its <c>SType</c> value, when one exists.</summary>
    public string STypeValue { get; set; }

    /// <summary>The name of the specification that declared it (webgpu for the built-ins).</summary>
    public string SpecName { get; set; }
}

/// <summary>A struct field.</summary>
public sealed class ApiField
{
    /// <summary>The C member name.</summary>
    public string CName { get; set; }

    /// <summary>The C# field name.</summary>
    public string CsName { get; set; }

    /// <summary>The C# field type.</summary>
    public string CsType { get; set; }

    /// <summary>The C ABI type.</summary>
    public TypeRef Type { get; set; }

    /// <summary>The YAML doc (or a synthesized one for count / chain fields).</summary>
    public string Doc { get; set; }
}

/// <summary>A callback type.</summary>
public sealed class ApiCallback
{
    /// <summary>The C function-pointer type name (<c>WGPURequestAdapterCallback</c>).</summary>
    public string CName { get; set; }

    /// <summary>The C# base name.</summary>
    public string CsName { get; set; }

    /// <summary>The C# unmanaged function pointer type.</summary>
    public string FunctionPointerType { get; set; }

    /// <summary>The C signature, for docs.</summary>
    public string CSignature { get; set; }

    /// <summary>The callback-info struct, or null for a legacy single-userdata callback.</summary>
    public ApiStruct Info { get; set; }

    /// <summary>The YAML doc.</summary>
    public string Doc { get; set; }

    /// <summary>The arguments, expanded as C passes them.</summary>
    public List<ApiParameter> Parameters { get; } = new List<ApiParameter>();
}

/// <summary>An exported function.</summary>
public sealed class ApiFunction
{
    /// <summary>The C symbol.</summary>
    public string CName { get; set; }

    /// <summary>The C# method name.</summary>
    public string CsName { get; set; }

    /// <summary>The C# return type.</summary>
    public string ReturnType { get; set; }

    /// <summary>The C ABI return type, or null for void.</summary>
    public TypeRef Return { get; set; }

    /// <summary>The YAML doc of the return value, or null.</summary>
    public string ReturnDoc { get; set; }

    /// <summary>The parameters, expanded as C passes them.</summary>
    public List<ApiParameter> Parameters { get; } = new List<ApiParameter>();

    /// <summary>The YAML doc.</summary>
    public string Doc { get; set; }

    /// <summary>The name of the specification that declared it.</summary>
    public string SpecName { get; set; }

    /// <summary>The kind of synthesized function, or null: add_ref, release, free_members, get_proc_address.</summary>
    public string Synthesized { get; set; }

    /// <summary>For an object method: the C# handle (or struct) name of the object.</summary>
    public string OwnerCsName { get; set; }
}

/// <summary>A function or callback parameter.</summary>
public sealed class ApiParameter
{
    /// <summary>The C parameter name.</summary>
    public string CName { get; set; }

    /// <summary>The C# parameter name (keyword-escaped).</summary>
    public string CsName { get; set; }

    /// <summary>The C# type.</summary>
    public string CsType { get; set; }

    /// <summary>The C ABI type.</summary>
    public TypeRef Type { get; set; }

    /// <summary>The YAML doc.</summary>
    public string Doc { get; set; }
}

/// <summary>An object handle type.</summary>
public sealed class ApiHandle
{
    /// <summary>The C type name (<c>WGPUAdapter</c>).</summary>
    public string CName { get; set; }

    /// <summary>The C# type name.</summary>
    public string CsName { get; set; }

    /// <summary>The YAML doc.</summary>
    public string Doc { get; set; }

    /// <summary>The <c>name</c> of the specification file that declares the object.</summary>
    public string SpecName { get; set; }
}
