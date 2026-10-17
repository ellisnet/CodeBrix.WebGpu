using SilverAssertions;
using Xunit;

namespace CodeBrix.WebGpu.Native.Tests;

/// <summary>
/// Error scopes, including <see cref="ErrorFilter.Internal" /> (wgpu-native-tools/patches/0003): push, nesting,
/// classification, capture and pop, the uncaptured-error callback for everything no scope catches, and a pop of
/// an empty stack. Every test skips, with the reason, when the machine has no adapter.
/// </summary>
public unsafe class ErrorScopeTests : IClassFixture<GpuFixture>
{
    // One texture image and two samplers: valid WGSL, but the GL backend's shader writer cannot express it
    // (GLSL combines a texture with ONE sampler), so the pipeline fails in the backend = a WebGPU internal error.
    private const string TextureWithTwoSamplers =
        "@group(0) @binding(0) var image: texture_2d<f32>;\n"
        + "@group(0) @binding(1) var first: sampler;\n"
        + "@group(0) @binding(2) var second: sampler;\n"
        + "@group(0) @binding(3) var<storage, read_write> result: array<vec4<f32>>;\n"
        + "@compute @workgroup_size(1)\n"
        + "fn main() {\n"
        + "    result[0] = textureSampleLevel(image, first, vec2<f32>(0.5), 0.0) + textureSampleLevel(image, second, vec2<f32>(0.5), 0.0);\n"
        + "}\n";

    private readonly GpuSession gpu;

    public ErrorScopeTests(GpuFixture fixture)
    {
        gpu = fixture.Session;
    }

    private void RequireGpu() => Assert.SkipWhen(gpu.SkipReason != null, gpu.SkipReason ?? string.Empty);

    // MapRead together with MapWrite is a validation error.
    private static void ProvokeValidationError(GpuSession session)
        => WebGpu.BufferRelease(session.CreateBuffer(BufferUsage.MapRead | BufferUsage.MapWrite, 16));

    // A 256 GiB texture (8192 x 8192 x 256 layers of RGBA32Float) is inside the default limits, so only the
    // allocation can fail: an out-of-memory error.
    private static void ProvokeOutOfMemoryError(GpuSession session)
    {
        TextureDescriptor descriptor = new TextureDescriptor
        {
            Usage = TextureUsage.TextureBinding,
            Dimension = TextureDimension.Dimension2D,
            Size = new Extent3D { Width = 8192, Height = 8192, DepthOrArrayLayers = 256 },
            Format = TextureFormat.RGBA32Float,
            MipLevelCount = 1,
            SampleCount = 1,
        };
        Texture texture = WebGpu.DeviceCreateTexture(session.Device, &descriptor);
        WebGpu.TextureRelease(texture);
    }

    private static void ProvokeInternalError(GpuSession session)
    {
        ShaderModule module = session.CreateWgslModule(TextureWithTwoSamplers);
        ComputePipeline pipeline = session.CreateComputePipeline(module, PipelineLayout.Null);
        WebGpu.ComputePipelineRelease(pipeline);
        WebGpu.ShaderModuleRelease(module);
    }

    private static GpuSession CreateGlSession()
    {
        GpuSession session = GpuSession.CreateFor(InstanceBackend.GL);
        if (session.SkipReason != null)
        {
            string reason = session.SkipReason;
            session.Dispose();
            Assert.Skip("No OpenGL adapter for the internal-error tests: " + reason);
        }

        return session;
    }

    [Fact]
    public void DevicePushErrorScope_accepts_Internal_and_an_unused_scope_pops_NoError()
    {
        //Arrange
        RequireGpu();

        //Act
        WebGpu.DevicePushErrorScope(gpu.Device, ErrorFilter.Internal);
        (PopErrorScopeStatus Status, ErrorType Type, string Message) popped = gpu.PopErrorScope();

        //Assert
        popped.Status.Should().Be(PopErrorScopeStatus.Success);
        popped.Type.Should().Be(ErrorType.NoError);
    }

    [Fact]
    public void validation_error_inside_Validation_inside_Internal_is_captured_by_the_Validation_scope()
    {
        //Arrange
        RequireGpu();
        gpu.ClearUncapturedErrors();
        WebGpu.DevicePushErrorScope(gpu.Device, ErrorFilter.Internal);
        WebGpu.DevicePushErrorScope(gpu.Device, ErrorFilter.Validation);

        //Act
        ProvokeValidationError(gpu);
        (PopErrorScopeStatus Status, ErrorType Type, string Message) inner = gpu.PopErrorScope();
        (PopErrorScopeStatus Status, ErrorType Type, string Message) outer = gpu.PopErrorScope();

        //Assert
        inner.Status.Should().Be(PopErrorScopeStatus.Success);
        inner.Type.Should().Be(ErrorType.Validation);
        inner.Message.Should().NotBeNullOrEmpty();
        outer.Type.Should().Be(ErrorType.NoError);
        gpu.UncapturedErrors.Should().BeEmpty();
    }

    [Fact]
    public void validation_error_passes_an_inner_Internal_scope_to_the_enclosing_Validation_scope()
    {
        //Arrange
        RequireGpu();
        gpu.ClearUncapturedErrors();
        WebGpu.DevicePushErrorScope(gpu.Device, ErrorFilter.Validation);
        WebGpu.DevicePushErrorScope(gpu.Device, ErrorFilter.Internal);

        //Act
        ProvokeValidationError(gpu);
        (PopErrorScopeStatus Status, ErrorType Type, string Message) inner = gpu.PopErrorScope();
        (PopErrorScopeStatus Status, ErrorType Type, string Message) outer = gpu.PopErrorScope();

        //Assert
        inner.Type.Should().Be(ErrorType.NoError);
        outer.Status.Should().Be(PopErrorScopeStatus.Success);
        outer.Type.Should().Be(ErrorType.Validation);
        gpu.UncapturedErrors.Should().BeEmpty();
    }

    [Fact]
    public void out_of_memory_error_passes_an_Internal_scope_and_is_captured_by_the_OutOfMemory_scope()
    {
        //Arrange
        RequireGpu();
        gpu.ClearUncapturedErrors();
        WebGpu.DevicePushErrorScope(gpu.Device, ErrorFilter.OutOfMemory);
        WebGpu.DevicePushErrorScope(gpu.Device, ErrorFilter.Internal);

        //Act
        ProvokeOutOfMemoryError(gpu);
        (PopErrorScopeStatus Status, ErrorType Type, string Message) inner = gpu.PopErrorScope();
        (PopErrorScopeStatus Status, ErrorType Type, string Message) outer = gpu.PopErrorScope();

        //Assert
        inner.Type.Should().Be(ErrorType.NoError);
        outer.Status.Should().Be(PopErrorScopeStatus.Success);
        outer.Type.Should().Be(ErrorType.OutOfMemory);
        gpu.UncapturedErrors.Should().BeEmpty();
    }

    [Fact]
    public void validation_error_with_only_an_Internal_scope_open_reaches_the_uncaptured_error_callback()
    {
        //Arrange
        RequireGpu();
        gpu.ClearUncapturedErrors();
        WebGpu.DevicePushErrorScope(gpu.Device, ErrorFilter.Internal);

        //Act
        ProvokeValidationError(gpu);
        (PopErrorScopeStatus Status, ErrorType Type, string Message) popped = gpu.PopErrorScope();

        //Assert
        popped.Type.Should().Be(ErrorType.NoError);
        gpu.UncapturedErrors.Should().ContainSingle().Which.Type.Should().Be(ErrorType.Validation);
    }

    [Fact]
    public void internal_error_passes_an_inner_Validation_scope_and_is_captured_by_the_Internal_scope()
    {
        //Arrange
        using GpuSession session = CreateGlSession();
        WebGpu.DevicePushErrorScope(session.Device, ErrorFilter.Internal);
        WebGpu.DevicePushErrorScope(session.Device, ErrorFilter.Validation);

        //Act
        ProvokeInternalError(session);
        (PopErrorScopeStatus Status, ErrorType Type, string Message) inner = session.PopErrorScope();
        (PopErrorScopeStatus Status, ErrorType Type, string Message) outer = session.PopErrorScope();

        //Assert
        inner.Type.Should().Be(ErrorType.NoError);
        outer.Status.Should().Be(PopErrorScopeStatus.Success);
        outer.Type.Should().Be(ErrorType.Internal);
        outer.Message.Should().Contain("Internal Error");
        session.UncapturedErrors.Should().BeEmpty();
    }

    [Fact]
    public void internal_error_without_an_Internal_scope_reaches_the_uncaptured_error_callback_as_Internal()
    {
        //Arrange
        using GpuSession session = CreateGlSession();
        WebGpu.DevicePushErrorScope(session.Device, ErrorFilter.Validation);

        //Act
        ProvokeInternalError(session);
        (PopErrorScopeStatus Status, ErrorType Type, string Message) popped = session.PopErrorScope();

        //Assert
        popped.Type.Should().Be(ErrorType.NoError);
        session.UncapturedErrors.Should().ContainSingle().Which.Type.Should().Be(ErrorType.Internal);
    }

    [Fact]
    public void DevicePopErrorScope_on_an_empty_stack_reports_Error_status()
    {
        //Arrange
        using GpuSession session = GpuSession.CreateForThisPlatform();
        Assert.SkipWhen(session.SkipReason != null, session.SkipReason ?? string.Empty);

        //Act
        (PopErrorScopeStatus Status, ErrorType Type, string Message) popped = session.PopErrorScope();

        //Assert
        popped.Status.Should().Be(PopErrorScopeStatus.Error);
        popped.Message.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void scopes_unwind_in_order_and_the_stack_is_empty_afterwards()
    {
        //Arrange
        using GpuSession session = GpuSession.CreateForThisPlatform();
        Assert.SkipWhen(session.SkipReason != null, session.SkipReason ?? string.Empty);
        WebGpu.DevicePushErrorScope(session.Device, ErrorFilter.Validation);
        WebGpu.DevicePushErrorScope(session.Device, ErrorFilter.OutOfMemory);
        WebGpu.DevicePushErrorScope(session.Device, ErrorFilter.Internal);
        ProvokeValidationError(session);

        //Act
        ErrorType first = session.PopErrorScope().Type;
        ErrorType second = session.PopErrorScope().Type;
        ErrorType third = session.PopErrorScope().Type;
        PopErrorScopeStatus beyond = session.PopErrorScope().Status;
        ProvokeValidationError(session);

        //Assert
        first.Should().Be(ErrorType.NoError);
        second.Should().Be(ErrorType.NoError);
        third.Should().Be(ErrorType.Validation);
        beyond.Should().Be(PopErrorScopeStatus.Error);
        session.UncapturedErrors.Should().ContainSingle().Which.Type.Should().Be(ErrorType.Validation);
    }

    [Fact]
    public void releasing_a_device_with_open_scopes_is_clean()
    {
        //Arrange
        GpuSession session = GpuSession.CreateForThisPlatform();
        if (session.SkipReason != null)
        {
            string reason = session.SkipReason;
            session.Dispose();
            Assert.Skip(reason);
        }

        WebGpu.DevicePushErrorScope(session.Device, ErrorFilter.Internal);
        WebGpu.DevicePushErrorScope(session.Device, ErrorFilter.Validation);
        ProvokeValidationError(session);

        //Act
        session.Dispose();

        //Assert
        session.UncapturedErrors.Should().BeEmpty();
    }
}
