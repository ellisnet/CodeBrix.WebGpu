namespace CodeBrix.WebGpu.Graphics;

/// <summary>How the colour values of an image are interpreted when it becomes a texture.</summary>
public enum TextureColorSpace
{
    /// <summary>
    /// The image holds colours (photographs, colour maps, base-colour and emissive textures): the texture is
    /// <see cref="Native.TextureFormat.RGBA8UnormSrgb" />, so sampling returns LINEAR values and filtering and mipmaps
    /// happen in linear light. Render into an sRGB target (for example an RGBA8UnormSrgb headless target) to see the
    /// colours as authored.
    /// </summary>
    Srgb,

    /// <summary>
    /// The image holds data (normal maps, metallic-roughness, occlusion, masks, lookup tables), or colours you want
    /// sampled exactly as stored: the texture is <see cref="Native.TextureFormat.RGBA8Unorm" />.
    /// </summary>
    Linear,
}

/// <summary>Options for creating a texture from an image (<see cref="GraphicsContext.CreateTextureFromFile" /> and its siblings).</summary>
public sealed class TextureLoadOptions
{
    /// <summary>The defaults: a full GPU-generated mip chain, sRGB, straight alpha.</summary>
    public static TextureLoadOptions Default => new TextureLoadOptions();

    /// <summary>Defaults for data maps (normal, metallic-roughness, occlusion): mipmaps, linear, straight alpha.</summary>
    public static TextureLoadOptions Data => new TextureLoadOptions { ColorSpace = TextureColorSpace.Linear };

    /// <summary>True (default) for a full mip chain, generated on the GPU after the upload (<see cref="GraphicsContext.GenerateMipmaps" />).</summary>
    public bool Mipmaps { get; set; } = true;

    /// <summary>The colour space; default <see cref="TextureColorSpace.Srgb" />.</summary>
    public TextureColorSpace ColorSpace { get; set; } = TextureColorSpace.Srgb;

    /// <summary>
    /// True to multiply red, green and blue by alpha while uploading (for <see cref="BlendPreset.PremultipliedAlpha" />
    /// drawing; mipmaps of premultiplied pixels have no dark fringes). The multiplication works on the stored 8-bit
    /// values (for sRGB images that is the common approximation, not a linear-light multiply). Default false.
    /// </summary>
    public bool PremultiplyAlpha { get; set; }

    /// <summary>Extra usages added to TextureBinding | CopyDst | CopySrc (and RenderAttachment when mipmaps are generated). Default none.</summary>
    public Native.TextureUsage ExtraUsage { get; set; }

    /// <summary>A label, or null (the file name when loading from a file).</summary>
    public string Label { get; set; }

    /// <summary>The texture format the options produce.</summary>
    internal Native.TextureFormat Format => ColorSpace == TextureColorSpace.Srgb ? Native.TextureFormat.RGBA8UnormSrgb : Native.TextureFormat.RGBA8Unorm;
}
