using System;
using CodeBrix.WebGpu.Native;

namespace CodeBrix.WebGpu.Graphics;

/// <summary>
/// Where a render pass draws: a colour texture view with its format and size, and optionally a depth view. Any view
/// works - a <see cref="HeadlessRenderTarget" />'s, a <see cref="WindowSurface" /> frame's, a <see cref="GpuTexture" />'s,
/// or one another library owns (for a <c>WebGpuSkiaTarget</c>:
/// <c>new RenderTargetView(target.TextureView, target.Format, target.Width, target.Height)</c>).
/// </summary>
public readonly struct RenderTargetView
{
    /// <summary>Creates a target.</summary>
    /// <param name="colorView">The colour view.</param>
    /// <param name="colorFormat">Its format.</param>
    /// <param name="width">Its width in pixels.</param>
    /// <param name="height">Its height in pixels.</param>
    /// <param name="depthView">A depth view of the same size, or null.</param>
    /// <param name="depthFormat">The depth view's format, or <see cref="TextureFormat.Undefined" />.</param>
    public RenderTargetView(TextureView colorView, TextureFormat colorFormat, int width, int height, TextureView depthView = default, TextureFormat depthFormat = TextureFormat.Undefined)
    {
        ColorView = colorView;
        ColorFormat = colorFormat;
        Width = width;
        Height = height;
        DepthView = depthView;
        DepthFormat = depthView.IsNull ? TextureFormat.Undefined : depthFormat;
    }

    /// <summary>The colour view.</summary>
    public TextureView ColorView { get; }

    /// <summary>The colour format.</summary>
    public TextureFormat ColorFormat { get; }

    /// <summary>The width in pixels.</summary>
    public int Width { get; }

    /// <summary>The height in pixels.</summary>
    public int Height { get; }

    /// <summary>The depth view, or null.</summary>
    public TextureView DepthView { get; }

    /// <summary>The depth format, or <see cref="TextureFormat.Undefined" /> when there is no depth view.</summary>
    public TextureFormat DepthFormat { get; }

    /// <summary>True when the target has a depth view.</summary>
    public bool HasDepth => !DepthView.IsNull;

    /// <summary>Width / height.</summary>
    public float AspectRatio => Height == 0 ? 1f : (float)Width / Height;

    /// <summary>The same colour view with a depth texture.</summary>
    /// <param name="depth">The depth texture (same size), or null for none.</param>
    /// <returns>The target.</returns>
    /// <exception cref="ArgumentException">The depth texture is not a depth format or has another size.</exception>
    public RenderTargetView WithDepth(GpuTexture depth)
    {
        if (depth == null) return new RenderTargetView(ColorView, ColorFormat, Width, Height);
        if (!TextureFormats.IsDepth(depth.Format)) throw new ArgumentException("The texture is " + depth.Format + ", not a depth format.", nameof(depth));
        if (depth.Width != Width || depth.Height != Height) throw new ArgumentException("The depth texture is " + depth.Width + " x " + depth.Height + "; the target is " + Width + " x " + Height + ".", nameof(depth));
        return new RenderTargetView(ColorView, ColorFormat, Width, Height, depth.View, depth.Format);
    }

    /// <summary>Describes the target.</summary>
    /// <returns>For example <c>640x480 BGRA8Unorm + Depth32Float</c>.</returns>
    public override string ToString() => Width + "x" + Height + " " + ColorFormat + (HasDepth ? " + " + DepthFormat : string.Empty);
}
