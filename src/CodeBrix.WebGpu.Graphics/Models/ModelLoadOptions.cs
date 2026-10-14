using System;
using System.Collections.Generic;

namespace CodeBrix.WebGpu.Graphics;

/// <summary>How strictly a glTF file is validated while it is read.</summary>
public enum ModelValidation
{
    /// <summary>Full validation; any problem fails the load.</summary>
    Strict,

    /// <summary>Validate, repairing what can be repaired (the default).</summary>
    TryFix,

    /// <summary>No validation (fastest; for files you trust).</summary>
    Skip,
}

/// <summary>Options for <see cref="GraphicsContext.LoadModel(string, ModelLoadOptions)" />.</summary>
public sealed class ModelLoadOptions
{
    /// <summary>True (default) to decode and upload the material textures.</summary>
    public bool LoadTextures { get; set; } = true;

    /// <summary>True (default) for GPU-generated mipmaps on every texture.</summary>
    public bool Mipmaps { get; set; } = true;

    /// <summary>
    /// True (default) to create colour textures (base colour, emissive) as sRGB, as glTF specifies; false creates them
    /// linear (sampling returns the stored values - for renderers that work in gamma space). Data textures (normal,
    /// metallic-roughness, occlusion) are always linear.
    /// </summary>
    public bool ColorTexturesSrgb { get; set; } = true;

    /// <summary>True (default) to generate tangents for primitives whose material has a normal map and whose file has no TANGENT.</summary>
    public bool GenerateTangents { get; set; } = true;

    /// <summary>True (default) to decode the images of a model on several threads before uploading them.</summary>
    public bool DecodeTexturesInParallel { get; set; } = true;

    /// <summary>The validation level. Default <see cref="ModelValidation.TryFix" />.</summary>
    public ModelValidation Validation { get; set; } = ModelValidation.TryFix;

    /// <summary>
    /// A cache shared by several loads: an external image file (for example a kit's shared colormap.png) is decoded and
    /// uploaded once and reused by every model that names it. The cache owns those textures; disposing a model leaves
    /// them alone. Null (the default) = every model owns its textures.
    /// </summary>
    public ModelTextureCache TextureCache { get; set; }

    /// <summary>A label for the model's GPU objects, or null (the file name).</summary>
    public string Label { get; set; }
}

/// <summary>
/// Textures decoded from external image files, shared between <see cref="Model" />s loaded with the same cache
/// (<see cref="ModelLoadOptions.TextureCache" />). Owns its textures: dispose it after the models that use them.
/// Not thread-safe.
/// </summary>
public sealed class ModelTextureCache : IDisposable
{
    private readonly Dictionary<(string Path, TextureColorSpace ColorSpace, bool Mipmaps), GpuTexture> textures = new Dictionary<(string, TextureColorSpace, bool), GpuTexture>(new KeyComparer());

    /// <summary>The number of cached textures.</summary>
    public int Count => textures.Count;

    /// <summary>Disposes every cached texture and empties the cache.</summary>
    public void Dispose()
    {
        foreach (GpuTexture texture in textures.Values) texture.Dispose();
        textures.Clear();
    }

    /// <summary>Looks a texture up.</summary>
    /// <param name="path">The full path of the image file.</param>
    /// <param name="colorSpace">The colour space.</param>
    /// <param name="mipmaps">Whether it has mipmaps.</param>
    /// <param name="texture">The texture.</param>
    /// <returns>True when cached (and not disposed).</returns>
    internal bool TryGet(string path, TextureColorSpace colorSpace, bool mipmaps, out GpuTexture texture) =>
        textures.TryGetValue((path, colorSpace, mipmaps), out texture) && !texture.IsDisposed;

    /// <summary>Adds a texture.</summary>
    /// <param name="path">The full path.</param>
    /// <param name="colorSpace">The colour space.</param>
    /// <param name="mipmaps">Whether it has mipmaps.</param>
    /// <param name="texture">The texture.</param>
    internal void Add(string path, TextureColorSpace colorSpace, bool mipmaps, GpuTexture texture) => textures[(path, colorSpace, mipmaps)] = texture;

    private sealed class KeyComparer : IEqualityComparer<(string Path, TextureColorSpace ColorSpace, bool Mipmaps)>
    {
        public bool Equals((string Path, TextureColorSpace ColorSpace, bool Mipmaps) x, (string Path, TextureColorSpace ColorSpace, bool Mipmaps) y) =>
            string.Equals(x.Path, y.Path, StringComparison.Ordinal) && x.ColorSpace == y.ColorSpace && x.Mipmaps == y.Mipmaps;

        public int GetHashCode((string Path, TextureColorSpace ColorSpace, bool Mipmaps) value) =>
            HashCode.Combine(StringComparer.Ordinal.GetHashCode(value.Path), value.ColorSpace, value.Mipmaps);
    }
}
