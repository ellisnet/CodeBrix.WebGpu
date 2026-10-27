using System;
using System.Collections.Generic;
using System.IO;
using CodeBrix.WebGpu.BindingGenerator.Model;
using CodeBrix.WebGpu.BindingGenerator.Rendering;
using SilverAssertions;
using Xunit;

namespace CodeBrix.WebGpu.BindingGenerator.Tests;

/// <summary>
/// The --check of the hand-written codebrix_wgpu.h (shipped for native modules) against specs/codebrix.yml: the
/// committed header is current, and every kind of drift is reported.
/// </summary>
public class CodebrixHeaderCheckTests
{
    private static readonly ApiModel Model = BindingGenerator.LoadModel(TestPaths.RepositoryRoot);

    private static readonly string Header = File.ReadAllText(Path.Combine(TestPaths.RepositoryRoot, CodebrixHeaderCheck.HeaderPath));

    private static List<string> CheckWith(string from, string to)
    {
        Header.Should().Contain(from);
        return CodebrixHeaderCheck.Check(Model, Header.Replace(from, to, StringComparison.Ordinal));
    }

    [Fact]
    public void the_committed_header_is_current()
    {
        //Assert
        CodebrixHeaderCheck.Check(Model, Header).Should().BeEmpty();
        BindingGenerator.CheckCodebrixHeader(TestPaths.RepositoryRoot).Should().BeEmpty();
    }

    [Fact]
    public void a_changed_parameter_type_is_reported()
        => CheckWith("WGPUTexture texture, WGPUCodebrixVulkanTextureInfo * info", "WGPUTexture texture, WGPUCodebrixVulkanTextureInfo const * info")
            .Should().Contain(problem => problem.Contains("wgpuCodebrixTextureGetVulkanInfo", StringComparison.Ordinal));

    [Fact]
    public void a_missing_struct_member_is_reported()
        => CheckWith("    uint32_t sampleCount;\n} WGPUCodebrixVulkanTextureInfo", "} WGPUCodebrixVulkanTextureInfo")
            .Should().Contain(problem => problem.StartsWith("struct WGPUCodebrixVulkanTextureInfo", StringComparison.Ordinal));

    [Fact]
    public void a_changed_enum_value_is_reported()
        => CheckWith("WGPUCodebrixTextureState_Present = 0x000C0008", "WGPUCodebrixTextureState_Present = 0x000C0009")
            .Should().Contain(problem => problem.StartsWith("enum WGPUCodebrixTextureState", StringComparison.Ordinal));

    [Fact]
    public void a_changed_extended_enum_value_is_reported()
        => CheckWith("WGPUSType_CodebrixPipelineDescriptorExtras = 0x000C0001", "WGPUSType_CodebrixPipelineDescriptorExtras = 0x000C0002")
            .Should().Contain(problem => problem.StartsWith("enum WGPUCodebrixSType", StringComparison.Ordinal));

    [Fact]
    public void a_missing_object_handle_is_reported()
        => CheckWith("typedef struct WGPUCodebrixPipelineCacheImpl* WGPUCodebrixPipelineCache WGPU_OBJECT_ATTRIBUTE;", string.Empty)
            .Should().Contain(problem => problem.Contains("WGPUCodebrixPipelineCacheImpl", StringComparison.Ordinal));

    [Fact]
    public void a_different_api_version_is_reported()
        => CheckWith("#define WGPU_CODEBRIX_API_VERSION (4)", "#define WGPU_CODEBRIX_API_VERSION (5)")
            .Should().Contain(problem => problem.Contains("WGPU_CODEBRIX_API_VERSION", StringComparison.Ordinal));

    [Fact]
    public void declarations_the_specification_lacks_are_reported()
    {
        //Arrange
        string extra = "#endif  /* !defined(WGPU_SKIP_DECLARATIONS) */";

        //Act
        List<string> problems = CheckWith(extra, "WGPU_EXPORT void wgpuCodebrixSomethingNew(void) WGPU_FUNCTION_ATTRIBUTE;\ntypedef struct WGPUCodebrixExtra {\n    uint32_t a;\n} WGPUCodebrixExtra;\n" + extra);

        //Assert
        problems.Should().Contain("function not in specs/codebrix.yml: wgpuCodebrixSomethingNew");
        problems.Should().Contain("struct not in specs/codebrix.yml: WGPUCodebrixExtra");
    }

    [Fact]
    public void a_missing_function_is_reported()
        => CheckWith("WGPU_EXPORT WGPUStatus wgpuCodebrixInstanceAdoptMetalDevice(", "WGPU_EXPORT WGPUStatus wgpuCodebrixInstanceAdoptMetalDeviceX(")
            .Should().Contain(problem => problem.StartsWith("missing or different: WGPU_EXPORT WGPUStatus wgpuCodebrixInstanceAdoptMetalDevice(", StringComparison.Ordinal));
}
