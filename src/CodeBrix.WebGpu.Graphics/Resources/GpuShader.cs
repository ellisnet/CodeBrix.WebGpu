using CodeBrix.WebGpu.Native;

namespace CodeBrix.WebGpu.Graphics;

/// <summary>
/// A compiled WGSL shader module. Create one with <see cref="GraphicsContext.CreateShader(string, string)" />, which
/// throws a <see cref="ShaderCompilationException" /> carrying the compiler's diagnostics when the source is invalid.
/// </summary>
public sealed class GpuShader : GraphicsResource
{
    internal GpuShader(GraphicsContext context, ShaderModule handle, string source, string label)
        : base(context, label)
    {
        Handle = handle;
        Source = source;
    }

    /// <summary>The native shader module. Owned by this object.</summary>
    public ShaderModule Handle { get; private set; }

    /// <summary>The WGSL source it was compiled from.</summary>
    public string Source { get; }

    private protected override void ReleaseNative()
    {
        if (!Handle.IsNull) Native.WebGpu.ShaderModuleRelease(Handle);
        Handle = ShaderModule.Null;
    }
}
