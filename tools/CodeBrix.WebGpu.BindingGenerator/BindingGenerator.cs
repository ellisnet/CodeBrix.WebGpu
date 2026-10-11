using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using CodeBrix.WebGpu.BindingGenerator.Model;
using CodeBrix.WebGpu.BindingGenerator.Rendering;
using CodeBrix.WebGpu.BindingGenerator.Schema;
using CodeBrix.WebGpu.BindingGenerator.Yaml;

namespace CodeBrix.WebGpu.BindingGenerator;

/// <summary>The generator end to end: inputs -> validated model -> rendered outputs.</summary>
public static class BindingGenerator
{
    /// <summary>The schema, relative to the repository root.</summary>
    public const string SchemaPath = "wgpu-native-tools/wgpu-native/ffi/webgpu-headers/schema.json";

    /// <summary>The three specification inputs, relative to the repository root, in merge order.</summary>
    public static readonly IReadOnlyList<string> SpecPaths = new[]
    {
        "wgpu-native-tools/wgpu-native/ffi/webgpu-headers/webgpu.yml",
        "wgpu-native-tools/specs/wgpu.yml",
        "wgpu-native-tools/specs/codebrix.yml",
    };

    /// <summary>Loads, validates and merges the inputs of a repository.</summary>
    /// <param name="repositoryRoot">The repository root.</param>
    /// <returns>The model.</returns>
    /// <exception cref="SpecException">An input is invalid.</exception>
    public static ApiModel LoadModel(string repositoryRoot)
    {
        Dictionary<string, string> texts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string path in SpecPaths) texts[path] = File.ReadAllText(Path.Combine(repositoryRoot, path));
        return LoadModel(File.ReadAllText(Path.Combine(repositoryRoot, SchemaPath)), texts);
    }

    /// <summary>Validates and merges specification texts (the first must be the core webgpu.yml).</summary>
    /// <param name="schemaJson">The schema text.</param>
    /// <param name="specs">File name to YAML text, in merge order.</param>
    /// <returns>The model.</returns>
    public static ApiModel LoadModel(string schemaJson, IEnumerable<KeyValuePair<string, string>> specs)
    {
        SchemaValidator validator = new SchemaValidator(schemaJson);
        List<SpecFile> files = new List<SpecFile>();
        foreach (KeyValuePair<string, string> spec in specs)
        {
            string name = Path.GetFileName(spec.Key);
            SpecNode root = SpecLoader.Parse(spec.Value, name);
            validator.Validate(root, name);
            files.Add(SpecFileReader.Read(root, name));
        }

        return new ApiModelBuilder(files).Build();
    }

    /// <summary>Generates every output of a repository in memory.</summary>
    /// <param name="repositoryRoot">The repository root.</param>
    /// <returns>Relative path (forward slashes) to content.</returns>
    public static SortedDictionary<string, string> Generate(string repositoryRoot)
    {
        ApiModel model = LoadModel(repositoryRoot);
        List<KeyValuePair<string, string>> hashes = new List<KeyValuePair<string, string>>
        {
            new KeyValuePair<string, string>(SchemaPath, Sha256(Path.Combine(repositoryRoot, SchemaPath))),
        };
        foreach (string path in SpecPaths) hashes.Add(new KeyValuePair<string, string>(path, Sha256(Path.Combine(repositoryRoot, path))));
        return new CSharpRenderer(model, hashes).RenderAll();
    }

    /// <summary>Compares generated outputs with the files on disk.</summary>
    /// <param name="repositoryRoot">The repository root.</param>
    /// <param name="outputs">The generated outputs.</param>
    /// <returns>A human-readable description of every difference; empty when the files are current.</returns>
    public static List<string> Compare(string repositoryRoot, SortedDictionary<string, string> outputs)
    {
        List<string> differences = new List<string>();
        foreach (KeyValuePair<string, string> output in outputs)
        {
            string path = Path.Combine(repositoryRoot, output.Key);
            if (!File.Exists(path))
            {
                differences.Add("MISSING " + output.Key);
                continue;
            }

            string current = File.ReadAllText(path);
            if (current != output.Value) differences.Add("DIFFERS " + output.Key + Environment.NewLine + LineDiff(current, output.Value));
        }

        string generatedFolder = Path.Combine(repositoryRoot, CSharpRenderer.GeneratedFolder);
        if (Directory.Exists(generatedFolder))
        {
            foreach (string file in Directory.GetFiles(generatedFolder, "*.cs"))
            {
                string relative = CSharpRenderer.GeneratedFolder + "/" + Path.GetFileName(file);
                if (!outputs.ContainsKey(relative)) differences.Add("STALE " + relative + " (not produced by the generator; delete it)");
            }
        }

        return differences;
    }

    /// <summary>Writes the outputs (only the files whose content changed).</summary>
    /// <param name="repositoryRoot">The repository root.</param>
    /// <param name="outputs">The outputs.</param>
    /// <returns>The relative paths written.</returns>
    public static List<string> Write(string repositoryRoot, SortedDictionary<string, string> outputs)
    {
        List<string> written = new List<string>();
        foreach (KeyValuePair<string, string> output in outputs)
        {
            string path = Path.Combine(repositoryRoot, output.Key);
            if (File.Exists(path) && File.ReadAllText(path) == output.Value) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, output.Value, new UTF8Encoding(false));
            written.Add(output.Key);
        }

        return written;
    }

    /// <summary>Finds the repository root: the nearest ancestor holding CodeBrix.WebGpu.slnx.</summary>
    /// <param name="start">Where to start looking.</param>
    /// <returns>The root, or null.</returns>
    public static string FindRepositoryRoot(string start)
    {
        DirectoryInfo directory = new DirectoryInfo(start);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CodeBrix.WebGpu.slnx"))) return directory.FullName;
            directory = directory.Parent;
        }

        return null;
    }

    private static string Sha256(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    private static string LineDiff(string current, string generated)
    {
        string[] a = current.Split('\n');
        string[] b = generated.Split('\n');
        StringBuilder builder = new StringBuilder();
        int shown = 0;
        int max = Math.Max(a.Length, b.Length);
        for (int index = 0; index < max && shown < 20; index++)
        {
            string left = index < a.Length ? a[index] : "<end of file>";
            string right = index < b.Length ? b[index] : "<end of file>";
            if (left == right) continue;
            builder.AppendLine("  line " + (index + 1) + ":");
            builder.AppendLine("    - " + left);
            builder.AppendLine("    + " + right);
            shown++;
        }

        if (shown == 20) builder.AppendLine("  ... (first 20 differing lines shown)");
        return builder.ToString();
    }
}
