using System;

namespace CodeBrix.WebGpu.Native;

/// <summary>Filling the per-frame target hand-off for a native module.</summary>
public unsafe partial struct CodebrixFrameHandoff
{
    /// <summary>
    /// Fills a frame hand-off from a texture target and the encoder of its open pass into caller-provided memory
    /// (no allocation - call it every frame).
    /// </summary>
    /// <remarks>
    /// Everything is borrowed for the pass only: the module records every command that touches the texture into
    /// <paramref name="encoder" />, on the thread that opened the pass, closes its own render / compute passes before
    /// returning, and never finishes, submits or releases the encoder; the caller then calls
    /// <see cref="IWebGpuTextureTarget.EndWebGpuPass" />.
    /// </remarks>
    /// <param name="target">The texture target (for example a <c>WebGpuSkiaTarget</c>).</param>
    /// <param name="encoder">The encoder <see cref="IWebGpuTextureTarget.BeginWebGpuPass" /> returned, or a null encoder when no pass is open.</param>
    /// <param name="destination">Where to write the struct (native or pinned memory).</param>
    /// <exception cref="ArgumentNullException"><paramref name="target" /> or <paramref name="destination" /> is null.</exception>
    public static void Fill(IWebGpuTextureTarget target, CommandEncoder encoder, CodebrixFrameHandoff* destination)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (destination == null) throw new ArgumentNullException(nameof(destination));

        destination->Size = (uint)sizeof(CodebrixFrameHandoff);
        destination->Version = CurrentVersion;
        destination->Format = target.Format;
        destination->Width = (uint)Math.Max(0, target.Width);
        destination->Height = (uint)Math.Max(0, target.Height);
        destination->SampleCount = (uint)Math.Max(0, target.SampleCount);
        destination->Usage = target.Usage;
        destination->Texture = target.Texture;
        destination->TextureView = target.TextureView;
        destination->CommandEncoder = encoder;
    }

    /// <summary>Fills a frame hand-off (see <see cref="Fill(IWebGpuTextureTarget, CommandEncoder, CodebrixFrameHandoff*)" />).</summary>
    /// <param name="target">The texture target.</param>
    /// <param name="encoder">The encoder of the open pass, or a null encoder.</param>
    /// <param name="destination">The struct to fill.</param>
    /// <exception cref="ArgumentNullException"><paramref name="target" /> is null.</exception>
    public static void Fill(IWebGpuTextureTarget target, CommandEncoder encoder, ref CodebrixFrameHandoff destination)
    {
        fixed (CodebrixFrameHandoff* pointer = &destination) Fill(target, encoder, pointer);
    }

    /// <summary>Returns a frame hand-off (see <see cref="Fill(IWebGpuTextureTarget, CommandEncoder, CodebrixFrameHandoff*)" />).</summary>
    /// <param name="target">The texture target.</param>
    /// <param name="encoder">The encoder of the open pass, or a null encoder.</param>
    /// <returns>The hand-off.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="target" /> is null.</exception>
    public static CodebrixFrameHandoff From(IWebGpuTextureTarget target, CommandEncoder encoder)
    {
        CodebrixFrameHandoff result = default;
        Fill(target, encoder, &result);
        return result;
    }

    /// <summary>
    /// Opens the target's pass (<see cref="IWebGpuTextureTarget.BeginWebGpuPass" />) and fills the hand-off with it in
    /// one call. Hand <paramref name="destination" /> to the module, then call
    /// <see cref="IWebGpuTextureTarget.EndWebGpuPass" /> when it returns (when the returned encoder is not null).
    /// </summary>
    /// <param name="target">The texture target.</param>
    /// <param name="destination">Where to write the struct (native or pinned memory).</param>
    /// <returns>The pass's encoder; a null encoder when the target cannot take WebGPU work (no pass is open then).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="target" /> or <paramref name="destination" /> is null.</exception>
    /// <exception cref="ObjectDisposedException">The target has been disposed.</exception>
    /// <exception cref="InvalidOperationException">A pass is already open.</exception>
    public static CommandEncoder BeginWebGpuPass(IWebGpuTextureTarget target, CodebrixFrameHandoff* destination)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (destination == null) throw new ArgumentNullException(nameof(destination));
        CommandEncoder encoder = target.BeginWebGpuPass();
        Fill(target, encoder, destination);
        return encoder;
    }
}
