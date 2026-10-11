using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodeBrix.WebGpu.BindingGenerator.Model;
using SilverAssertions;
using Xunit;

namespace CodeBrix.WebGpu.BindingGenerator.Tests;

/// <summary>
/// Guards the hand-maintained specs/wgpu.yml (and the generator's reading of webgpu.yml): every function and
/// struct the model takes from webgpu.yml / wgpu.yml must appear in the release webgpu.h / wgpu.h with the same
/// parameter count / member names, and every one the headers declare must be in the model.
/// </summary>
public class HeaderParityTests
{
    private static readonly ApiModel Model = BindingGenerator.LoadModel(TestPaths.RepositoryRoot);

    private static readonly CHeaderScanner.Header Headers = CHeaderScanner.Scan(
        File.ReadAllText(Path.Combine(TestPaths.FfiFolder, "webgpu-headers", "webgpu.h")),
        File.ReadAllText(Path.Combine(TestPaths.FfiFolder, "wgpu.h")));

    private static bool FromHeaders(string specName) => specName is "webgpu" or "wgpu";

    [Fact]
    public void every_header_function_is_in_the_model_with_the_same_parameter_count()
    {
        //Arrange
        Dictionary<string, int> model = Model.Functions.Where(f => FromHeaders(f.SpecName)).ToDictionary(f => f.CName, f => f.Parameters.Count);
        List<string> problems = new List<string>();

        //Act
        foreach (KeyValuePair<string, int> function in Headers.Functions)
        {
            if (!model.TryGetValue(function.Key, out int count)) problems.Add("missing from the model: " + function.Key);
            else if (count != function.Value) problems.Add(function.Key + ": header has " + function.Value + " parameters, model " + count);
        }

        //Assert
        problems.Should().BeEmpty();
    }

    [Fact]
    public void every_model_function_from_the_headers_specs_is_declared_in_a_header()
        => Model.Functions.Where(f => FromHeaders(f.SpecName)).Select(f => f.CName)
            .Where(name => !Headers.Functions.ContainsKey(name)).Should().BeEmpty();

    [Fact]
    public void every_header_struct_is_in_the_model_with_the_same_members()
    {
        //Arrange
        Dictionary<string, ApiStruct> model = Model.LayoutStructs.ToDictionary(s => s.CName);
        List<string> problems = new List<string>();

        //Act
        foreach (KeyValuePair<string, List<string>> header in Headers.Structs)
        {
            if (!model.TryGetValue(header.Key, out ApiStruct value))
            {
                problems.Add("missing from the model: " + header.Key);
                continue;
            }

            List<string> members = value.Fields.Select(f => f.CName).ToList();
            if (!members.SequenceEqual(header.Value))
            {
                problems.Add(header.Key + ": header [" + string.Join(", ", header.Value) + "] model [" + string.Join(", ", members) + "]");
            }
        }

        //Assert
        problems.Should().BeEmpty();
    }

    [Fact]
    public void every_model_struct_from_the_headers_specs_is_declared_in_a_header()
        => Model.LayoutStructs.Where(s => FromHeaders(s.SpecName)).Select(s => s.CName)
            .Where(name => !Headers.Structs.ContainsKey(name)).Should().BeEmpty();

    [Fact]
    public void the_scanner_sees_the_whole_release_surface()
    {
        //Assert
        Headers.Functions.Count.Should().Be(228);
        Headers.Structs.Should().ContainKey("WGPUNativeDisplayHandle");
        Headers.Structs["WGPUNativeDisplayHandle"].Should().Equal("type", "data");
        Headers.Functions["wgpuDevicePoll"].Should().Be(3);
        Headers.Functions["wgpuGetVersion"].Should().Be(0);
    }
}
