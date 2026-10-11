using System.Collections.Generic;
using System.IO;
using CodeBrix.YamlParse.Core;
using CodeBrix.YamlParse.RepresentationModel;

namespace CodeBrix.WebGpu.BindingGenerator.Yaml;

/// <summary>Loads a YAML specification file into a <see cref="SpecNode" /> tree.</summary>
public static class SpecLoader
{
    /// <summary>Parses YAML text.</summary>
    /// <param name="text">The YAML text (one document).</param>
    /// <param name="sourceName">The file name, for error messages.</param>
    /// <returns>The root node.</returns>
    /// <exception cref="SpecException">The text is not exactly one YAML document, or has duplicate keys.</exception>
    public static SpecNode Parse(string text, string sourceName)
    {
        YamlStream stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(text));
        }
        catch (YamlException exception)
        {
            throw new SpecException(sourceName, "not valid YAML: " + exception.Message);
        }

        if (stream.Documents.Count != 1)
        {
            throw new SpecException(sourceName, "expected exactly one YAML document, found " + stream.Documents.Count);
        }

        return Convert(stream.Documents[0].RootNode, "$");
    }

    private static SpecNode Convert(YamlNode node, string path)
    {
        switch (node)
        {
            case YamlMappingNode mapping:
            {
                List<KeyValuePair<string, SpecNode>> entries = new List<KeyValuePair<string, SpecNode>>();
                HashSet<string> seen = new HashSet<string>();
                foreach (KeyValuePair<YamlNode, YamlNode> pair in mapping.Children)
                {
                    if (pair.Key is not YamlScalarNode keyNode)
                    {
                        throw new SpecException(path, "mapping keys must be scalars");
                    }

                    string key = keyNode.Value ?? string.Empty;
                    if (!seen.Add(key)) throw new SpecException(path + "." + key, "duplicate key");
                    entries.Add(new KeyValuePair<string, SpecNode>(key, Convert(pair.Value, path + "." + key)));
                }

                return SpecNode.Mapping(path, entries);
            }

            case YamlSequenceNode sequence:
            {
                List<SpecNode> items = new List<SpecNode>();
                for (int index = 0; index < sequence.Children.Count; index++)
                {
                    items.Add(Convert(sequence.Children[index], path + "[" + index + "]"));
                }

                return SpecNode.Sequence(path, items);
            }

            case YamlScalarNode scalar:
                return SpecNode.Scalar(path, scalar.Value, scalar.Style == ScalarStyle.Plain || scalar.Style == ScalarStyle.Any);

            default:
                throw new SpecException(path, "unsupported YAML node (aliases are not used by the specifications)");
        }
    }
}
