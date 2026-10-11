using System.Collections.Generic;
using CodeBrix.WebGpu.BindingGenerator.Yaml;

namespace CodeBrix.WebGpu.BindingGenerator.Model;

/// <summary>Turns a schema-validated <see cref="SpecNode" /> tree into a <see cref="SpecFile" />.</summary>
public static class SpecFileReader
{
    /// <summary>Reads a validated document.</summary>
    /// <param name="root">The root node (already validated against schema.json).</param>
    /// <param name="fileName">The file name, for messages.</param>
    /// <returns>The parsed file.</returns>
    public static SpecFile Read(SpecNode root, string fileName)
    {
        SpecFile file = new SpecFile
        {
            FileName = fileName,
            Name = root["name"].Text,
            EnumPrefix = checked((ushort)root["enum_prefix"].AsUInt64()),
            Copyright = root["copyright"].Text,
            Doc = root["doc"].Text,
        };

        foreach (SpecNode node in Items(root["constants"]))
        {
            file.Constants.Add(Base(new SpecConstant { Value = node["value"].Text }, node, file));
        }

        foreach (SpecNode node in Items(root["typedefs"]))
        {
            file.Typedefs.Add(Base(new SpecTypedef { Type = node["type"].Text }, node, file));
        }

        foreach (SpecNode node in Items(root["enums"]))
        {
            SpecEnum value = Base(new SpecEnum(), node, file);
            int index = 0;
            foreach (SpecNode entryNode in Items(node["entries"]))
            {
                if (entryNode.Kind == SpecNodeKind.Null)
                {
                    value.Entries.Add(null);
                }
                else
                {
                    SpecEnumEntry entry = Base(new SpecEnumEntry { Index = index, DeclaringBlock = value }, entryNode, file);
                    if (entryNode["value"] != null) entry.Value = checked((ushort)entryNode["value"].AsUInt64());
                    value.Entries.Add(entry);
                }

                index++;
            }

            file.Enums.Add(value);
        }

        foreach (SpecNode node in Items(root["bitflags"]))
        {
            SpecBitflag value = Base(new SpecBitflag(), node, file);
            int index = 0;
            foreach (SpecNode entryNode in Items(node["entries"]))
            {
                SpecBitflagEntry entry = Base(new SpecBitflagEntry { Index = index++ }, entryNode, file);
                SpecNode raw = entryNode["value"];
                if (raw != null)
                {
                    if (raw.Kind != SpecNodeKind.Integer)
                    {
                        throw new SpecException(fileName + " " + raw.Path, "bitflag values must be integers");
                    }

                    entry.Value = raw.AsUInt64();
                }

                foreach (SpecNode name in Items(entryNode["value_combination"])) entry.ValueCombination.Add(name.Text);
                value.Entries.Add(entry);
            }

            file.Bitflags.Add(value);
        }

        foreach (SpecNode node in Items(root["structs"]))
        {
            SpecStruct value = Base(new SpecStruct { Type = node["type"].Text }, node, file);
            value.FreeMembers = IsTrue(node["free_members"]);
            foreach (SpecNode extended in Items(node["extends"])) value.Extends.Add(extended.Text);
            foreach (SpecNode member in Items(node["members"])) value.Members.Add(Parameter(member));
            file.Structs.Add(value);
        }

        foreach (SpecNode node in Items(root["callbacks"]))
        {
            SpecCallback value = Base(new SpecCallback { Style = node["style"].Text }, node, file);
            foreach (SpecNode argument in Items(node["args"])) value.Args.Add(Parameter(argument));
            file.Callbacks.Add(value);
        }

        foreach (SpecNode node in Items(root["functions"])) file.Functions.Add(Function(node, file));

        foreach (SpecNode node in Items(root["objects"]))
        {
            SpecObject value = Base(new SpecObject(), node, file);
            foreach (SpecNode method in Items(node["methods"])) value.Methods.Add(Function(method, file));
            file.Objects.Add(value);
        }

        return file;
    }

    private static SpecFunction Function(SpecNode node, SpecFile file)
    {
        SpecFunction value = Base(new SpecFunction(), node, file);
        if (node["returns"] != null) value.Returns = Parameter(node["returns"]);
        value.Callback = node["callback"]?.Text;
        foreach (SpecNode argument in Items(node["args"])) value.Args.Add(Parameter(argument));
        return value;
    }

    private static SpecParameter Parameter(SpecNode node) =>
        new SpecParameter
        {
            Name = node["name"]?.Text,
            Doc = node["doc"]?.Text,
            Type = node["type"].Text,
            Pointer = node["pointer"]?.Text,
            Optional = IsTrue(node["optional"]),
            PassedWithOwnership = node["passed_with_ownership"] == null ? null : IsTrue(node["passed_with_ownership"]),
            Default = node["default"]?.Text,
            Path = node.Path,
        };

    private static T Base<T>(T item, SpecNode node, SpecFile file)
        where T : SpecItem
    {
        item.Name = node["name"].Text;
        item.Namespace = node["namespace"]?.Text;
        item.Doc = node["doc"]?.Text;
        item.Extended = IsTrue(node["extended"]);
        item.Source = file;
        item.Path = file.FileName + " " + node.Path;
        return item;
    }

    private static bool IsTrue(SpecNode node) =>
        node != null && node.Kind == SpecNodeKind.Boolean && node.Text.ToLowerInvariant() == "true";

    private static IReadOnlyList<SpecNode> Items(SpecNode node) =>
        node == null || node.Kind != SpecNodeKind.Sequence ? new List<SpecNode>() : node.Items;
}
