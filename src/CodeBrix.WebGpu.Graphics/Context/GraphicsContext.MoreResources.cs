using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using CodeBrix.WebGpu.Graphics.Interop;
using CodeBrix.WebGpu.Native;
using Buffer = CodeBrix.WebGpu.Native.Buffer;

namespace CodeBrix.WebGpu.Graphics;

/// <summary>Mapped and indirect buffers, SPIR-V / GLSL / trusted shaders and pipeline layouts of <see cref="GraphicsContext" />.</summary>
public sealed unsafe partial class GraphicsContext
{
    /// <summary>
    /// Creates a buffer that starts MAPPED for writing over its whole size (no map usage needed): fill it through
    /// <see cref="GpuBuffer.GetMappedSpan{T}()" /> or <see cref="GpuBuffer.WriteMapped{T}" />, then
    /// <see cref="GpuBuffer.Unmap" /> before the GPU uses it. Usages follow
    /// <see cref="CreateBuffer(ulong, BufferUsage, string)" /> (size rounded up to 4).
    /// </summary>
    /// <param name="size">The size in bytes (at least 1).</param>
    /// <param name="usage">The usages.</param>
    /// <param name="label">A label, or null.</param>
    /// <returns>The mapped buffer.</returns>
    /// <exception cref="GraphicsException">WebGPU rejected the buffer.</exception>
    public GpuBuffer CreateMappedBuffer(ulong size, BufferUsage usage, string label = null)
    {
        ThrowIfDisposed();
        if (size == 0) throw new ArgumentOutOfRangeException(nameof(size), "A buffer needs at least one byte.");
        size = (size + 3) & ~3UL;
        if ((usage & (BufferUsage.MapRead | BufferUsage.MapWrite)) == 0) usage |= BufferUsage.CopySrc | BufferUsage.CopyDst;
        using Utf8Text text = new Utf8Text();
        BufferDescriptor descriptor = new BufferDescriptor { Label = text.View(label), Usage = usage, Size = size, MappedAtCreation = 1 };
        BeginGuard();
        Buffer handle = Native.WebGpu.DeviceCreateBuffer(Device, &descriptor);
        string error = EndGuard();
        if (error != null || handle.IsNull)
        {
            if (!handle.IsNull) Native.WebGpu.BufferRelease(handle);
            throw new GraphicsException("The buffer" + LabelText(label) + " could not be created: " + (error ?? "wgpuDeviceCreateBuffer returned null."));
        }

        GpuBuffer buffer = new GpuBuffer(this, handle, size, usage, label);
        buffer.MarkMappedAtCreation();
        return buffer;
    }

    /// <summary>
    /// Creates a buffer holding <paramref name="data" /> by writing it while the buffer is mapped at creation - no
    /// queue staging, and the data is there before ANY command, even ones already recorded.
    /// </summary>
    /// <typeparam name="T">A blittable struct (or primitive).</typeparam>
    /// <param name="data">The initial content.</param>
    /// <param name="usage">The usages.</param>
    /// <param name="label">A label, or null.</param>
    /// <returns>The buffer (unmapped).</returns>
    public GpuBuffer CreateInitializedBuffer<T>(ReadOnlySpan<T> data, BufferUsage usage, string label = null)
        where T : unmanaged
    {
        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(data);
        GpuBuffer buffer = CreateMappedBuffer((ulong)Math.Max(1, bytes.Length), usage, label);
        bytes.CopyTo(buffer.GetMappedRange(0));
        buffer.Unmap();
        return buffer;
    }

    /// <summary>
    /// Creates an indirect-argument buffer holding <paramref name="arguments" /> (<see cref="DrawIndirectArgs" />,
    /// <see cref="DrawIndexedIndirectArgs" />, <see cref="DispatchIndirectArgs" />, or 32-bit counts). It is also a
    /// storage buffer, so a compute shader can write the arguments on the GPU.
    /// </summary>
    /// <typeparam name="T">The argument struct.</typeparam>
    /// <param name="arguments">The arguments.</param>
    /// <param name="label">A label, or null.</param>
    /// <returns>The buffer.</returns>
    public GpuBuffer CreateIndirectBuffer<T>(ReadOnlySpan<T> arguments, string label = null)
        where T : unmanaged => CreateBuffer(arguments, BufferUsage.Indirect | BufferUsage.Storage, label);

    /// <summary>Creates an empty indirect-argument (and storage) buffer.</summary>
    /// <param name="size">The size in bytes.</param>
    /// <param name="label">A label, or null.</param>
    /// <returns>The buffer.</returns>
    public GpuBuffer CreateIndirectBuffer(ulong size, string label = null) => CreateBuffer(size, BufferUsage.Indirect | BufferUsage.Storage, label);

    /// <summary>
    /// Creates a shader module from SPIR-V words, validated and translated by the engine like WGSL
    /// (<see cref="ShaderLanguage.SpirV" />).
    /// </summary>
    /// <param name="spirv">The SPIR-V words (starting with the magic number 0x07230203).</param>
    /// <param name="label">A label, or null.</param>
    /// <returns>The shader.</returns>
    /// <exception cref="ShaderCompilationException">The module is invalid; the exception carries the diagnostics.</exception>
    public GpuShader CreateShaderFromSpirV(ReadOnlySpan<uint> spirv, string label = null)
    {
        ThrowIfDisposed();
        if (spirv.Length == 0) throw new ArgumentException("The SPIR-V module is empty.", nameof(spirv));
        using Utf8Text text = new Utf8Text();
        fixed (uint* words = spirv)
        {
            ShaderSourceSPIRV source = new ShaderSourceSPIRV { CodeSize = (uint)spirv.Length, Code = words };
            source.Chain.SType = ShaderSourceSPIRV.StructType;
            ShaderModuleDescriptor descriptor = new ShaderModuleDescriptor { NextInChain = &source.Chain, Label = text.View(label) };
            return CreateShaderCore(&descriptor, null, label, ShaderLanguage.SpirV, null);
        }
    }

    /// <summary>
    /// Creates a shader module from SPIR-V handed to the driver UNCHANGED (no validation or translation). Needs a
    /// SPIR-V passthrough feature the engine release this package ships does not expose, so on it this always throws
    /// with the engine's validation message; prefer <see cref="CreateShaderFromSpirV" />.
    /// </summary>
    /// <param name="spirv">The SPIR-V words.</param>
    /// <param name="label">A label, or null.</param>
    /// <returns>The shader.</returns>
    /// <exception cref="ShaderCompilationException">The engine refused the module.</exception>
    public GpuShader CreateShaderFromSpirVPassthrough(ReadOnlySpan<uint> spirv, string label = null)
    {
        ThrowIfDisposed();
        if (spirv.Length == 0) throw new ArgumentException("The SPIR-V module is empty.", nameof(spirv));
        using Utf8Text text = new Utf8Text();
        fixed (uint* words = spirv)
        {
            ShaderModuleDescriptorSpirV descriptor = new ShaderModuleDescriptorSpirV { Label = text.View(label), SourceSize = (uint)spirv.Length, Source = words };
            BeginGuard();
            ShaderModule module = Native.WebGpu.DeviceCreateShaderModuleSpirV(Device, &descriptor);
            string diagnostics = EndGuard();
            return FinishShader(module, diagnostics, null, label, ShaderLanguage.SpirVPassthrough);
        }
    }

    /// <summary>
    /// Compiles WGSL with chosen runtime checks turned OFF (<see cref="ShaderRuntimeChecks.None" />) or on: bounds
    /// checks and loop bounding cost time; skip them only for shaders you trust.
    /// </summary>
    /// <param name="wgsl">The WGSL source.</param>
    /// <param name="runtimeChecks">The checks to keep.</param>
    /// <param name="label">A label, or null.</param>
    /// <returns>The shader.</returns>
    /// <exception cref="ShaderCompilationException">The source does not compile.</exception>
    public GpuShader CreateShaderTrusted(string wgsl, ShaderRuntimeChecks runtimeChecks, string label = null)
    {
        ArgumentNullException.ThrowIfNull(wgsl);
        ThrowIfDisposed();
        using Utf8Text text = new Utf8Text();
        ShaderSourceWGSL source = new ShaderSourceWGSL { Code = text.View(wgsl) };
        source.Chain.SType = ShaderSourceWGSL.StructType;
        ShaderModuleDescriptor descriptor = new ShaderModuleDescriptor { NextInChain = &source.Chain, Label = text.View(label) };
        return CreateShaderCore(&descriptor, wgsl, label, ShaderLanguage.Wgsl, runtimeChecks);
    }

    /// <summary>
    /// Compiles a GLSL shader for one stage (Vulkan-flavoured GLSL 4.40+, entry point <c>main</c>), with optional
    /// preprocessor defines.
    /// </summary>
    /// <param name="glsl">The GLSL source.</param>
    /// <param name="stage">The stage (exactly one of vertex, fragment, compute).</param>
    /// <param name="label">A label, or null.</param>
    /// <param name="defines">Preprocessor defines (name, value), or null.</param>
    /// <returns>The shader.</returns>
    /// <exception cref="ShaderCompilationException">The source does not compile.</exception>
    public GpuShader CreateShaderFromGlsl(string glsl, ShaderStage stage, string label = null, IReadOnlyDictionary<string, string> defines = null)
    {
        ArgumentNullException.ThrowIfNull(glsl);
        ThrowIfDisposed();
        if (stage != ShaderStage.Vertex && stage != ShaderStage.Fragment && stage != ShaderStage.Compute) throw new ArgumentOutOfRangeException(nameof(stage), "Name exactly one stage.");
        using Utf8Text text = new Utf8Text();
        int defineCount = defines?.Count ?? 0;
        ShaderDefine* nativeDefines = (ShaderDefine*)text.Allocate(sizeof(ShaderDefine) * Math.Max(defineCount, 1));
        if (defines != null)
        {
            int index = 0;
            foreach (KeyValuePair<string, string> define in defines) nativeDefines[index++] = new ShaderDefine { Name = text.View(define.Key), Value = text.View(define.Value ?? string.Empty) };
        }

        ShaderSourceGLSL source = new ShaderSourceGLSL { Stage = stage, Code = text.View(glsl), DefineCount = (uint)defineCount, Defines = defineCount == 0 ? null : nativeDefines };
        source.Chain.SType = ShaderSourceGLSL.StructType;
        ShaderModuleDescriptor descriptor = new ShaderModuleDescriptor { NextInChain = &source.Chain, Label = text.View(label) };
        return CreateShaderCore(&descriptor, glsl, label, ShaderLanguage.Glsl, null);
    }

    /// <summary>
    /// Creates an explicit pipeline layout: bind-group layouts for groups 0, 1, ... and the size of the immediate
    /// block (push constants; needs <see cref="NativeFeature.Immediates" /> and a device
    /// <see cref="Limits.MaxImmediateSize" /> at least that large).
    /// </summary>
    /// <param name="immediateSize">The immediate block size in bytes (multiple of 4; 0 for none).</param>
    /// <param name="label">A label, or null.</param>
    /// <param name="bindGroupLayouts">The bind-group layouts.</param>
    /// <returns>The layout.</returns>
    /// <exception cref="GraphicsException">WebGPU rejected the layout.</exception>
    public GpuPipelineLayout CreatePipelineLayout(uint immediateSize, string label, params GpuBindGroupLayout[] bindGroupLayouts)
    {
        ThrowIfDisposed();
        bindGroupLayouts ??= Array.Empty<GpuBindGroupLayout>();
        if (immediateSize % 4 != 0) throw new ArgumentOutOfRangeException(nameof(immediateSize), "The immediate size must be a multiple of 4.");
        PipelineLayout handle = CreatePipelineLayoutHandle(bindGroupLayouts, immediateSize, label, out string error);
        if (error != null || handle.IsNull)
        {
            if (!handle.IsNull) Native.WebGpu.PipelineLayoutRelease(handle);
            throw new GraphicsException("The pipeline layout" + LabelText(label) + " could not be created: " + (error ?? "wgpuDeviceCreatePipelineLayout returned null."));
        }

        return new GpuPipelineLayout(this, handle, (GpuBindGroupLayout[])bindGroupLayouts.Clone(), immediateSize, false, label);
    }

    /// <summary>Creates a native pipeline layout inside an error scope.</summary>
    /// <param name="layouts">The bind-group layouts.</param>
    /// <param name="immediateSize">The immediate size.</param>
    /// <param name="label">A label, or null.</param>
    /// <param name="error">The validation error, or null.</param>
    /// <returns>The layout (possibly an invalid one when <paramref name="error" /> is set).</returns>
    internal PipelineLayout CreatePipelineLayoutHandle(IReadOnlyList<GpuBindGroupLayout> layouts, uint immediateSize, string label, out string error)
    {
        using Utf8Text text = new Utf8Text();
        BindGroupLayout* handles = (BindGroupLayout*)text.Allocate(sizeof(BindGroupLayout) * Math.Max(layouts.Count, 1));
        for (int index = 0; index < layouts.Count; index++)
        {
            GpuBindGroupLayout layout = layouts[index] ?? throw new ArgumentException("Bind-group layout " + index + " is null.", nameof(layouts));
            layout.ThrowIfDisposed();
            handles[index] = layout.Handle;
        }

        PipelineLayoutDescriptor descriptor = new PipelineLayoutDescriptor
        {
            Label = text.View(label),
            BindGroupLayoutCount = (nuint)layouts.Count,
            BindGroupLayouts = layouts.Count == 0 ? null : handles,
            ImmediateSize = immediateSize,
        };
        BeginGuard();
        PipelineLayout handle = Native.WebGpu.DeviceCreatePipelineLayout(Device, &descriptor);
        error = EndGuard();
        return handle;
    }

    private GpuShader CreateShaderCore(ShaderModuleDescriptor* descriptor, string source, string label, ShaderLanguage language, ShaderRuntimeChecks? runtimeChecks)
    {
        BeginGuard();
        ShaderModule module = runtimeChecks.HasValue
            ? Native.WebGpu.DeviceCreateShaderModuleTrusted(Device, descriptor, runtimeChecks.Value)
            : Native.WebGpu.DeviceCreateShaderModule(Device, descriptor);
        string diagnostics = EndGuard();
        return FinishShader(module, diagnostics, source, label, language);
    }

    private GpuShader FinishShader(ShaderModule module, string diagnostics, string source, string label, ShaderLanguage language)
    {
        if (diagnostics != null || module.IsNull)
        {
            if (!module.IsNull) Native.WebGpu.ShaderModuleRelease(module);
            throw new ShaderCompilationException(label, diagnostics ?? "The engine returned no shader module.");
        }

        return new GpuShader(this, module, source, label) { Language = language };
    }
}
