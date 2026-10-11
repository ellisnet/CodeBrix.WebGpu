using System;
using System.Collections.Generic;
using System.IO;
using CodeBrix.WebGpu.BindingGenerator.Model;

namespace CodeBrix.WebGpu.BindingGenerator.Tests;

/// <summary>Locates the repository and the test fixtures.</summary>
internal static class TestPaths
{
    public static string RepositoryRoot { get; } =
        BindingGenerator.FindRepositoryRoot(AppContext.BaseDirectory)
        ?? throw new InvalidOperationException("Cannot find CodeBrix.WebGpu.slnx above " + AppContext.BaseDirectory);

    public static string SchemaJson => File.ReadAllText(Path.Combine(RepositoryRoot, BindingGenerator.SchemaPath));

    public static string FfiFolder => Path.Combine(RepositoryRoot, "wgpu-native-tools", "wgpu-native", "ffi");

    public static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    public static string Golden(string name) => Path.Combine(AppContext.BaseDirectory, "Golden", name);

    /// <summary>The fixture model: Fixtures/webgpu.yml then Fixtures/ext.yml.</summary>
    public static ApiModel FixtureModel() =>
        BindingGenerator.LoadModel(
            SchemaJson,
            new[]
            {
                new KeyValuePair<string, string>("webgpu.yml", Fixture("webgpu.yml")),
                new KeyValuePair<string, string>("ext.yml", Fixture("ext.yml")),
            });

    /// <summary>A model from the fixture core file plus one extra YAML text.</summary>
    public static ApiModel ModelWith(string extensionYaml) =>
        BindingGenerator.LoadModel(
            SchemaJson,
            new[]
            {
                new KeyValuePair<string, string>("webgpu.yml", Fixture("webgpu.yml")),
                new KeyValuePair<string, string>("extra.yml", extensionYaml),
            });
}
