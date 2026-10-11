using System;
using CodeBrix.WebGpu.Native;
using SilverAssertions;
using Xunit;

namespace CodeBrix.WebGpu.Graphics.Tests;

/// <summary>Samplers, bind-group layouts and bind groups.</summary>
public class BindingTests : IClassFixture<GraphicsGpuFixture>
{
    private readonly GraphicsGpuFixture gpu;

    public BindingTests(GraphicsGpuFixture fixture)
    {
        gpu = fixture;
    }

    [Fact]
    public void Sampler_presets_create_samplers_with_their_settings()
    {
        //Arrange
        gpu.RequireDevice();

        //Act
        using GpuSampler linear = gpu.Context.CreateSampler(SamplerDescription.LinearRepeat);
        using GpuSampler nearest = gpu.Context.CreateSampler(SamplerDescription.NearestClamp);
        using GpuSampler comparison = gpu.Context.CreateSampler(new SamplerDescription { Compare = CompareFunction.Less, Label = "shadow" });

        //Assert
        linear.AddressMode.Should().Be(AddressMode.Repeat);
        nearest.MagFilter.Should().Be(FilterMode.Nearest);
        comparison.IsComparison.Should().BeTrue();
        comparison.Label.Should().Be("shadow");
    }

    [Fact]
    public void Bind_group_binds_a_uniform_a_texture_and_a_sampler()
    {
        //Arrange
        gpu.RequireDevice();
        GraphicsContext context = gpu.Context;
        using GpuBindGroupLayout layout = context.CreateBindGroupLayout(
            "material",
            BindingLayoutEntry.Uniform(0, ShaderStage.Fragment),
            BindingLayoutEntry.Texture(1, ShaderStage.Fragment),
            BindingLayoutEntry.Sampler(2, ShaderStage.Fragment));
        using GpuBuffer uniforms = context.CreateUniformBuffer(16);
        using GpuTexture texture = context.CreateTextureFromRgba(1, 1, new byte[] { 255, 255, 255, 255 });
        using GpuSampler sampler = context.CreateSampler();

        //Act
        using GpuBindGroup group = context.CreateBindGroup(layout, BindingEntry.ForBuffer(0, uniforms), BindingEntry.ForTexture(1, texture), BindingEntry.ForSampler(2, sampler));

        //Assert
        group.Handle.IsNull.Should().BeFalse();
        group.Layout.Should().BeSameAs(layout);
        layout.Entries.Should().HaveCount(3);
        layout.Entries[1].Kind.Should().Be(BindingKind.Texture);
    }

    [Fact]
    public void Bind_group_with_a_missing_resource_throws_with_the_validation_message()
    {
        //Arrange
        gpu.RequireDevice();
        using GpuBindGroupLayout layout = gpu.Context.CreateBindGroupLayout(BindingLayoutEntry.Uniform(0, ShaderStage.Vertex), BindingLayoutEntry.Sampler(1, ShaderStage.Fragment));
        using GpuBuffer uniforms = gpu.Context.CreateUniformBuffer(16);

        //Act
        Action act = () => gpu.Context.CreateBindGroup(layout, "incomplete", BindingEntry.ForBuffer(0, uniforms));

        //Assert
        act.Should().Throw<GraphicsException>().WithMessage("*incomplete*");
        gpu.Context.TakeErrors().Should().BeEmpty();
    }

    [Fact]
    public void Storage_and_storage_texture_layouts_are_accepted()
    {
        //Arrange
        gpu.RequireDevice();

        //Act
        using GpuBindGroupLayout layout = gpu.Context.CreateBindGroupLayout(
            BindingLayoutEntry.Storage(0, ShaderStage.Compute),
            BindingLayoutEntry.Storage(1, ShaderStage.Compute | ShaderStage.Vertex, readOnly: true),
            BindingLayoutEntry.StorageTexture(2, ShaderStage.Compute, TextureFormat.RGBA8Unorm));

        //Assert
        layout.Handle.IsNull.Should().BeFalse();
        layout.Entries[1].BufferType.Should().Be(BufferBindingType.ReadOnlyStorage);
    }
}
