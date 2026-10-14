using System;
using CodeBrix.WebGpu.Native;
using Xunit;

namespace CodeBrix.WebGpu.Graphics.Tests;

/// <summary>
/// One <see cref="GraphicsContext" /> with every feature the adapter supports and the adapter's own limits (timestamp
/// and pipeline-statistics queries, immediates, multi-draw-indirect-count, texture clears where the GPU has them),
/// shared by the GPU tests of a class. Tests skip with a reason when there is no device or the GPU lacks a feature.
/// </summary>
public sealed class FeatureGpuFixture : IDisposable
{
    /// <summary>Creates the context.</summary>
    public FeatureGpuFixture()
    {
        GraphicsContextOptions options = new GraphicsContextOptions { RequestAllAdapterFeatures = true, RequestAdapterLimits = true, Label = "feature fixture" };
        GraphicsContext.TryCreate(options, out GraphicsContext context, out string reason);
        Context = context;
        Reason = reason;
    }

    /// <summary>The context, or null.</summary>
    public GraphicsContext Context { get; }

    /// <summary>Why there is no context, or null.</summary>
    public string Reason { get; }

    /// <summary>Skips the test when there is no device.</summary>
    public void RequireDevice() => Assert.SkipWhen(Context == null, "No WebGPU device: " + Reason);

    /// <summary>Skips the test when the device lacks a standard feature.</summary>
    /// <param name="feature">The feature.</param>
    public void Require(FeatureName feature)
    {
        RequireDevice();
        Assert.SkipUnless(Context.HasFeature(feature), "The GPU lacks " + feature + ".");
    }

    /// <summary>Skips the test when the device lacks a native feature.</summary>
    /// <param name="feature">The feature.</param>
    public void Require(NativeFeature feature)
    {
        RequireDevice();
        Assert.SkipUnless(Context.HasFeature(feature), "The GPU lacks " + feature + ".");
    }

    /// <summary>Releases the context.</summary>
    public void Dispose() => Context?.Dispose();
}

/// <summary>Small WGSL programs shared by the tests.</summary>
internal static class TestShaders
{
    /// <summary>A full-screen triangle (no vertex buffer) filled with a uniform colour at group 0 binding 0.</summary>
    public const string FullScreenUniformColor = @"
@group(0) @binding(0) var<uniform> color: vec4<f32>;
@vertex fn vs_main(@builtin(vertex_index) i: u32) -> @builtin(position) vec4<f32> {
    var p = array<vec2<f32>, 3>(vec2<f32>(-1.0, -1.0), vec2<f32>(3.0, -1.0), vec2<f32>(-1.0, 3.0));
    return vec4<f32>(p[i], 0.0, 1.0);
}
@fragment fn fs_main() -> @location(0) vec4<f32> { return color; }
";

    /// <summary>A full-screen triangle (no vertex buffer) filled with a constant colour.</summary>
    public const string FullScreenRed = @"
@vertex fn vs_main(@builtin(vertex_index) i: u32) -> @builtin(position) vec4<f32> {
    var p = array<vec2<f32>, 3>(vec2<f32>(-1.0, -1.0), vec2<f32>(3.0, -1.0), vec2<f32>(-1.0, 3.0));
    return vec4<f32>(p[i], 0.0, 1.0);
}
@fragment fn fs_main() -> @location(0) vec4<f32> { return vec4<f32>(1.0, 0.0, 0.0, 1.0); }
";

    /// <summary>A full-screen triangle whose colour comes from immediate data.</summary>
    public const string FullScreenImmediateColor = @"
var<immediate> color: vec4<f32>;
@vertex fn vs_main(@builtin(vertex_index) i: u32) -> @builtin(position) vec4<f32> {
    var p = array<vec2<f32>, 3>(vec2<f32>(-1.0, -1.0), vec2<f32>(3.0, -1.0), vec2<f32>(-1.0, 3.0));
    return vec4<f32>(p[i], 0.0, 1.0);
}
@fragment fn fs_main() -> @location(0) vec4<f32> { return color; }
";

    /// <summary>A quad covering the left half of the target, as two triangles from six vertex indices; red.</summary>
    public const string LeftHalfRed = @"
@vertex fn vs_main(@builtin(vertex_index) i: u32) -> @builtin(position) vec4<f32> {
    var p = array<vec2<f32>, 6>(vec2<f32>(-1.0, -1.0), vec2<f32>(0.0, -1.0), vec2<f32>(0.0, 1.0), vec2<f32>(-1.0, -1.0), vec2<f32>(0.0, 1.0), vec2<f32>(-1.0, 1.0));
    return vec4<f32>(p[i], 0.0, 1.0);
}
@fragment fn fs_main() -> @location(0) vec4<f32> { return vec4<f32>(1.0, 0.0, 0.0, 1.0); }
";

    /// <summary>Doubles every u32 of the storage buffer at group 0 binding 0.</summary>
    public const string Doubler = @"
@group(0) @binding(0) var<storage, read_write> values: array<u32>;
@compute @workgroup_size(64) fn main(@builtin(global_invocation_id) id: vec3<u32>) {
    if (id.x < arrayLength(&values)) { values[id.x] = values[id.x] * 2u; }
}
";

    /// <summary>Adds the immediate value to every u32 of the storage buffer.</summary>
    public const string ImmediateAdder = @"
var<immediate> amount: u32;
@group(0) @binding(0) var<storage, read_write> values: array<u32>;
@compute @workgroup_size(64) fn main(@builtin(global_invocation_id) id: vec3<u32>) {
    if (id.x < arrayLength(&values)) { values[id.x] = values[id.x] + amount; }
}
";

    /// <summary>The empty compute module "main" in SPIR-V (local size 1 x 1 x 1).</summary>
    public static readonly uint[] EmptyComputeSpirV =
    {
        0x07230203, 0x00010000, 0x00000000, 0x00000005, 0x00000000,
        0x00020011, 0x00000001,
        0x0003000E, 0x00000000, 0x00000001,
        0x0005000F, 0x00000005, 0x00000001, 0x6E69616D, 0x00000000,
        0x00060010, 0x00000001, 0x00000011, 0x00000001, 0x00000001, 0x00000001,
        0x00020013, 0x00000002,
        0x00030021, 0x00000003, 0x00000002,
        0x00050036, 0x00000002, 0x00000001, 0x00000000, 0x00000003,
        0x000200F8, 0x00000004,
        0x000100FD,
        0x00010038,
    };
}
