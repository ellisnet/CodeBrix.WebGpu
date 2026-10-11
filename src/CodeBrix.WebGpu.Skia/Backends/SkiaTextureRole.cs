namespace CodeBrix.WebGpu.Skia.Backends;

/// <summary>What Skia will do with a WebGPU texture it is handed, which fixes the layout / state it is told the texture is in.</summary>
internal enum SkiaTextureRole
{
    /// <summary>Skia draws into it (an <c>SKSurface</c>): colour-attachment layout / render-target state.</summary>
    RenderTarget = 0,

    /// <summary>Skia samples it (an <c>SKImage</c>): shader-read-only layout / pixel-shader-resource state.</summary>
    Sampled = 1,
}
