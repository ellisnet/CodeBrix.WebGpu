using System.Collections.Generic;
using System.Numerics;

namespace CodeBrix.WebGpu.Graphics;

/// <summary>
/// One texture reference of a <see cref="ModelMaterial" />: the GPU texture, its sampler, the texture-coordinate set
/// and the KHR_texture_transform (identity when absent; its texCoord override is already applied to
/// <see cref="TexCoord" />).
/// </summary>
public readonly struct ModelTextureSlot
{
    /// <summary>Creates a slot.</summary>
    /// <param name="texture">The texture.</param>
    /// <param name="sampler">The sampler.</param>
    /// <param name="texCoord">The texture-coordinate set.</param>
    /// <param name="transform">The transform.</param>
    /// <param name="hasTransform">True when the file gave a transform.</param>
    /// <param name="textureIndex">The glTF texture index.</param>
    internal ModelTextureSlot(GpuTexture texture, GpuSampler sampler, int texCoord, Matrix3x2 transform, bool hasTransform, int textureIndex)
    {
        Texture = texture;
        Sampler = sampler;
        TexCoord = texCoord;
        Transform = transform;
        HasTransform = hasTransform;
        TextureIndex = textureIndex;
    }

    /// <summary>The texture, or null when the slot is empty (or its image could not be decoded - see <see cref="Model.Warnings" />).</summary>
    public GpuTexture Texture { get; }

    /// <summary>The sampler (wrap modes and filters from the glTF sampler).</summary>
    public GpuSampler Sampler { get; }

    /// <summary>The texture-coordinate set (0 = TEXCOORD_0, 1 = TEXCOORD_1).</summary>
    public int TexCoord { get; }

    /// <summary>The texture-coordinate transform (<c>Vector2.Transform(uv, Transform)</c>), identity when none.</summary>
    public Matrix3x2 Transform { get; }

    /// <summary>True when the file gave a KHR_texture_transform.</summary>
    public bool HasTransform { get; }

    /// <summary>The glTF texture index, or -1 for an empty slot.</summary>
    public int TextureIndex { get; }

    /// <summary>True when the slot names a texture.</summary>
    public bool IsPresent => TextureIndex >= 0;

    /// <summary>The empty slot.</summary>
    internal static ModelTextureSlot Empty => new ModelTextureSlot(null, null, 0, Matrix3x2.Identity, false, -1);
}

/// <summary>
/// A glTF material, captured completely as metallic-roughness parameters (immutable). Renderers read what they
/// support; <see cref="ApproximatedFeatures" /> lists the extensions that are captured but only approximated.
/// </summary>
public sealed class ModelMaterial
{
    internal ModelMaterial(int index, string name)
    {
        Index = index;
        Name = name;
    }

    /// <summary>The index in <see cref="Model.Materials" />, or -1 for the glTF default material.</summary>
    public int Index { get; }

    /// <summary>The name, or null.</summary>
    public string Name { get; }

    /// <summary>baseColorFactor (linear RGBA). Default white.</summary>
    public Vector4 BaseColorFactor { get; internal set; } = Vector4.One;

    /// <summary>baseColorTexture (sRGB texture).</summary>
    public ModelTextureSlot BaseColorTexture { get; internal set; } = ModelTextureSlot.Empty;

    /// <summary>metallicFactor. Default 1.</summary>
    public float MetallicFactor { get; internal set; } = 1f;

    /// <summary>roughnessFactor. Default 1.</summary>
    public float RoughnessFactor { get; internal set; } = 1f;

    /// <summary>metallicRoughnessTexture (linear; roughness in G, metallic in B).</summary>
    public ModelTextureSlot MetallicRoughnessTexture { get; internal set; } = ModelTextureSlot.Empty;

    /// <summary>normalTexture (linear, tangent space).</summary>
    public ModelTextureSlot NormalTexture { get; internal set; } = ModelTextureSlot.Empty;

    /// <summary>normalTexture.scale. Default 1.</summary>
    public float NormalScale { get; internal set; } = 1f;

    /// <summary>occlusionTexture (linear; occlusion in R).</summary>
    public ModelTextureSlot OcclusionTexture { get; internal set; } = ModelTextureSlot.Empty;

    /// <summary>occlusionTexture.strength. Default 1.</summary>
    public float OcclusionStrength { get; internal set; } = 1f;

    /// <summary>emissiveFactor (linear RGB). Default black.</summary>
    public Vector3 EmissiveFactor { get; internal set; }

    /// <summary>KHR_materials_emissive_strength. Default 1.</summary>
    public float EmissiveStrength { get; internal set; } = 1f;

    /// <summary>emissiveTexture (sRGB).</summary>
    public ModelTextureSlot EmissiveTexture { get; internal set; } = ModelTextureSlot.Empty;

    /// <summary>alphaMode (OPAQUE, MASK, BLEND). Default <see cref="MaterialAlphaMode.Opaque" />.</summary>
    public MaterialAlphaMode AlphaMode { get; internal set; } = MaterialAlphaMode.Opaque;

    /// <summary>alphaCutoff (used by MASK). Default 0.5.</summary>
    public float AlphaCutoff { get; internal set; } = 0.5f;

    /// <summary>doubleSided. Default false.</summary>
    public bool DoubleSided { get; internal set; }

    /// <summary>KHR_materials_unlit: the base colour is shown without lighting.</summary>
    public bool Unlit { get; internal set; }

    /// <summary>KHR_materials_ior. Default 1.5.</summary>
    public float IndexOfRefraction { get; internal set; } = 1.5f;

    /// <summary>KHR_materials_clearcoat factor (0 = none). Approximated.</summary>
    public float ClearCoatFactor { get; internal set; }

    /// <summary>KHR_materials_clearcoat roughness. Approximated.</summary>
    public float ClearCoatRoughness { get; internal set; }

    /// <summary>KHR_materials_specular factor. Default 1. Approximated.</summary>
    public float SpecularFactor { get; internal set; } = 1f;

    /// <summary>KHR_materials_specular colour. Default white. Approximated.</summary>
    public Vector3 SpecularColor { get; internal set; } = Vector3.One;

    /// <summary>KHR_materials_transmission factor (0 = none). Approximated.</summary>
    public float TransmissionFactor { get; internal set; }

    /// <summary>The extensions on this material that are captured but only approximated by the renderers (for example "KHR_materials_clearcoat").</summary>
    public IReadOnlyList<string> ApproximatedFeatures { get; internal set; } = System.Array.Empty<string>();

    /// <summary>True when the base colour is alpha-blended.</summary>
    public bool IsTranslucent => AlphaMode == MaterialAlphaMode.Blend;

    /// <summary>The name and index.</summary>
    /// <returns>For example <c>ModelMaterial 2 'colormap'</c>.</returns>
    public override string ToString() => "ModelMaterial " + Index + (Name == null ? string.Empty : " '" + Name + "'");
}
