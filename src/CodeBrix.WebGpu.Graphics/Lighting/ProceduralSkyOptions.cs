using System.Numerics;

namespace CodeBrix.WebGpu.Graphics;

/// <summary>
/// A simple analytic sky for <see cref="GraphicsContext.CreateProceduralSky" />: a zenith-to-horizon gradient, a
/// ground colour below the horizon, a haze band and glow around the sun controlled by <see cref="Turbidity" />, and an
/// optional sun disc. The colours are linear radiance (a white matte surface under a uniform sky of grey g shows g).
/// The defaults are the sky <see cref="GraphicsContext.DefaultEnvironment" /> uses: a clear day whose light suits the
/// default <see cref="SceneLighting" /> key light (the sun direction matches it).
/// </summary>
/// <remarks>
/// The sun is usually lit by a directional light (<see cref="SceneLighting.LightDirection" />): keep
/// <see cref="SunIntensity" /> at 0 and point the light along <see cref="SunDirection" />. To light a scene from the
/// environment alone, switch the key light off and give the sun an intensity instead (the same unit as a directional
/// light's intensity).
/// </remarks>
public sealed class ProceduralSkyOptions
{
    /// <summary>The defaults (a fresh instance each time).</summary>
    public static ProceduralSkyOptions Default => new ProceduralSkyOptions();

    /// <summary>
    /// The direction the sunlight TRAVELS (from the sun into the scene), the same convention as
    /// <see cref="SceneLighting.LightDirection" />. Default the default key light's direction (a high sun to the
    /// front-left).
    /// </summary>
    public Vector3 SunDirection { get; set; } = Vector3.Normalize(new Vector3(-0.4f, -1f, -0.5f));

    /// <summary>The sun's colour (linear). Default a warm white.</summary>
    public Color4 SunColor { get; set; } = new Color4(1f, 0.95f, 0.86f);

    /// <summary>
    /// The light the sun disc adds to the environment, in directional-light units (1 = a white matte surface facing
    /// the sun shows white from the disc alone). Default 0: the sun is a bright glow only and a directional light plays
    /// the sun.
    /// </summary>
    public float SunIntensity { get; set; }

    /// <summary>The sun disc's angular radius in radians (0.002 to 0.2). Default 0.0175 (one degree; a little larger than the real sun so it survives the cube resolution).</summary>
    public float SunAngularRadius { get; set; } = 0.0175f;

    /// <summary>The strength of the glow around the sun (0 = none). Default 1.</summary>
    public float SunGlow { get; set; } = 1f;

    /// <summary>The sky colour straight up (linear). Default a clear blue.</summary>
    public Color4 ZenithColor { get; set; } = new Color4(0.12f, 0.24f, 0.52f);

    /// <summary>The sky colour at the horizon (linear). Default a pale blue-white.</summary>
    public Color4 HorizonColor { get; set; } = new Color4(0.56f, 0.64f, 0.74f);

    /// <summary>The colour below the horizon (linear; the light bounced from the ground). Default a dark warm grey.</summary>
    public Color4 GroundColor { get; set; } = new Color4(0.16f, 0.14f, 0.12f);

    /// <summary>
    /// Haze, 1 (very clear: a thin horizon band, deep zenith) to 10 (hazy: a wide pale band, a large soft sun glow).
    /// Default 2.5.
    /// </summary>
    public float Turbidity { get; set; } = 2.5f;

    /// <summary>Scales the whole sky (gradient, ground and glow; not the sun disc's <see cref="SunIntensity" />). Default 1.</summary>
    public float Intensity { get; set; } = 1f;

    /// <summary>The direction that is "up" for the sky. Default +Y.</summary>
    public Vector3 Up { get; set; } = Vector3.UnitY;
}
