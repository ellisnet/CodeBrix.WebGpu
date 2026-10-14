namespace CodeBrix.WebGpu.Graphics.Lighting;

/// <summary>
/// The WGSL of the environment bake (equirectangular to cube, procedural sky, irradiance, GGX prefilter, BRDF LUT), the
/// skybox and the tone-map pass. Every bake pass draws one full-screen triangle into one face (layer) and mip of a
/// cube texture; the face texel's direction comes from <c>face_direction</c> (WebGPU cube layout: layers +X, -X, +Y,
/// -Y, +Z, -Z; texel row 0 at the top of each face).
/// </summary>
internal static class EnvironmentShaders
{
    /// <summary>Shared declarations: the bake parameters, the full-screen triangle, cube face directions, GGX helpers.</summary>
    private const string Common = @"
const PI: f32 = 3.14159265358979;

struct Bake {
    face: u32,
    count: u32,
    level: u32,
    size: u32,
    a: vec4<f32>,
    b: vec4<f32>,
    c: vec4<f32>,
    d: vec4<f32>,
};

@vertex
fn vs_main(@builtin(vertex_index) index: u32) -> @builtin(position) vec4<f32> {
    let uv = vec2<f32>(f32((index << 1u) & 2u), f32(index & 2u));
    return vec4<f32>(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0, 0.0, 1.0);
}

// The direction through a point of a cube face (uv 0..1, v down), WebGPU / Vulkan / D3D cube conventions.
fn face_direction(face: u32, uv: vec2<f32>) -> vec3<f32> {
    let s = uv.x * 2.0 - 1.0;
    let t = uv.y * 2.0 - 1.0;
    var d: vec3<f32>;
    switch face {
        case 0u: { d = vec3<f32>(1.0, -t, -s); }
        case 1u: { d = vec3<f32>(-1.0, -t, s); }
        case 2u: { d = vec3<f32>(s, 1.0, t); }
        case 3u: { d = vec3<f32>(s, -1.0, -t); }
        case 4u: { d = vec3<f32>(s, -t, 1.0); }
        default: { d = vec3<f32>(-s, -t, -1.0); }
    }
    return normalize(d);
}

fn hammersley(i: u32, count: u32) -> vec2<f32> {
    return vec2<f32>(f32(i) / f32(count), f32(reverseBits(i)) * 2.3283064365386963e-10);
}

// A GGX half vector around n for a uniform 2D sample (alpha = roughness squared).
fn importance_sample_ggx(xi: vec2<f32>, n: vec3<f32>, alpha: f32) -> vec3<f32> {
    let phi = 2.0 * PI * xi.x;
    let cos_theta = sqrt((1.0 - xi.y) / (1.0 + (alpha * alpha - 1.0) * xi.y));
    let sin_theta = sqrt(max(1.0 - cos_theta * cos_theta, 0.0));
    let h = vec3<f32>(cos(phi) * sin_theta, sin(phi) * sin_theta, cos_theta);
    let up = select(vec3<f32>(1.0, 0.0, 0.0), vec3<f32>(0.0, 0.0, 1.0), abs(n.z) < 0.999);
    let tangent = normalize(cross(up, n));
    let bitangent = cross(n, tangent);
    return normalize(tangent * h.x + bitangent * h.y + n * h.z);
}

fn distribution_ggx(n_dot_h: f32, alpha: f32) -> f32 {
    let alpha2 = alpha * alpha;
    let f = n_dot_h * n_dot_h * (alpha2 - 1.0) + 1.0;
    return alpha2 / (PI * f * f);
}
";

    /// <summary>Equirectangular panorama to one cube face (u from the heading, 0.5 at -Z; v from the elevation, 0 at the top).</summary>
    public const string Equirect = Common + @"
@group(0) @binding(0) var<uniform> bake: Bake;
@group(0) @binding(1) var panorama: texture_2d<f32>;
@group(0) @binding(2) var panorama_sampler: sampler;

@fragment
fn fs_main(@builtin(position) position: vec4<f32>) -> @location(0) vec4<f32> {
    let d = face_direction(bake.face, position.xy / f32(bake.size));
    let uv = vec2<f32>(0.5 + atan2(d.x, -d.z) / (2.0 * PI), 0.5 - asin(clamp(d.y, -1.0, 1.0)) / PI);
    return vec4<f32>(textureSampleLevel(panorama, panorama_sampler, uv, bake.a.x).rgb, 1.0);
}
";

    /// <summary>
    /// The procedural sky for one cube face; its parameters (colours, band exponent, sun direction and size, glow
    /// lobes, up and intensity) are in a second uniform buffer (<c>Sky</c>, see EnvironmentBaker).
    /// </summary>
    public const string ProceduralSky = Common + @"
struct Sky {
    zenith: vec4<f32>,
    horizon: vec4<f32>,
    ground: vec4<f32>,
    sun: vec4<f32>,
    sun_color: vec4<f32>,
    glow: vec4<f32>,
    up: vec4<f32>,
};

@group(0) @binding(0) var<uniform> bake: Bake;
@group(0) @binding(1) var<uniform> sky: Sky;

@fragment
fn fs_main(@builtin(position) position: vec4<f32>) -> @location(0) vec4<f32> {
    let d = face_direction(bake.face, position.xy / f32(bake.size));
    let h = dot(d, sky.up.xyz);
    let above = max(h, 0.0);
    let band = pow(1.0 - above, sky.horizon.w);
    let gradient = mix(sky.zenith.rgb, sky.horizon.rgb, band);
    let horizon_blend = smoothstep(-0.04, 0.01, h);
    let below = mix(sky.horizon.rgb * 0.6, sky.ground.rgb, smoothstep(0.0, 0.25, -h));
    var color = mix(below, gradient, horizon_blend);

    let cos_sun = dot(d, sky.sun.xyz);
    let c = max(cos_sun, 0.0);
    let glow = sky.glow.x * pow(c, sky.glow.y) + sky.glow.z * pow(c, sky.glow.w);
    color += sky.sun_color.rgb * glow * mix(0.15, 1.0, horizon_blend);
    color *= sky.up.w;

    // The disc, anti-aliased over about one texel (angle in radians).
    let texel = 1.5707963 / f32(bake.size);
    let angle = acos(clamp(cos_sun, -1.0, 1.0));
    let coverage = 1.0 - smoothstep(sky.sun.w - texel * 0.5, sky.sun.w + texel * 0.5, angle);
    color += sky.sun_color.rgb * sky.sun_color.w * coverage;
    return vec4<f32>(min(color, vec3<f32>(60000.0)), 1.0);
}
";

    /// <summary>
    /// Diffuse irradiance for one face texel: every texel of one radiance mip (the source, <c>bake.count</c> texels a
    /// side) weighted by its solid angle and the cosine; stores irradiance / PI (a white matte surface shows it).
    /// </summary>
    public const string Irradiance = Common + @"
@group(0) @binding(0) var<uniform> bake: Bake;
@group(0) @binding(1) var radiance: texture_2d_array<f32>;

@fragment
fn fs_main(@builtin(position) position: vec4<f32>) -> @location(0) vec4<f32> {
    let n = face_direction(bake.face, position.xy / f32(bake.size));
    let source = bake.count;
    let inverse = 1.0 / f32(source);
    var sum = vec3<f32>(0.0);
    var weight = 0.0;
    for (var face = 0u; face < 6u; face++) {
        for (var y = 0u; y < source; y++) {
            for (var x = 0u; x < source; x++) {
                let uv = (vec2<f32>(f32(x), f32(y)) + vec2<f32>(0.5)) * inverse;
                let st = uv * 2.0 - vec2<f32>(1.0);
                let l = face_direction(face, uv);
                let cosine = dot(n, l);
                if (cosine > 0.0) {
                    // The solid angle of a texel at (s, t) on a unit-distance face: dA / (1 + s^2 + t^2)^(3/2).
                    let r2 = 1.0 + dot(st, st);
                    let w = cosine / (r2 * sqrt(r2));
                    sum += textureLoad(radiance, vec2<i32>(i32(x), i32(y)), i32(face), i32(bake.level)).rgb * w;
                    weight += w;
                }
            }
        }
    }
    return vec4<f32>(sum / max(weight, 1e-8), 1.0);
}
";

    /// <summary>
    /// One mip of the GGX-prefiltered cube: a.x = roughness, a.y = mip-0 face size of the radiance cube, a.z = its last
    /// mip, a.w = the radiance lod matching this face size (used for roughness 0). Filtered importance sampling: each
    /// sample reads the radiance mip whose texel solid angle matches the sample's.
    /// </summary>
    public const string Prefilter = Common + @"
@group(0) @binding(0) var<uniform> bake: Bake;
@group(0) @binding(1) var radiance: texture_cube<f32>;
@group(0) @binding(2) var radiance_sampler: sampler;

@fragment
fn fs_main(@builtin(position) position: vec4<f32>) -> @location(0) vec4<f32> {
    let n = face_direction(bake.face, position.xy / f32(bake.size));
    let roughness = bake.a.x;
    if (roughness <= 0.0) {
        return vec4<f32>(textureSampleLevel(radiance, radiance_sampler, n, bake.a.w).rgb, 1.0);
    }
    let alpha = roughness * roughness;
    let texel_solid_angle = 4.0 * PI / (6.0 * bake.a.y * bake.a.y);
    var sum = vec3<f32>(0.0);
    var weight = 0.0;
    for (var i = 0u; i < bake.count; i++) {
        let h = importance_sample_ggx(hammersley(i, bake.count), n, alpha);
        let n_dot_h = max(dot(n, h), 0.0);
        let l = 2.0 * n_dot_h * h - n;
        let n_dot_l = dot(n, l);
        if (n_dot_l > 0.0) {
            // pdf(l) = D * n.h / (4 v.h) = D / 4 with v = n.
            let pdf = distribution_ggx(n_dot_h, alpha) * 0.25;
            let sample_solid_angle = 1.0 / (f32(bake.count) * pdf + 1e-6);
            let lod = clamp(0.5 * log2(sample_solid_angle / texel_solid_angle) + 1.0, 0.0, bake.a.z);
            sum += textureSampleLevel(radiance, radiance_sampler, l, lod).rgb * n_dot_l;
            weight += n_dot_l;
        }
    }
    return vec4<f32>(sum / max(weight, 1e-8), 1.0);
}
";

    /// <summary>
    /// The split-sum BRDF LUT: x = N.V, y = perceptual roughness (texel centres); R = scale and G = bias of F0, from
    /// GGX importance sampling with the height-correlated Smith visibility (the model shader's direct-light terms).
    /// </summary>
    public const string BrdfLut = Common + @"
const SAMPLES: u32 = 4096u;

@group(0) @binding(0) var<uniform> bake: Bake;

fn visibility_smith(n_dot_l: f32, n_dot_v: f32, alpha: f32) -> f32 {
    let alpha2 = alpha * alpha;
    let ggx_v = n_dot_l * sqrt(n_dot_v * n_dot_v * (1.0 - alpha2) + alpha2);
    let ggx_l = n_dot_v * sqrt(n_dot_l * n_dot_l * (1.0 - alpha2) + alpha2);
    let sum = ggx_v + ggx_l;
    return select(0.0, 0.5 / sum, sum > 0.0);
}

@fragment
fn fs_main(@builtin(position) position: vec4<f32>) -> @location(0) vec4<f32> {
    let n_dot_v = position.x / f32(bake.size);
    let roughness = position.y / f32(bake.size);
    let alpha = max(roughness * roughness, 0.0001);
    let v = vec3<f32>(sqrt(1.0 - n_dot_v * n_dot_v), 0.0, n_dot_v);
    let n = vec3<f32>(0.0, 0.0, 1.0);
    var scale = 0.0;
    var bias = 0.0;
    for (var i = 0u; i < SAMPLES; i++) {
        let h = importance_sample_ggx(hammersley(i, SAMPLES), n, alpha);
        let v_dot_h = dot(v, h);
        let l = 2.0 * v_dot_h * h - v;
        let n_dot_l = l.z;
        let n_dot_h = max(h.z, 1e-6);
        if (n_dot_l > 0.0 && v_dot_h > 0.0) {
            let g = visibility_smith(n_dot_l, n_dot_v, alpha) * 4.0 * n_dot_l * v_dot_h / n_dot_h;
            let fc = pow(1.0 - v_dot_h, 5.0);
            scale += (1.0 - fc) * g;
            bias += fc * g;
        }
    }
    return vec4<f32>(scale / f32(SAMPLES), bias / f32(SAMPLES), 0.0, 1.0);
}
";

    /// <summary>The skybox: a full-screen triangle at the far plane; the view ray from the inverse view-projection.</summary>
    public const string Skybox = @"
struct Sky {
    inverse_view_projection: mat4x4<f32>,
    params: vec4<f32>,
    lods: vec4<f32>,
};

@group(0) @binding(0) var<uniform> sky: Sky;
@group(0) @binding(1) var radiance: texture_cube<f32>;
@group(0) @binding(2) var prefiltered: texture_cube<f32>;
@group(0) @binding(3) var sky_sampler: sampler;

struct VertexOutput {
    @builtin(position) clip: vec4<f32>,
    @location(0) ndc: vec2<f32>,
};

@vertex
fn vs_main(@builtin(vertex_index) index: u32) -> VertexOutput {
    let uv = vec2<f32>(f32((index << 1u) & 2u), f32(index & 2u));
    let ndc = vec2<f32>(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0);
    var output: VertexOutput;
    output.clip = vec4<f32>(ndc, 1.0, 1.0);
    output.ndc = ndc;
    return output;
}

fn encode_srgb(value: vec3<f32>) -> vec3<f32> {
    let c = clamp(value, vec3<f32>(0.0), vec3<f32>(1.0));
    let low = c * 12.92;
    let high = 1.055 * pow(c, vec3<f32>(1.0 / 2.4)) - vec3<f32>(0.055);
    return select(high, low, c <= vec3<f32>(0.0031308));
}

@fragment
fn fs_main(input: VertexOutput) -> @location(0) vec4<f32> {
    let near = sky.inverse_view_projection * vec4<f32>(input.ndc, 0.0, 1.0);
    let far = sky.inverse_view_projection * vec4<f32>(input.ndc, 1.0, 1.0);
    let world = normalize(far.xyz / far.w - near.xyz / near.w);
    // Rotate the world direction into the environment (the environment turns by params.y about +Y).
    let c = cos(sky.params.y);
    let s = sin(sky.params.y);
    let d = vec3<f32>(c * world.x - s * world.z, world.y, s * world.x + c * world.z);
    let sharp = textureSampleLevel(radiance, sky_sampler, d, sky.lods.x).rgb;
    let blurred = textureSampleLevel(prefiltered, sky_sampler, d, sky.lods.y).rgb;
    let color = select(sharp, blurred, sky.lods.z > 0.5) * sky.params.x * sky.params.z;
    if (sky.params.w > 0.5) {
        return vec4<f32>(encode_srgb(color), 1.0);
    }
    return vec4<f32>(color, 1.0);
}
";

    /// <summary>The tone-map pass: exposure, an operator, then sRGB encoding for UNORM targets (params.z = 1).</summary>
    public const string ToneMap = @"
struct Params {
    params: vec4<f32>,
    source: vec4<f32>,
};

@group(0) @binding(0) var<uniform> tone: Params;
@group(0) @binding(1) var hdr: texture_2d<f32>;
@group(0) @binding(2) var hdr_sampler: sampler;

@vertex
fn vs_main(@builtin(vertex_index) index: u32) -> @builtin(position) vec4<f32> {
    let uv = vec2<f32>(f32((index << 1u) & 2u), f32(index & 2u));
    return vec4<f32>(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0, 0.0, 1.0);
}

fn encode_srgb(value: vec3<f32>) -> vec3<f32> {
    let c = clamp(value, vec3<f32>(0.0), vec3<f32>(1.0));
    let low = c * 12.92;
    let high = 1.055 * pow(c, vec3<f32>(1.0 / 2.4)) - vec3<f32>(0.055);
    return select(high, low, c <= vec3<f32>(0.0031308));
}

fn aces_narkowicz(x: vec3<f32>) -> vec3<f32> {
    return clamp(x * (2.51 * x + vec3<f32>(0.03)) / (x * (2.43 * x + vec3<f32>(0.59)) + vec3<f32>(0.14)), vec3<f32>(0.0), vec3<f32>(1.0));
}

// Stephen Hill's fit of the ACES RRT + ODT (sRGB primaries in, sRGB primaries out).
fn aces_fitted(color: vec3<f32>) -> vec3<f32> {
    let input = mat3x3<f32>(
        vec3<f32>(0.59719, 0.07600, 0.02840),
        vec3<f32>(0.35458, 0.90834, 0.13383),
        vec3<f32>(0.04823, 0.01566, 0.83777));
    let output = mat3x3<f32>(
        vec3<f32>(1.60475, -0.10208, -0.00327),
        vec3<f32>(-0.53108, 1.10813, -0.07276),
        vec3<f32>(-0.07367, -0.00605, 1.07602));
    let v = input * color;
    let a = v * (v + vec3<f32>(0.0245786)) - vec3<f32>(0.000090537);
    let b = v * (0.983729 * v + vec3<f32>(0.4329510)) + vec3<f32>(0.238081);
    return clamp(output * (a / b), vec3<f32>(0.0), vec3<f32>(1.0));
}

// Khronos PBR Neutral (Khronos 3D Commerce reference).
fn pbr_neutral(input: vec3<f32>) -> vec3<f32> {
    let start_compression = 0.8 - 0.04;
    let desaturation = 0.15;
    var color = input;
    let x = min(color.r, min(color.g, color.b));
    let offset = select(0.04, x - 6.25 * x * x, x < 0.08);
    color -= vec3<f32>(offset);
    let peak = max(color.r, max(color.g, color.b));
    if (peak < start_compression) {
        return color;
    }
    let d = 1.0 - start_compression;
    let new_peak = 1.0 - d * d / (peak + d - start_compression);
    color *= new_peak / peak;
    let g = 1.0 - 1.0 / (desaturation * (peak - new_peak) + 1.0);
    return mix(color, vec3<f32>(new_peak), g);
}

@fragment
fn fs_main(@builtin(position) position: vec4<f32>) -> @location(0) vec4<f32> {
    let uv = position.xy * tone.source.zw;
    let sampled = textureSampleLevel(hdr, hdr_sampler, uv, 0.0);
    let exposed = max(sampled.rgb * tone.params.x, vec3<f32>(0.0));
    var mapped: vec3<f32>;
    switch u32(tone.params.y) {
        case 0u: { mapped = aces_fitted(exposed); }
        case 1u: { mapped = pbr_neutral(exposed); }
        case 2u: { mapped = exposed / (vec3<f32>(1.0) + exposed); }
        case 3u: { mapped = aces_narkowicz(exposed); }
        default: { mapped = min(exposed, vec3<f32>(1.0)); }
    }
    mapped = clamp(mapped, vec3<f32>(0.0), vec3<f32>(1.0));
    if (tone.params.z > 0.5) {
        return vec4<f32>(encode_srgb(mapped), sampled.a);
    }
    return vec4<f32>(mapped, sampled.a);
}
";
}
