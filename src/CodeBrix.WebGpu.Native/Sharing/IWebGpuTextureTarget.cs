namespace CodeBrix.WebGpu.Native;

/// <summary>
/// A WebGPU texture that another renderer shares, which WebGPU code may render into only inside an explicit pass
/// (<see cref="BeginWebGpuPass" /> ... <see cref="EndWebGpuPass" />) - the contract through which two libraries
/// that both build on this binding hand one texture back and forth without referencing each other. The owner
/// implements it (for example <c>WebGpuSkiaTarget</c>, a texture a Skia surface also draws on); a consumer accepts
/// it (for example <c>GraphicsContext.BeginFrame</c> and <c>RenderTargetView.From</c>).
/// </summary>
/// <remarks>
/// <para>
/// The pass protocol (the "texture is ready for WebGPU" contract):
/// </para>
/// <list type="number">
/// <item><see cref="BeginWebGpuPass" /> makes the texture ready for WebGPU (the other renderer's pending work is
/// submitted and ordered before WebGPU's) and returns a command encoder the TARGET owns.</item>
/// <item>EVERY WebGPU command that touches the texture - rendering into <see cref="TextureView" />, copying to or from
/// <see cref="Texture" />, sampling it - is recorded into THAT encoder, never into another encoder. The consumer
/// never finishes, submits or releases the encoder (it may take and release its own reference).</item>
/// <item><see cref="EndWebGpuPass" /> finishes and submits the encoder and hands the texture back to the other
/// renderer.</item>
/// <item>Between <see cref="EndWebGpuPass" /> and the next <see cref="BeginWebGpuPass" /> the texture belongs to the
/// other renderer: no WebGPU work may use it.</item>
/// </list>
/// <para>
/// Ownership: <see cref="Texture" /> and <see cref="TextureView" /> stay owned by the target; reading them takes NO
/// reference. They are valid until the target is disposed - a target of another size is a new target with new
/// handles. A consumer that keeps a handle longer (a wrapper object, a bind group) takes its own reference
/// (<see cref="WebGpu.TextureAddRef" />, <see cref="WebGpu.TextureViewAddRef" />) and releases only that; it still
/// uses the texture only inside the target's passes. The texture belongs to the device of the source the target was
/// made on - use it only with that device.
/// </para>
/// <para>Threading: one thread, the same one that uses the device.</para>
/// </remarks>
public interface IWebGpuTextureTarget
{
    /// <summary>The texture WebGPU renders into. Null when the target is unavailable or disposed.</summary>
    Texture Texture { get; }

    /// <summary>A default (whole texture, one mip level) view of <see cref="Texture" />, for colour attachments and sampling.</summary>
    TextureView TextureView { get; }

    /// <summary>The texture's format - the colour-target format of every pipeline that renders into it.</summary>
    TextureFormat Format { get; }

    /// <summary>The width in pixels.</summary>
    int Width { get; }

    /// <summary>The height in pixels.</summary>
    int Height { get; }

    /// <summary>
    /// The texture's samples per pixel (1 for a single-sampled texture). Pipelines that render into the texture and
    /// any depth attachment used with it must have the same sample count.
    /// </summary>
    int SampleCount { get; }

    /// <summary>
    /// The texture's usages. Always includes <see cref="TextureUsage.RenderAttachment" />; sample, copy or store only
    /// when the matching usage is present.
    /// </summary>
    TextureUsage Usage { get; }

    /// <summary>True between <see cref="BeginWebGpuPass" /> and <see cref="EndWebGpuPass" />.</summary>
    bool IsInWebGpuPass { get; }

    /// <summary>
    /// Makes the texture ready for WebGPU and returns the encoder to record every command that uses it into (owned by
    /// the target: never finish, submit or release it).
    /// </summary>
    /// <returns>The encoder, or a null encoder when the target cannot take WebGPU work (unavailable); no pass is open then.</returns>
    /// <exception cref="System.ObjectDisposedException">The target has been disposed.</exception>
    /// <exception cref="System.InvalidOperationException">A pass is already open.</exception>
    CommandEncoder BeginWebGpuPass();

    /// <summary>
    /// Finishes and submits the pass's encoder and hands the texture back to the other renderer. The commands must be
    /// complete (no render or compute pass left open). The engine ends the process on an invalid submission, so an
    /// implementation should finish the encoder inside a validation error scope and, on an error, submit nothing,
    /// close the pass and throw (<c>WebGpuSkiaTarget</c> throws <c>WebGpuSkiaValidationException</c>).
    /// </summary>
    /// <exception cref="System.ObjectDisposedException">The target has been disposed.</exception>
    /// <exception cref="System.InvalidOperationException">No pass is open.</exception>
    void EndWebGpuPass();
}
