using System;
using System.Text;

namespace CodeBrix.WebGpu.Graphics.Shaders;

/// <summary>What a variant of the model shader compiles in (one shader module per combination).</summary>
[Flags]
internal enum PbrFeatures
{
    /// <summary>Nothing beyond the base metallic-roughness shading.</summary>
    None = 0,

    /// <summary>GPU skinning from the skin stream and the instance's joint palette.</summary>
    Skinned = 1 << 0,

    /// <summary>Morph targets from the model's delta buffer and the instance's weights.</summary>
    Morphed = 1 << 1,

    /// <summary>A normal map (tangent frame from TANGENT, else from screen-space derivatives).</summary>
    NormalMap = 1 << 2,

    /// <summary>KHR_materials_unlit: the base colour is the output.</summary>
    Unlit = 1 << 3,

    /// <summary>Alpha mode MASK: discard below the cutoff, write alpha 1.</summary>
    AlphaMask = 1 << 4,

    /// <summary>Alpha mode BLEND: keep alpha.</summary>
    AlphaBlend = 1 << 5,

    /// <summary>doubleSided: back faces shade with flipped normals.</summary>
    DoubleSided = 1 << 6,

    /// <summary>KHR_materials_clearcoat (approximated: a second GGX lobe on the geometric normal).</summary>
    ClearCoat = 1 << 7,

    /// <summary>The target is a non-sRGB 8-bit format: the shader encodes linear to sRGB itself.</summary>
    EncodeSrgb = 1 << 8,

    /// <summary>
    /// Pipeline-only (no shader change): the draw's model matrix has a negative determinant (a mirroring node or
    /// instance transform), so clockwise triangles are the front faces (glTF 2.0 rule).
    /// </summary>
    Mirrored = 1 << 9,
}

/// <summary>
/// The model renderer's metallic-roughness shader (glTF 2.0 reference behaviour: Lambert diffuse, GGX distribution,
/// height-correlated Smith visibility, Schlick Fresnel). Variants are specialised by WGSL <c>const</c>s prepended to
/// one source (<see cref="Source" />).
/// </summary>
/// <remarks>
/// <para>Bind groups (one pipeline layout for every variant):</para>
/// <list type="bullet">
/// <item>0 - frame: 0 frame uniforms (dynamic offset; camera, exposure, hemispheric ambient, up to 8 directional
/// lights, environment switch, intensity, prefiltered mip count and rotation about +Y), 1 diffuse irradiance cube, 2
/// prefiltered specular cube, 3 BRDF LUT (2D, RG = scale and bias of F0), 4 environment sampler. With the hemispheric
/// ambient the three environment textures are 1 x 1 placeholders; the shader reads them only when
/// <c>frame.environment.x</c> is 1 (image-based lighting replaces the hemispheric ambient, nothing else changes).</item>
/// <item>1 - material: 0 material uniforms (256 bytes), 1-5 base colour, metallic-roughness, normal, occlusion and
/// emissive textures, 6-10 their samplers. Missing textures are a white texture, so the factors stand alone.</item>
/// <item>2 - instances (per frame, read-only storage): 0 instance records (world matrix, node, palette and weight
/// bases), 1 matrices (each instance's node matrices followed by its joint palettes), 2 morph weights (weight minus
/// the weight baked into the vertex buffer).</item>
/// <item>3 - model: 0 draw uniforms (dynamic offset, one slot per draw item: node, vertex bases, skin and morph
/// layout), 1 the model's skin stream (24 bytes per vertex), 2 the model's morph deltas (vec4 per channel).</item>
/// </list>
/// </remarks>
internal static class PbrShader
{
    /// <summary>The source of one variant.</summary>
    /// <param name="features">The features.</param>
    /// <returns>The WGSL.</returns>
    public static string Source(PbrFeatures features)
    {
        StringBuilder builder = new StringBuilder(Body.Length + 512);
        Append(builder, "SKINNED", features, PbrFeatures.Skinned);
        Append(builder, "MORPHED", features, PbrFeatures.Morphed);
        Append(builder, "NORMAL_MAP", features, PbrFeatures.NormalMap);
        Append(builder, "UNLIT", features, PbrFeatures.Unlit);
        Append(builder, "ALPHA_MASK", features, PbrFeatures.AlphaMask);
        Append(builder, "ALPHA_BLEND", features, PbrFeatures.AlphaBlend);
        Append(builder, "DOUBLE_SIDED", features, PbrFeatures.DoubleSided);
        Append(builder, "CLEARCOAT", features, PbrFeatures.ClearCoat);
        Append(builder, "ENCODE_SRGB", features, PbrFeatures.EncodeSrgb);
        builder.Append(Body);
        return builder.ToString();
    }

    private static void Append(StringBuilder builder, string name, PbrFeatures features, PbrFeatures flag) =>
        builder.Append("const ").Append(name).Append(": bool = ").Append((features & flag) != 0 ? "true" : "false").Append(";\n");

    private const string Body = @"
const PI: f32 = 3.14159265358979;
const MAX_LIGHTS: u32 = 8u;

struct Light {
    direction: vec4<f32>,
    color: vec4<f32>,
};

struct FrameUniforms {
    view_projection: mat4x4<f32>,
    camera_position: vec4<f32>,
    ambient_sky: vec4<f32>,
    ambient_ground: vec4<f32>,
    ambient_up: vec4<f32>,
    environment: vec4<f32>,
    lights: array<Light, 8>,
};

struct MaterialUniforms {
    base_color: vec4<f32>,
    emissive: vec4<f32>,
    params: vec4<f32>,
    extra: vec4<f32>,
    specular: vec4<f32>,
    flags: vec4<u32>,
    uv: array<vec4<f32>, 10>,
};

struct Instance {
    world: mat4x4<f32>,
    node_base: u32,
    joint_base: u32,
    weight_base: u32,
    pad: u32,
};

struct DrawUniforms {
    node: u32,
    base_vertex: u32,
    skin_base: u32,
    joint_offset: u32,
    morph_targets: u32,
    morph_weight_offset: u32,
    morph_delta_base: u32,
    morph_vertex_count: u32,
    morph_channels: u32,
    morph_stride: u32,
    pad0: u32,
    pad1: u32,
};

@group(0) @binding(0) var<uniform> frame: FrameUniforms;
@group(0) @binding(1) var irradiance_map: texture_cube<f32>;
@group(0) @binding(2) var prefiltered_map: texture_cube<f32>;
@group(0) @binding(3) var brdf_lut: texture_2d<f32>;
@group(0) @binding(4) var environment_sampler: sampler;

@group(1) @binding(0) var<uniform> material: MaterialUniforms;
@group(1) @binding(1) var base_texture: texture_2d<f32>;
@group(1) @binding(2) var metallic_roughness_texture: texture_2d<f32>;
@group(1) @binding(3) var normal_texture: texture_2d<f32>;
@group(1) @binding(4) var occlusion_texture: texture_2d<f32>;
@group(1) @binding(5) var emissive_texture: texture_2d<f32>;
@group(1) @binding(6) var base_sampler: sampler;
@group(1) @binding(7) var metallic_roughness_sampler: sampler;
@group(1) @binding(8) var normal_sampler: sampler;
@group(1) @binding(9) var occlusion_sampler: sampler;
@group(1) @binding(10) var emissive_sampler: sampler;

@group(2) @binding(0) var<storage, read> instances: array<Instance>;
// Matrices are read as four vec4 columns, not as an array of mat4x4: some mobile drivers (Adreno 6xx Vulkan) read a
// runtime-sized storage array of bare matrices wrongly. Same memory layout (64-byte stride, column-major).
@group(2) @binding(1) var<storage, read> matrices: array<vec4<f32>>;
@group(2) @binding(2) var<storage, read> morph_weights: array<f32>;

@group(3) @binding(0) var<uniform> draw: DrawUniforms;
@group(3) @binding(1) var<storage, read> skin_stream: array<u32>;
@group(3) @binding(2) var<storage, read> morph_deltas: array<vec4<f32>>;

fn load_matrix(index: u32) -> mat4x4<f32> {
    let at = index * 4u;
    return mat4x4<f32>(matrices[at], matrices[at + 1u], matrices[at + 2u], matrices[at + 3u]);
}

struct VertexInput {
    @location(0) position: vec3<f32>,
    @location(1) normal: vec3<f32>,
    @location(2) tangent: vec4<f32>,
    @location(3) uv0: vec2<f32>,
    @location(4) uv1: vec2<f32>,
    @location(5) color: vec4<f32>,
};

struct VertexOutput {
    @builtin(position) clip: vec4<f32>,
    @location(0) world_position: vec3<f32>,
    @location(1) normal: vec3<f32>,
    @location(2) tangent: vec4<f32>,
    @location(3) uv0: vec2<f32>,
    @location(4) uv1: vec2<f32>,
    @location(5) color: vec4<f32>,
};

@vertex
fn vs_main(input: VertexInput, @builtin(vertex_index) vertex_index: u32, @builtin(instance_index) instance_index: u32) -> VertexOutput {
    let instance = instances[instance_index];
    var position = input.position;
    var normal = input.normal;
    var tangent = input.tangent.xyz;
    if (MORPHED) {
        let local_vertex = vertex_index - draw.base_vertex;
        for (var morph = 0u; morph < draw.morph_targets; morph++) {
            let weight = morph_weights[instance.weight_base + draw.morph_weight_offset + morph];
            if (weight != 0.0) {
                var at = draw.morph_delta_base + (morph * draw.morph_vertex_count + local_vertex) * draw.morph_stride;
                if ((draw.morph_channels & 1u) != 0u) {
                    position += weight * morph_deltas[at].xyz;
                    at++;
                }
                if ((draw.morph_channels & 2u) != 0u) {
                    normal += weight * morph_deltas[at].xyz;
                    at++;
                }
                if ((draw.morph_channels & 4u) != 0u) {
                    tangent += weight * morph_deltas[at].xyz;
                }
            }
        }
    }

    var model: mat4x4<f32>;
    if (SKINNED) {
        let at = (vertex_index - draw.base_vertex + draw.skin_base) * 6u;
        let joints01 = skin_stream[at];
        let joints23 = skin_stream[at + 1u];
        let weights = vec4<f32>(bitcast<f32>(skin_stream[at + 2u]), bitcast<f32>(skin_stream[at + 3u]), bitcast<f32>(skin_stream[at + 4u]), bitcast<f32>(skin_stream[at + 5u]));
        let base = instance.joint_base + draw.joint_offset;
        let skin = weights.x * load_matrix(base + (joints01 & 0xFFFFu))
            + weights.y * load_matrix(base + (joints01 >> 16u))
            + weights.z * load_matrix(base + (joints23 & 0xFFFFu))
            + weights.w * load_matrix(base + (joints23 >> 16u));
        model = instance.world * skin;
    } else {
        model = instance.world * load_matrix(instance.node_base + draw.node);
    }

    let world = model * vec4<f32>(position, 1.0);
    let basis = mat3x3<f32>(model[0].xyz, model[1].xyz, model[2].xyz);
    let cofactor = mat3x3<f32>(cross(basis[1], basis[2]), cross(basis[2], basis[0]), cross(basis[0], basis[1]));
    let handedness = select(-1.0, 1.0, dot(basis[0], cofactor[0]) >= 0.0);
    var output: VertexOutput;
    output.clip = frame.view_projection * world;
    output.world_position = world.xyz;
    output.normal = cofactor * normal * handedness;
    // A mirroring model matrix flips cross(N, T), so the bitangent sign flips with it.
    output.tangent = vec4<f32>(basis * tangent, input.tangent.w * handedness);
    output.uv0 = input.uv0;
    output.uv1 = input.uv1;
    output.color = input.color;
    return output;
}

fn slot_uv(slot: u32, uv0: vec2<f32>, uv1: vec2<f32>) -> vec2<f32> {
    let source = vec3<f32>(select(uv0, uv1, ((material.flags.y >> slot) & 1u) != 0u), 1.0);
    return vec2<f32>(dot(material.uv[slot * 2u].xyz, source), dot(material.uv[slot * 2u + 1u].xyz, source));
}

fn fresnel_schlick(f0: vec3<f32>, cosine: f32) -> vec3<f32> {
    return f0 + (vec3<f32>(1.0) - f0) * pow(clamp(1.0 - cosine, 0.0, 1.0), 5.0);
}

fn distribution_ggx(n_dot_h: f32, alpha: f32) -> f32 {
    let alpha2 = alpha * alpha;
    let f = n_dot_h * n_dot_h * (alpha2 - 1.0) + 1.0;
    return alpha2 / (PI * f * f);
}

fn visibility_smith(n_dot_l: f32, n_dot_v: f32, alpha: f32) -> f32 {
    let alpha2 = alpha * alpha;
    let ggx_v = n_dot_l * sqrt(n_dot_v * n_dot_v * (1.0 - alpha2) + alpha2);
    let ggx_l = n_dot_v * sqrt(n_dot_l * n_dot_l * (1.0 - alpha2) + alpha2);
    let sum = ggx_v + ggx_l;
    return select(0.0, 0.5 / sum, sum > 0.0);
}

fn environment_brdf_approx(f0: vec3<f32>, roughness: f32, n_dot_v: f32) -> vec3<f32> {
    let c0 = vec4<f32>(-1.0, -0.0275, -0.572, 0.022);
    let c1 = vec4<f32>(1.0, 0.0425, 1.04, -0.04);
    let r = roughness * c0 + c1;
    let a004 = min(r.x * r.x, exp2(-9.28 * n_dot_v)) * r.x + r.y;
    let ab = vec2<f32>(-1.04, 1.04) * a004 + r.zw;
    return f0 * ab.x + vec3<f32>(ab.y);
}

// World direction -> environment direction (the environment turns by frame.environment.w about +Y).
fn to_environment(direction: vec3<f32>) -> vec3<f32> {
    let c = cos(frame.environment.w);
    let s = sin(frame.environment.w);
    return vec3<f32>(c * direction.x - s * direction.z, direction.y, s * direction.x + c * direction.z);
}

fn environment_specular(direction: vec3<f32>, roughness: f32) -> vec3<f32> {
    return textureSampleLevel(prefiltered_map, environment_sampler, to_environment(direction), roughness * frame.environment.z).rgb * frame.environment.y;
}

fn hemisphere(direction: vec3<f32>) -> vec3<f32> {
    let t = clamp(dot(direction, frame.ambient_up.xyz) * 0.5 + 0.5, 0.0, 1.0);
    return mix(frame.ambient_ground.rgb, frame.ambient_sky.rgb, t);
}

fn encode_srgb(value: vec3<f32>) -> vec3<f32> {
    let c = clamp(value, vec3<f32>(0.0), vec3<f32>(1.0));
    let low = c * 12.92;
    let high = 1.055 * pow(c, vec3<f32>(1.0 / 2.4)) - vec3<f32>(0.055);
    return select(high, low, c <= vec3<f32>(0.0031308));
}

fn finish(rgb: vec3<f32>, alpha: f32) -> vec4<f32> {
    let exposed = rgb * frame.camera_position.w;
    if (ENCODE_SRGB) {
        return vec4<f32>(encode_srgb(exposed), alpha);
    }
    return vec4<f32>(exposed, alpha);
}

@fragment
fn fs_main(input: VertexOutput, @builtin(front_facing) front_facing: bool) -> @location(0) vec4<f32> {
    let base_uv = slot_uv(0u, input.uv0, input.uv1);
    let metallic_roughness_uv = slot_uv(1u, input.uv0, input.uv1);
    let normal_uv = slot_uv(2u, input.uv0, input.uv1);
    let occlusion_uv = slot_uv(3u, input.uv0, input.uv1);
    let emissive_uv = slot_uv(4u, input.uv0, input.uv1);
    let base_sample = textureSample(base_texture, base_sampler, base_uv);
    let metallic_roughness_sample = textureSample(metallic_roughness_texture, metallic_roughness_sampler, metallic_roughness_uv);
    let normal_sample = textureSample(normal_texture, normal_sampler, normal_uv);
    let occlusion_sample = textureSample(occlusion_texture, occlusion_sampler, occlusion_uv);
    let emissive_sample = textureSample(emissive_texture, emissive_sampler, emissive_uv);
    let position_dx = dpdx(input.world_position);
    let position_dy = dpdy(input.world_position);
    let uv_dx = dpdx(normal_uv);
    let uv_dy = dpdy(normal_uv);

    var base = material.base_color * input.color * base_sample;
    if (ALPHA_MASK) {
        if (base.a < material.extra.x) {
            discard;
        }
        base.a = 1.0;
    } else if (!ALPHA_BLEND) {
        base.a = 1.0;
    }

    if (UNLIT) {
        return finish(base.rgb, base.a);
    }

    let geometric = normalize(input.normal);
    var n = geometric;
    var face = 1.0;
    if (DOUBLE_SIDED) {
        face = select(-1.0, 1.0, front_facing);
    }
    if (NORMAL_MAP) {
        var t: vec3<f32>;
        var b: vec3<f32>;
        let denominator = uv_dx.x * uv_dy.y - uv_dy.x * uv_dx.y;
        let inverse = select(0.0, 1.0 / denominator, abs(denominator) > 1e-12);
        let derived = (uv_dy.y * position_dx - uv_dx.y * position_dy) * inverse;
        let derived_bitangent = (uv_dx.x * position_dy - uv_dy.x * position_dx) * inverse;
        let supplied = input.tangent.xyz;
        if (dot(supplied, supplied) > 1e-12) {
            t = normalize(supplied - geometric * dot(geometric, supplied));
            b = cross(geometric, t) * select(-1.0, 1.0, input.tangent.w >= 0.0);
        } else {
            let orthogonal = derived - geometric * dot(geometric, derived);
            t = select(vec3<f32>(1.0, 0.0, 0.0), normalize(orthogonal), dot(orthogonal, orthogonal) > 1e-20);
            // The bitangent points up the image (towards v = 0), i.e. against dP/dv: mirrored UV layouts and mirroring
            // transforms flip it.
            b = cross(geometric, t) * select(1.0, -1.0, dot(cross(geometric, t), derived_bitangent) > 0.0);
        }
        let tangent_normal = (normal_sample.xyz * 2.0 - vec3<f32>(1.0)) * vec3<f32>(material.params.z, material.params.z, 1.0);
        n = normalize(mat3x3<f32>(t, b, geometric) * tangent_normal);
    }
    n = n * face;
    let surface_geometric = geometric * face;

    let metallic = clamp(material.params.x * metallic_roughness_sample.b, 0.0, 1.0);
    let roughness = clamp(material.params.y * metallic_roughness_sample.g, 0.0, 1.0);
    let alpha = max(roughness * roughness, 0.0001);
    let occlusion = 1.0 + material.params.w * (occlusion_sample.r - 1.0);
    let dielectric_f0 = min(vec3<f32>(material.extra.y) * material.specular.rgb, vec3<f32>(1.0)) * material.extra.z;
    let f0 = mix(dielectric_f0, base.rgb, metallic);
    let diffuse_color = base.rgb * (1.0 - metallic);

    let v = normalize(frame.camera_position.xyz - input.world_position);
    let n_dot_v = clamp(dot(n, v), 0.0001, 1.0);
    let clearcoat = material.extra.w;
    let clearcoat_alpha = max(material.emissive.w * material.emissive.w, 0.0001);
    let clearcoat_n_dot_v = clamp(dot(surface_geometric, v), 0.0001, 1.0);

    var color = vec3<f32>(0.0);
    let light_count = min(u32(frame.ambient_up.w), MAX_LIGHTS);
    for (var index = 0u; index < light_count; index++) {
        let light = frame.lights[index];
        let l = -light.direction.xyz;
        let n_dot_l = dot(n, l);
        if (n_dot_l > 0.0) {
            let h = normalize(l + v);
            let v_dot_h = clamp(dot(v, h), 0.0, 1.0);
            let n_dot_h = clamp(dot(n, h), 0.0, 1.0);
            let f = fresnel_schlick(f0, v_dot_h);
            let diffuse = (vec3<f32>(1.0) - f) * diffuse_color / PI;
            let specular = f * distribution_ggx(n_dot_h, alpha) * visibility_smith(n_dot_l, n_dot_v, alpha);
            var radiance = (diffuse + specular) * light.color.rgb * (PI * n_dot_l);
            if (CLEARCOAT) {
                let c_dot_l = clamp(dot(surface_geometric, l), 0.0, 1.0);
                let c_dot_h = clamp(dot(surface_geometric, h), 0.0, 1.0);
                let coat_f = fresnel_schlick(vec3<f32>(0.04), v_dot_h).x * clearcoat;
                let coat = coat_f * distribution_ggx(c_dot_h, clearcoat_alpha) * visibility_smith(c_dot_l, clearcoat_n_dot_v, clearcoat_alpha) * PI * c_dot_l;
                radiance = radiance * (1.0 - coat_f) + light.color.rgb * coat;
            }
            color += radiance;
        }
    }

    let r = reflect(-v, n);
    var ambient_diffuse: vec3<f32>;
    var ambient_specular: vec3<f32>;
    if (frame.environment.x > 0.5) {
        let irradiance = textureSampleLevel(irradiance_map, environment_sampler, to_environment(n), 0.0).rgb * frame.environment.y;
        let prefiltered = environment_specular(r, roughness);
        let scale_bias = textureSampleLevel(brdf_lut, environment_sampler, vec2<f32>(n_dot_v, roughness), 0.0).rg;
        // Split-sum single scattering plus multiple-scattering energy compensation (Fdez-Aguera 2019): a white
        // furnace stays white for every roughness and metalness.
        let single = f0 * scale_bias.x + vec3<f32>(scale_bias.y);
        let multiple_energy = 1.0 - (scale_bias.x + scale_bias.y);
        let average_f = f0 + (vec3<f32>(1.0) - f0) / 21.0;
        let multiple = single * average_f / (vec3<f32>(1.0) - multiple_energy * average_f);
        let diffuse_weight = diffuse_color * (vec3<f32>(1.0) - single - multiple * multiple_energy);
        ambient_diffuse = irradiance * diffuse_weight + irradiance * multiple * multiple_energy;
        ambient_specular = prefiltered * single;
    } else {
        ambient_diffuse = hemisphere(n) * diffuse_color;
        ambient_specular = hemisphere(r) * environment_brdf_approx(f0, roughness, n_dot_v);
    }
    var ambient = ambient_diffuse + ambient_specular;
    if (CLEARCOAT) {
        let coat_f = fresnel_schlick(vec3<f32>(0.04), clearcoat_n_dot_v).x * clearcoat;
        var coat_light: vec3<f32>;
        if (frame.environment.x > 0.5) {
            coat_light = environment_specular(reflect(-v, surface_geometric), material.emissive.w);
        } else {
            coat_light = hemisphere(reflect(-v, surface_geometric));
        }
        ambient = ambient * (1.0 - coat_f) + coat_light * coat_f;
    }
    color += ambient * occlusion;
    color += material.emissive.rgb * emissive_sample.rgb;
    return finish(color, base.a);
}
";
}
