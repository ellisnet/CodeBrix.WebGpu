namespace CodeBrix.WebGpu.Graphics.Shaders;

/// <summary>The WGSL sources of the library's own renderers.</summary>
internal static class BuiltInShaders
{
    /// <summary>The sprite and shape batch: one uniform transform, one texture + sampler per draw, a per-vertex "textured" weight so shapes and sprites share a draw.</summary>
    public const string Sprite = @"
struct Globals {
    transform: mat4x4<f32>,
};

@group(0) @binding(0) var<uniform> globals: Globals;
@group(1) @binding(0) var sprite_texture: texture_2d<f32>;
@group(1) @binding(1) var sprite_sampler: sampler;

struct VertexInput {
    @location(0) position: vec2<f32>,
    @location(1) uv: vec2<f32>,
    @location(2) color: vec4<f32>,
    @location(3) textured: f32,
};

struct VertexOutput {
    @builtin(position) clip: vec4<f32>,
    @location(0) uv: vec2<f32>,
    @location(1) color: vec4<f32>,
    @location(2) textured: f32,
};

@vertex
fn vs_main(input: VertexInput) -> VertexOutput {
    var output: VertexOutput;
    output.clip = globals.transform * vec4<f32>(input.position, 0.0, 1.0);
    output.uv = input.uv;
    output.color = input.color;
    output.textured = input.textured;
    return output;
}

@fragment
fn fs_main(input: VertexOutput) -> @location(0) vec4<f32> {
    let sampled = textureSample(sprite_texture, sprite_sampler, input.uv);
    return input.color * mix(vec4<f32>(1.0, 1.0, 1.0, 1.0), sampled, input.textured);
}
";

    /// <summary>The lit mesh renderer: Blinn-Phong with one directional light and an ambient term, per-instance model and normal matrices.</summary>
    public const string LitMesh = @"
struct FrameUniforms {
    view_projection: mat4x4<f32>,
    camera_position: vec4<f32>,
    light_direction: vec4<f32>,
    light_color: vec4<f32>,
    ambient_color: vec4<f32>,
};

struct MaterialUniforms {
    base_color: vec4<f32>,
    params: vec4<f32>,
};

@group(0) @binding(0) var<uniform> frame: FrameUniforms;
@group(1) @binding(0) var<uniform> material: MaterialUniforms;
@group(1) @binding(1) var base_texture: texture_2d<f32>;
@group(1) @binding(2) var base_sampler: sampler;

struct VertexInput {
    @location(0) position: vec3<f32>,
    @location(1) normal: vec3<f32>,
    @location(2) uv: vec2<f32>,
    @location(3) color: vec4<f32>,
    @location(4) model0: vec4<f32>,
    @location(5) model1: vec4<f32>,
    @location(6) model2: vec4<f32>,
    @location(7) model3: vec4<f32>,
    @location(8) normal0: vec4<f32>,
    @location(9) normal1: vec4<f32>,
    @location(10) normal2: vec4<f32>,
};

struct VertexOutput {
    @builtin(position) clip: vec4<f32>,
    @location(0) world_position: vec3<f32>,
    @location(1) normal: vec3<f32>,
    @location(2) uv: vec2<f32>,
    @location(3) color: vec4<f32>,
};

@vertex
fn vs_main(input: VertexInput) -> VertexOutput {
    let model = mat4x4<f32>(input.model0, input.model1, input.model2, input.model3);
    let normal_matrix = mat3x3<f32>(input.normal0.xyz, input.normal1.xyz, input.normal2.xyz);
    let world = model * vec4<f32>(input.position, 1.0);
    var output: VertexOutput;
    output.clip = frame.view_projection * world;
    output.world_position = world.xyz;
    output.normal = normal_matrix * input.normal;
    output.uv = input.uv;
    output.color = input.color;
    return output;
}

@fragment
fn fs_main(input: VertexOutput, @builtin(front_facing) front_facing: bool) -> @location(0) vec4<f32> {
    let albedo = material.base_color * input.color * textureSample(base_texture, base_sampler, input.uv);
    var n = normalize(input.normal);
    if (!front_facing) {
        n = -n;
    }
    let l = normalize(-frame.light_direction.xyz);
    let v = normalize(frame.camera_position.xyz - input.world_position);
    let h = normalize(l + v);
    let diffuse = max(dot(n, l), 0.0);
    var specular = 0.0;
    if (diffuse > 0.0) {
        specular = pow(max(dot(n, h), 0.0), material.params.x) * material.params.y;
    }
    let lit = albedo.rgb * (frame.ambient_color.rgb + diffuse * frame.light_color.rgb) + specular * frame.light_color.rgb;
    let rgb = mix(lit, albedo.rgb, material.params.z);
    return vec4<f32>(rgb, albedo.a);
}
";
}
