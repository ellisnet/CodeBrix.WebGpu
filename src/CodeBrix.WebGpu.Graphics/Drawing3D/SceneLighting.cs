using System.Collections.Generic;
using System.Numerics;

namespace CodeBrix.WebGpu.Graphics;

/// <summary>
/// The lighting a <see cref="MeshRenderer" /> or <see cref="ModelRenderer" /> applies: a key directional light, any
/// number of additional directional lights (the model renderer shades up to <see cref="MaxDirectionalLights" /> in
/// total; the mesh renderer uses the key light only), an ambient term and an exposure. For the model renderer the
/// ambient term is image-based by default (<see cref="Ambient" /> = <see cref="AmbientLighting.Environment" />): the
/// <see cref="Environment" /> you set, or the context's procedural <see cref="GraphicsContext.DefaultEnvironment" />
/// sky; <see cref="AmbientLighting.Hemisphere" /> switches to a cheap sky/ground colour gradient instead.
/// </summary>
/// <remarks>
/// Units (model renderer): a light of intensity 1 makes a white matte surface that faces it show white, and the
/// ambient colours are the radiance of the sky and ground hemispheres (a white matte surface under a uniform ambient
/// of grey g shows g); an environment's values are radiance in the same unit. Everything the model renderer computes is
/// linear and multiplied by <see cref="Exposure" /> before it is written to the target.
/// </remarks>
public sealed class SceneLighting
{
    /// <summary>The most directional lights the model renderer shades (the key light plus seven additional ones).</summary>
    public const int MaxDirectionalLights = 8;

    /// <summary>The direction the key light TRAVELS (from the light into the scene). Default a sun high to the front-left.</summary>
    public Vector3 LightDirection { get; set; } = Vector3.Normalize(new Vector3(-0.4f, -1f, -0.5f));

    /// <summary>The key light's colour (linear). Default white.</summary>
    public Color4 LightColor { get; set; } = Color4.White;

    /// <summary>Scales the key light's colour; 0 switches it off. Default 1.</summary>
    public float LightIntensity { get; set; } = 1f;

    /// <summary>
    /// More directional lights (model renderer only; lights beyond <see cref="MaxDirectionalLights" /> in total are
    /// ignored). Empty by default.
    /// </summary>
    public List<DirectionalLight> AdditionalLights { get; } = new List<DirectionalLight>();

    /// <summary>
    /// The ambient light (linear). For the mesh renderer it is added everywhere; for the model renderer it is the
    /// colour of the upper (sky) hemisphere of the hemispheric ambient (used only when <see cref="Ambient" /> is
    /// <see cref="AmbientLighting.Hemisphere" />). Default 25% grey.
    /// </summary>
    public Color4 AmbientColor { get; set; } = new Color4(0.25f, 0.25f, 0.25f);

    /// <summary>
    /// The colour of the lower (ground) hemisphere of the model renderer's hemispheric ambient, or null for the same
    /// colour as <see cref="AmbientColor" /> (a uniform ambient). Default null.
    /// </summary>
    public Color4? AmbientGroundColor { get; set; }

    /// <summary>The direction the sky hemisphere is centred on (model renderer). Default +Y.</summary>
    public Vector3 AmbientUp { get; set; } = Vector3.UnitY;

    /// <summary>Scales everything the model renderer outputs (lit, emissive and unlit colours alike). Default 1.</summary>
    public float Exposure { get; set; } = 1f;

    /// <summary>
    /// Where the model renderer's ambient (indirect) light comes from. Default
    /// <see cref="AmbientLighting.Environment" />: image-based lighting from <see cref="Environment" /> (or the context's
    /// <see cref="GraphicsContext.DefaultEnvironment" /> when that is null). The mesh renderer always uses
    /// <see cref="AmbientColor" />.
    /// </summary>
    public AmbientLighting Ambient { get; set; } = AmbientLighting.Environment;

    /// <summary>
    /// The environment for image-based lighting (model renderer, <see cref="Ambient" /> =
    /// <see cref="AmbientLighting.Environment" />) and for <see cref="SkyboxRenderer.Draw(RenderPass, Camera, SceneLighting)" />;
    /// null = the context's <see cref="GraphicsContext.DefaultEnvironment" /> (a procedural sky). Not owned: dispose it
    /// yourself after the last frame that uses it. Default null.
    /// </summary>
    public EnvironmentMap Environment { get; set; }

    /// <summary>Scales the environment's light (diffuse and specular) and the skybox drawn from it. Default 1.</summary>
    public float EnvironmentIntensity { get; set; } = 1f;

    /// <summary>
    /// Turns the environment about +Y, in radians (right-handed: a positive angle turns the panorama's centre from -Z
    /// towards -X). Applies to the lighting and the skybox alike; free to change every frame. Default 0.
    /// </summary>
    public float EnvironmentRotation { get; set; }
}

/// <summary>Where the model renderer's ambient (indirect) light comes from.</summary>
public enum AmbientLighting
{
    /// <summary>
    /// Image-based lighting: diffuse irradiance and GGX-prefiltered specular reflections from
    /// <see cref="SceneLighting.Environment" /> (or the context's default procedural sky), with the split-sum BRDF LUT
    /// and multiple-scattering energy compensation. The default.
    /// </summary>
    Environment,

    /// <summary>
    /// A colour gradient between <see cref="SceneLighting.AmbientColor" /> (sky) and
    /// <see cref="SceneLighting.AmbientGroundColor" /> (ground) around <see cref="SceneLighting.AmbientUp" />, with an
    /// analytic environment-BRDF fit for the specular part. No environment textures are read; set both colours to black
    /// for direct light only.
    /// </summary>
    Hemisphere,
}

/// <summary>A directional light: a direction it travels in, a linear colour and an intensity.</summary>
public struct DirectionalLight
{
    /// <summary>Creates a light.</summary>
    /// <param name="direction">The direction the light TRAVELS (from the light into the scene).</param>
    /// <param name="color">The colour (linear).</param>
    /// <param name="intensity">Scales the colour.</param>
    public DirectionalLight(Vector3 direction, Color4 color, float intensity = 1f)
    {
        Direction = direction;
        Color = color;
        Intensity = intensity;
    }

    /// <summary>The direction the light travels.</summary>
    public Vector3 Direction { get; set; }

    /// <summary>The colour (linear).</summary>
    public Color4 Color { get; set; }

    /// <summary>Scales the colour.</summary>
    public float Intensity { get; set; }
}
