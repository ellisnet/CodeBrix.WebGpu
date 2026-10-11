using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CodeBrix.WebGpu.BindingGenerator.Yaml;

namespace CodeBrix.WebGpu.BindingGenerator.Model;

/// <summary>
/// Merges the specification files (in input order, with the reference generator's duplicate rules) and
/// resolves every name, type and value into an <see cref="ApiModel" />.
/// </summary>
public sealed class ApiModelBuilder
{
    private readonly List<SpecFile> files;
    private readonly List<SpecConstant> constants = new List<SpecConstant>();
    private readonly Dictionary<string, SpecTypedef> typedefs = new Dictionary<string, SpecTypedef>(StringComparer.Ordinal);
    private readonly Dictionary<string, SpecEnum> enums = new Dictionary<string, SpecEnum>(StringComparer.Ordinal);
    private readonly Dictionary<string, List<SpecEnumEntry>> enumEntries = new Dictionary<string, List<SpecEnumEntry>>(StringComparer.Ordinal);
    private readonly Dictionary<string, SpecBitflag> bitflags = new Dictionary<string, SpecBitflag>(StringComparer.Ordinal);
    private readonly Dictionary<string, List<SpecBitflagEntry>> bitflagEntries = new Dictionary<string, List<SpecBitflagEntry>>(StringComparer.Ordinal);
    private readonly Dictionary<string, SpecStruct> structs = new Dictionary<string, SpecStruct>(StringComparer.Ordinal);
    private readonly Dictionary<string, SpecCallback> callbacks = new Dictionary<string, SpecCallback>(StringComparer.Ordinal);
    private readonly List<SpecFunction> functions = new List<SpecFunction>();
    private readonly Dictionary<string, SpecObject> objects = new Dictionary<string, SpecObject>(StringComparer.Ordinal);
    private readonly Dictionary<string, List<SpecFunction>> objectMethods = new Dictionary<string, List<SpecFunction>>(StringComparer.Ordinal);
    private readonly Dictionary<string, ApiCallback> resolvedCallbacks = new Dictionary<string, ApiCallback>(StringComparer.Ordinal);
    private readonly List<string> errors = new List<string>();

    /// <summary>Creates a builder over the parsed files; the first must be the core webgpu specification.</summary>
    /// <param name="files">The files, in input order.</param>
    public ApiModelBuilder(IReadOnlyList<SpecFile> files)
    {
        if (files.Count == 0 || files[0].Name != "webgpu")
        {
            throw new SpecException("inputs", "the core webgpu.yml must be the first input");
        }

        this.files = files.ToList();
    }

    /// <summary>Merges and resolves.</summary>
    /// <returns>The model.</returns>
    /// <exception cref="SpecException">Duplicates, unresolved references or impossible values (all of them, listed).</exception>
    public ApiModel Build()
    {
        foreach (SpecFile file in files) Merge(file);
        Fail();

        ApiModel model = new ApiModel();
        model.Files.AddRange(files);

        BuildConstants(model);
        BuildEnums(model);
        BuildBitflags(model);
        BuildHandles(model);
        BuildCallbacks(model);
        BuildStructs(model);
        BuildFunctions(model);
        CheckTypeNames(model);
        Fail();
        return model;
    }

    private void Fail()
    {
        if (errors.Count > 0) throw new SpecException("model", string.Join(Environment.NewLine + "  ", errors.Prepend("specification errors:")));
    }

    // ---------------------------------------------------------------- merge (gen/validator.go rules)

    private void Merge(SpecFile file)
    {
        foreach (SpecConstant constant in file.Constants)
        {
            if (constants.Any(c => c.Name == constant.Name)) errors.Add(constant.Path + ": constant '" + constant.Name + "' already declared");
            constants.Add(constant);
        }

        foreach (SpecTypedef typedef in file.Typedefs)
        {
            if (!typedefs.TryAdd(typedef.Name, typedef)) errors.Add(typedef.Path + ": typedef '" + typedef.Name + "' already declared");
        }

        foreach (SpecEnum value in file.Enums)
        {
            if (enums.TryGetValue(value.Name, out SpecEnum _))
            {
                if (!value.Extended) errors.Add(value.Path + ": enum '" + value.Name + "' is being extended but isn't marked as one");
                foreach (SpecEnumEntry entry in value.Entries.Where(e => e != null))
                {
                    if (enumEntries[value.Name].Any(e => e.Name == entry.Name))
                    {
                        errors.Add(entry.Path + ": enum entry '" + value.Name + "." + entry.Name + "' already declared");
                    }

                    enumEntries[value.Name].Add(entry);
                }
            }
            else if (value.Extended)
            {
                errors.Add(value.Path + ": enum '" + value.Name + "' is marked extended but no earlier input declares it");
            }
            else
            {
                enums.Add(value.Name, value);
                enumEntries.Add(value.Name, value.Entries.Where(e => e != null).ToList());
            }
        }

        foreach (SpecBitflag value in file.Bitflags)
        {
            if (bitflags.ContainsKey(value.Name))
            {
                if (!value.Extended) errors.Add(value.Path + ": bitflag '" + value.Name + "' is being extended but isn't marked as one");
                foreach (SpecBitflagEntry entry in value.Entries)
                {
                    if (bitflagEntries[value.Name].Any(e => e.Name == entry.Name))
                    {
                        errors.Add(entry.Path + ": bitflag entry '" + value.Name + "." + entry.Name + "' already declared");
                    }

                    if (entry.Value == null && entry.ValueCombination.Count == 0)
                    {
                        errors.Add(entry.Path + ": extended bitflag entries must have an explicit value or value_combination");
                    }

                    bitflagEntries[value.Name].Add(entry);
                }
            }
            else if (value.Extended)
            {
                errors.Add(value.Path + ": bitflag '" + value.Name + "' is marked extended but no earlier input declares it");
            }
            else
            {
                bitflags.Add(value.Name, value);
                bitflagEntries.Add(value.Name, value.Entries.ToList());
            }
        }

        foreach (SpecCallback value in file.Callbacks)
        {
            if (!callbacks.TryAdd(value.Name, value)) errors.Add(value.Path + ": callback '" + value.Name + "' already declared");
        }

        foreach (SpecStruct value in file.Structs)
        {
            if (!structs.TryAdd(value.Name, value)) errors.Add(value.Path + ": struct '" + value.Name + "' already declared");
        }

        foreach (SpecFunction value in file.Functions)
        {
            if (functions.Any(f => f.Name == value.Name)) errors.Add(value.Path + ": function '" + value.Name + "' already declared");
            functions.Add(value);
        }

        foreach (SpecObject value in file.Objects)
        {
            if (objects.ContainsKey(value.Name))
            {
                if (!value.Extended) errors.Add(value.Path + ": object '" + value.Name + "' is being extended but isn't marked as one");
                foreach (SpecFunction method in value.Methods)
                {
                    if (objectMethods[value.Name].Any(m => m.Name == method.Name))
                    {
                        errors.Add(method.Path + ": method '" + value.Name + "." + method.Name + "' already declared");
                    }

                    objectMethods[value.Name].Add(method);
                }
            }
            else if (value.Extended)
            {
                errors.Add(value.Path + ": object '" + value.Name + "' is marked extended but no earlier input declares it");
            }
            else
            {
                objects.Add(value.Name, value);
                objectMethods.Add(value.Name, value.Methods.ToList());
            }
        }

        foreach (SpecStruct value in structs.Values.Where(s => s.Type == "extension"))
        {
            foreach (string extended in value.Extends)
            {
                if (!structs.ContainsKey(extended) && value.Source == file)
                {
                    errors.Add(value.Path + ": struct '" + value.Name + "' extends '" + extended + "', which is not declared");
                }
            }
        }
    }

    // ---------------------------------------------------------------- constants

    private void BuildConstants(ApiModel model)
    {
        foreach (SpecConstant constant in constants)
        {
            ApiConstant result = new ApiConstant
            {
                CName = "WGPU_" + Names.Constant(constant.Name),
                CsName = Names.Pascal(constant.Name),
                Doc = constant.Doc,
                IsConst = true,
            };

            switch (constant.Value)
            {
                case "usize_max":
                    result.CsType = "nuint";
                    result.CsValue = "nuint.MaxValue";
                    result.IsConst = false;
                    break;
                case "uint32_max":
                    result.CsType = "uint";
                    result.CsValue = "uint.MaxValue";
                    break;
                case "uint64_max":
                    result.CsType = "ulong";
                    result.CsValue = "ulong.MaxValue";
                    break;
                case "nan":
                    result.CsType = "float";
                    result.CsValue = "float.NaN";
                    break;
                default:
                    ulong value = ParseUInt64(constant.Value, constant.Path);
                    result.CsType = value <= uint.MaxValue ? "uint" : "ulong";
                    result.CsValue = value.ToString(CultureInfo.InvariantCulture) + (value <= uint.MaxValue ? "u" : "ul");
                    break;
            }

            model.Constants.Add(result);
        }
    }

    // ---------------------------------------------------------------- enums

    private void BuildEnums(ApiModel model)
    {
        foreach (SpecEnum value in enums.Values)
        {
            ApiEnum result = new ApiEnum { CName = "WGPU" + Names.Pascal(value.Name), CsName = Names.Pascal(value.Name), Doc = value.Doc };
            HashSet<string> names = new HashSet<string>(StringComparer.Ordinal);
            HashSet<uint> values = new HashSet<uint>();
            foreach (SpecEnumEntry entry in enumEntries[value.Name])
            {
                string entryName = Names.Pascal(entry.Name);
                string csName = char.IsDigit(entryName[0]) ? Names.LastWord(result.CsName) + entryName : entryName;
                uint value32 = EnumValue32(entry);
                if (!names.Add(csName)) errors.Add(entry.Path + ": C# member name '" + result.CsName + "." + csName + "' collides");
                if (!values.Add(value32)) errors.Add(entry.Path + ": value 0x" + value32.ToString("X8", CultureInfo.InvariantCulture) + " repeats in enum '" + value.Name + "'");
                result.Values.Add(new ApiEnumValue
                {
                    CName = result.CName + "_" + entryName,
                    CsName = csName,
                    Value = value32,
                    Doc = entry.Doc,
                    SpecName = entry.Source.Name,
                });
            }

            model.Enums.Add(result);
        }

        model.Enums.Sort((a, b) => string.CompareOrdinal(a.CsName, b.CsName));
    }

    /// <summary>
    /// The reference generator's EnumValue32: enum block &lt;&lt; 16 | 16-bit value. The block is chosen by the
    /// entry's resolved namespace (entry, then its declaring enum block, then the file): the file's own name
    /// gives the file's enum_prefix, <c>webgpu</c> gives 0x0000 and <c>compatibility_mode</c> 0x2000.
    /// </summary>
    private uint EnumValue32(SpecEnumEntry entry)
    {
        string space = entry.Namespace ?? entry.DeclaringBlock.Namespace ?? entry.Source.Name;
        ushort block;
        if (space == entry.Source.Name)
        {
            block = entry.Source.EnumPrefix;
        }
        else if (space == "webgpu")
        {
            block = 0x0000;
        }
        else if (space == "compatibility_mode")
        {
            block = 0x2000;
        }
        else
        {
            errors.Add(entry.Path + ": unknown enum namespace '" + space + "'");
            block = 0;
        }

        ushort value16 = entry.Value ?? checked((ushort)entry.Index);
        return ((uint)block << 16) | value16;
    }

    // ---------------------------------------------------------------- bitflags

    private void BuildBitflags(ApiModel model)
    {
        foreach (SpecBitflag value in bitflags.Values)
        {
            ApiBitflag result = new ApiBitflag { CName = "WGPU" + Names.Pascal(value.Name), CsName = Names.Pascal(value.Name), Doc = value.Doc };
            List<SpecBitflagEntry> entries = bitflagEntries[value.Name];
            foreach (SpecBitflagEntry entry in entries)
            {
                ulong bits;
                if (entry.ValueCombination.Count > 0)
                {
                    if (entry.Value != null) errors.Add(entry.Path + ": both value and value_combination");
                    bits = 0;
                    foreach (string part in entry.ValueCombination)
                    {
                        SpecBitflagEntry found = entries.FirstOrDefault(e => e.Name == part);
                        if (found == null)
                        {
                            errors.Add(entry.Path + ": value_combination names unknown entry '" + part + "'");
                            continue;
                        }

                        bits |= SingleBitflagValue(found);
                    }
                }
                else
                {
                    bits = SingleBitflagValue(entry);
                }

                string csName = Names.Pascal(entry.Name);
                if (char.IsDigit(csName[0])) csName = Names.LastWord(result.CsName) + csName;
                result.Values.Add(new ApiBitflagValue { CName = result.CName + "_" + Names.Pascal(entry.Name), CsName = csName, Value = bits, Doc = entry.Doc });
            }

            model.Bitflags.Add(result);
        }

        model.Bitflags.Sort((a, b) => string.CompareOrdinal(a.CsName, b.CsName));
    }

    // gen.go bitflagEntryValue: an implicit value is 2^(index-1), so index 0 is 0 (None).
    private static ulong SingleBitflagValue(SpecBitflagEntry entry) =>
        entry.Value ?? (entry.Index == 0 ? 0UL : 1UL << (entry.Index - 1));

    // ---------------------------------------------------------------- handles

    private void BuildHandles(ApiModel model)
    {
        foreach (SpecObject value in objects.Values)
        {
            model.Handles.Add(new ApiHandle { CName = "WGPU" + Names.Pascal(value.Name), CsName = Names.Pascal(value.Name), Doc = value.Doc });
        }

        model.Handles.Sort((a, b) => string.CompareOrdinal(a.CsName, b.CsName));
    }

    // ---------------------------------------------------------------- callbacks

    private void BuildCallbacks(ApiModel model)
    {
        foreach (SpecCallback value in callbacks.Values)
        {
            ApiCallback result = new ApiCallback
            {
                CName = "WGPU" + Names.Pascal(value.Name) + "Callback",
                CsName = Names.Pascal(value.Name),
                Doc = value.Doc,
            };

            foreach (SpecParameter argument in value.Args) AddParameters(result.Parameters, argument);
            if (!value.IsLegacySingleUserdata)
            {
                result.Parameters.Add(new ApiParameter { CName = "userdata1", CsName = "userdata1", CsType = "void*", Doc = "The first user data pointer from the callback-info struct." });
                result.Parameters.Add(new ApiParameter { CName = "userdata2", CsName = "userdata2", CsType = "void*", Doc = "The second user data pointer from the callback-info struct." });
            }

            result.FunctionPointerType = "delegate* unmanaged[Cdecl]<" + string.Join(", ", result.Parameters.Select(p => p.CsType).Append("void")) + ">";
            result.CSignature = "void (*" + result.CName + ")(" + string.Join(", ", result.Parameters.Select(p => p.CName)) + ")";

            if (!value.IsLegacySingleUserdata)
            {
                ApiStruct info = new ApiStruct
                {
                    CName = result.CName + "Info",
                    CsName = result.CsName + "CallbackInfo",
                    Kind = "callback_info",
                    Doc = value.Doc,
                    SpecName = value.Source.Name,
                };
                info.Fields.Add(new ApiField { CName = "nextInChain", CsName = "NextInChain", CsType = "ChainedStruct*", Doc = "Pointer to the first extension struct, or null." });
                if (value.Style == "callback_mode")
                {
                    info.Fields.Add(new ApiField { CName = "mode", CsName = "Mode", CsType = "CallbackMode", Doc = "Controls when the callback may be called. Has no default." });
                }

                info.Fields.Add(new ApiField { CName = "callback", CsName = "Callback", CsType = result.FunctionPointerType, Doc = "The callback: an `UnmanagedCallersOnly` Cdecl function." });
                info.Fields.Add(new ApiField { CName = "userdata1", CsName = "Userdata1", CsType = "void*", Doc = "Passed back to the callback as its first user data pointer." });
                info.Fields.Add(new ApiField { CName = "userdata2", CsName = "Userdata2", CsType = "void*", Doc = "Passed back to the callback as its second user data pointer." });
                result.Info = info;
                model.LayoutStructs.Add(info);
            }

            resolvedCallbacks.Add(value.Name, result);
            model.Callbacks.Add(result);
        }

        model.Callbacks.Sort((a, b) => string.CompareOrdinal(a.CsName, b.CsName));
    }

    // ---------------------------------------------------------------- structs

    private void BuildStructs(ApiModel model)
    {
        ApiStruct stringView = new ApiStruct
        {
            CName = "WGPUStringView",
            CsName = "StringView",
            Kind = "builtin",
            SpecName = "webgpu",
            Doc = "Nullable value defining a pointer+length view into a UTF-8 encoded string.\n\n"
                  + "Values passed into the API may use the special length value @ref WGPU_STRLEN to indicate a "
                  + "null-terminated string. Non-null values passed out of the API always provide an explicit length "
                  + "and **may or may not be null-terminated**. `{NULL, WGPU_STRLEN}` is the null value; `{any, 0}` "
                  + "is the empty string.",
        };
        stringView.Fields.Add(new ApiField { CName = "data", CsName = "Data", CsType = "byte*", Doc = "Pointer to the UTF-8 bytes, or null." });
        stringView.Fields.Add(new ApiField { CName = "length", CsName = "Length", CsType = "nuint", Doc = "Length in bytes, or @ref WGPU_STRLEN for a null-terminated string." });

        ApiStruct chained = new ApiStruct
        {
            CName = "WGPUChainedStruct",
            CsName = "ChainedStruct",
            Kind = "builtin",
            SpecName = "webgpu",
            Doc = "The header every extension struct starts with; extensible structs point at a chain of them through `nextInChain`.",
        };
        chained.Fields.Add(new ApiField { CName = "next", CsName = "Next", CsType = "ChainedStruct*", Doc = "The next struct in the chain, or null." });
        chained.Fields.Add(new ApiField { CName = "sType", CsName = "SType", CsType = "SType", Doc = "Identifies the struct this header begins." });

        model.Structs.Add(stringView);
        model.Structs.Add(chained);

        ApiEnum sType = model.Enums.FirstOrDefault(e => e.CsName == "SType");
        foreach (SpecStruct value in structs.Values)
        {
            ApiStruct result = new ApiStruct
            {
                CName = "WGPU" + Names.Pascal(value.Name),
                CsName = Names.Pascal(value.Name),
                Kind = value.Type,
                Doc = value.Doc,
                SpecName = value.Source.Name,
            };

            if (value.Type is "extensible" or "extensible_callback_arg")
            {
                result.Fields.Add(new ApiField { CName = "nextInChain", CsName = "NextInChain", CsType = "ChainedStruct*", Doc = "Pointer to the first extension struct in the chain, or null." });
            }
            else if (value.Type == "extension")
            {
                result.Fields.Add(new ApiField { CName = "chain", CsName = "Chain", CsType = "ChainedStruct", Doc = "The chain header; its `sType` must be this struct's @ref WGPUSType value (see `StructType`)." });
                ApiEnumValue tag = sType?.Values.FirstOrDefault(v => v.CsName == result.CsName);
                result.STypeValue = tag?.CsName;
            }

            foreach (SpecParameter member in value.Members)
            {
                string arrayItem = ArrayItemType(member.Type);
                if (arrayItem != null)
                {
                    string single = Names.Singularize(member.Name);
                    result.Fields.Add(new ApiField
                    {
                        CName = Names.Camel(single) + "Count",
                        CsName = Names.Pascal(single) + "Count",
                        CsType = "nuint",
                        Doc = "The number of elements in @ref " + result.CName + "::" + Names.Camel(member.Name) + ".",
                    });
                    result.Fields.Add(new ApiField
                    {
                        CName = Names.Camel(member.Name),
                        CsName = Names.Pascal(member.Name),
                        CsType = CsType(arrayItem, "array", member.Path) + "*",
                        Doc = member.Doc,
                    });
                }
                else if (member.Type.StartsWith("callback.", StringComparison.Ordinal))
                {
                    ApiCallback callback = ResolveCallback(member.Type, member.Path);
                    if (callback?.Info == null)
                    {
                        errors.Add(member.Path + ": a struct member cannot hold a callback without a callback-info struct");
                        continue;
                    }

                    result.Fields.Add(new ApiField { CName = Names.Camel(member.Name), CsName = Names.Pascal(member.Name), CsType = callback.Info.CsName, Doc = member.Doc });
                }
                else
                {
                    result.Fields.Add(new ApiField
                    {
                        CName = Names.Camel(member.Name),
                        CsName = Names.Pascal(member.Name),
                        CsType = CsType(member.Type, member.Pointer, member.Path),
                        Doc = member.Doc,
                    });
                }
            }

            HashSet<string> fieldNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (ApiField field in result.Fields)
            {
                if (field.CsName == result.CsName) field.CsName += "Value";
                if (!fieldNames.Add(field.CsName)) errors.Add(value.Path + ": field name '" + field.CsName + "' repeats");
            }

            if (result.STypeValue != null && fieldNames.Contains("StructType")) result.STypeValue = null;
            model.Structs.Add(result);
        }

        model.Structs.Sort((a, b) => string.CompareOrdinal(a.CsName, b.CsName));
        model.LayoutStructs.AddRange(model.Structs);
        model.LayoutStructs.Sort((a, b) => string.CompareOrdinal(a.CName, b.CName));
    }

    // ---------------------------------------------------------------- functions

    private void BuildFunctions(ApiModel model)
    {
        foreach (SpecFunction value in functions)
        {
            model.Functions.Add(Function(value, "wgpu" + Names.Pascal(value.Name), Names.Pascal(value.Name), null, null));
        }

        // Struct free_members methods (gen/main.go SortAndTransform).
        foreach (SpecStruct value in structs.Values.Where(s => s.FreeMembers))
        {
            string owner = Names.Pascal(value.Name);
            ApiFunction free = Function(
                new SpecFunction { Name = "free_members", Doc = "Frees members which were allocated by the API.", Source = value.Source, Path = value.Path },
                "wgpu" + owner + "FreeMembers",
                owner + "FreeMembers",
                new ApiParameter { CName = Names.Camel(value.Name), CsName = Names.Escape(Names.Camel(value.Name)), CsType = owner, Doc = "The struct whose API-allocated members are freed." },
                owner);
            free.Synthesized = "free_members";
            model.Functions.Add(free);
        }

        foreach (SpecObject value in objects.Values)
        {
            string owner = Names.Pascal(value.Name);
            ApiParameter self = new ApiParameter { CName = Names.Camel(value.Name), CsName = Names.Escape(Names.Camel(value.Name)), CsType = owner, Doc = "The object." };
            foreach (SpecFunction method in objectMethods[value.Name])
            {
                model.Functions.Add(Function(method, "wgpu" + owner + Names.Pascal(method.Name), owner + Names.Pascal(method.Name), self, owner));
            }

            foreach (string name in new[] { "add_ref", "release" })
            {
                ApiFunction synthesized = Function(
                    new SpecFunction { Name = name, Doc = null, Source = value.Source, Path = value.Path },
                    "wgpu" + owner + Names.Pascal(name),
                    owner + Names.Pascal(name),
                    self,
                    owner);
                synthesized.Synthesized = name;
                model.Functions.Add(synthesized);
            }
        }

        // Hand-written in the reference header template, so declared here: wgpuGetProcAddress.
        ApiFunction getProcAddress = new ApiFunction
        {
            CName = "wgpuGetProcAddress",
            CsName = "GetProcAddress",
            ReturnType = "delegate* unmanaged[Cdecl]<void>",
            ReturnDoc = "The function pointer, or null when the name is unknown. Cast it to the function's real signature.",
            Doc = "Returns the \"procedure address\" (function pointer) of the named function.",
            SpecName = "webgpu",
            Synthesized = "get_proc_address",
        };
        getProcAddress.Parameters.Add(new ApiParameter { CName = "procName", CsName = "procName", CsType = "StringView", Doc = "The C name of the function, for example `wgpuCreateInstance`." });
        model.Functions.Add(getProcAddress);

        HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (ApiFunction function in model.Functions)
        {
            if (!seen.Add(function.CName)) errors.Add("function symbol '" + function.CName + "' is produced twice");
        }

        model.Functions.Sort((a, b) => string.CompareOrdinal(a.CName, b.CName));
    }

    private ApiFunction Function(SpecFunction value, string cName, string csName, ApiParameter self, string owner)
    {
        ApiFunction result = new ApiFunction
        {
            CName = cName,
            CsName = csName,
            Doc = value.Doc,
            SpecName = value.Source.Name,
            OwnerCsName = owner,
        };

        if (self != null) result.Parameters.Add(self);
        foreach (SpecParameter argument in value.Args) AddParameters(result.Parameters, argument);

        if (value.Callback != null)
        {
            ApiCallback callback = ResolveCallback(value.Callback, value.Path);
            if (callback?.Info != null)
            {
                result.Parameters.Add(new ApiParameter { CName = "callbackInfo", CsName = "callbackInfo", CsType = callback.Info.CsName, Doc = "How and when the result is delivered." });
            }

            result.ReturnType = "Future";
            result.ReturnDoc = "The future that completes when the callback has been called.";
        }
        else if (value.Returns != null)
        {
            result.ReturnType = CsType(value.Returns.Type, value.Returns.Pointer, value.Path + ".returns");
            result.ReturnDoc = value.Returns.Doc;
        }
        else
        {
            result.ReturnType = "void";
        }

        return result;
    }

    private void AddParameters(List<ApiParameter> target, SpecParameter argument)
    {
        string arrayItem = ArrayItemType(argument.Type);
        if (arrayItem != null)
        {
            string count = Names.Camel(Names.Singularize(argument.Name)) + "Count";
            target.Add(new ApiParameter { CName = count, CsName = Names.Escape(count), CsType = "nuint", Doc = "The number of elements in `" + Names.Camel(argument.Name) + "`." });
            target.Add(new ApiParameter
            {
                CName = Names.Camel(argument.Name),
                CsName = Names.Escape(Names.Camel(argument.Name)),
                CsType = CsType(arrayItem, "array", argument.Path) + "*",
                Doc = argument.Doc,
            });
            return;
        }

        string csType = argument.Type.StartsWith("callback.", StringComparison.Ordinal)
            ? ResolveCallback(argument.Type, argument.Path)?.FunctionPointerType ?? "void*"
            : CsType(argument.Type, argument.Pointer, argument.Path);
        target.Add(new ApiParameter { CName = Names.Camel(argument.Name), CsName = Names.Escape(Names.Camel(argument.Name)), CsType = csType, Doc = argument.Doc });
    }

    // ---------------------------------------------------------------- types

    private static string ArrayItemType(string type) =>
        type.StartsWith("array<", StringComparison.Ordinal) && type.EndsWith(">", StringComparison.Ordinal)
            ? type.Substring(6, type.Length - 7)
            : null;

    private ApiCallback ResolveCallback(string type, string path)
    {
        string name = type.Substring("callback.".Length);
        if (resolvedCallbacks.TryGetValue(name, out ApiCallback callback)) return callback;
        errors.Add(path + ": unknown callback '" + name + "'");
        return null;
    }

    /// <summary>Maps a schema type to its C# spelling (pointer = mutable/immutable/null; "array" for array items, which are not pointers themselves).</summary>
    private string CsType(string type, string pointer, string path)
    {
        string baseType;
        switch (type)
        {
            case "bool": baseType = "uint"; break;
            case "nullable_string":
            case "string_with_default_empty":
            case "out_string":
            case "string":
                baseType = "StringView";
                break;
            case "uint16": baseType = "ushort"; break;
            case "uint32": baseType = "uint"; break;
            case "uint64": baseType = "ulong"; break;
            case "usize": baseType = "nuint"; break;
            case "int16": baseType = "short"; break;
            case "int32": baseType = "int"; break;
            case "float32":
            case "nullable_float32":
                baseType = "float";
                break;
            case "float64":
            case "float64_supertype":
                baseType = "double";
                break;
            case "c_void": baseType = "void"; break;
            default:
                baseType = ResolveNamedType(type, path);
                break;
        }

        bool isPointer = pointer is "mutable" or "immutable";
        if (baseType == "void" && !isPointer && pointer != "array")
        {
            errors.Add(path + ": c_void is only valid behind a pointer");
        }

        return isPointer ? baseType + "*" : baseType;
    }

    private string ResolveNamedType(string type, string path)
    {
        int dot = type.IndexOf('.');
        if (dot < 0)
        {
            errors.Add(path + ": unknown type '" + type + "'");
            return "void";
        }

        string category = type.Substring(0, dot);
        string name = type.Substring(dot + 1);
        bool known = category switch
        {
            "enum" => enums.ContainsKey(name),
            "bitflag" => bitflags.ContainsKey(name),
            "struct" => structs.ContainsKey(name),
            "object" => objects.ContainsKey(name),
            "typedef" => typedefs.ContainsKey(name),
            _ => false,
        };

        if (!known)
        {
            errors.Add(path + ": unknown " + category + " '" + name + "'");
            return "void";
        }

        return category == "typedef" ? CsType(typedefs[name].Type, null, path) : Names.Pascal(name);
    }

    private void CheckTypeNames(ApiModel model)
    {
        HashSet<string> names = new HashSet<string>(StringComparer.Ordinal) { "WebGpu", "Constants" };
        IEnumerable<string> all = model.Enums.Select(e => e.CsName)
            .Concat(model.Bitflags.Select(b => b.CsName))
            .Concat(model.Structs.Select(s => s.CsName))
            .Concat(model.Callbacks.Where(c => c.Info != null).Select(c => c.Info.CsName))
            .Concat(model.Handles.Select(h => h.CsName));
        foreach (string name in all)
        {
            if (!names.Add(name)) errors.Add("C# type name '" + name + "' is produced twice");
        }
    }

    private static ulong ParseUInt64(string text, string path)
    {
        try
        {
            return text.StartsWith("0x", StringComparison.Ordinal)
                ? ulong.Parse(text.Substring(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture)
                : ulong.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture);
        }
        catch (Exception exception) when (exception is FormatException or OverflowException)
        {
            throw new SpecException(path, "constant value '" + text + "' is not an unsigned integer");
        }
    }
}
