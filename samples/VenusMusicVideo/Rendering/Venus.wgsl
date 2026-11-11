// Venus, ray traced per pixel: a perfect unit sphere at any distance (no tessellation), coloured by the
// equirectangular colour map, shaded with the relief (slope) map, wrapped in a glowing haze shell, in front of a
// procedural star field. Space units: one Venus radius. The planet spins about +Y; the camera lives in space.

struct Uniforms {
  cam: vec4f,       // xyz camera position (space), w spin angle (radians, space -> surface)
  right: vec4f,     // xyz camera right, w tan(half horizontal fov)
  up: vec4f,        // xyz camera up, w tan(half vertical fov)
  fwd: vec4f,       // xyz camera forward, w angular size of one pixel (radians)
  light: vec4f,     // xyz direction TOWARDS the light (space), w relief exaggeration
  params: vec4f,    // x width, y height, z fade (0..1), w 1 = encode sRGB in the shader
  haze: vec4f,      // x shell thickness (radii), y density, z glow, w star brightness
  hazeLow: vec4f,   // rgb haze near the surface, w ambient
  hazeHigh: vec4f,  // rgb haze high up, w diffuse
  extra: vec4f,     // x exposure, y star density, z frame number (dither), w unused
  lighting: vec4f,  // x sunlight (0 = even studio light, 1 = sun with a dark night side), y terminator softness
  waveA: vec4f,     // x video time, y lead-in (song = video - lead-in), z envelope samples per second, w envelope length
  waveB: vec4f,     // x strength, y reach (radians), z ripples per second, w number of loudspeakers
  waveColor: vec4f, // rgb
  sources: array<vec4f, 8>, // per loudspeaker: (position xyz on the planet, start), (end, speed rad/s, 0, 0)
};

@group(0) @binding(0) var<uniform> u: Uniforms;
@group(0) @binding(1) var colorMap: texture_2d<f32>;
@group(0) @binding(2) var reliefMap: texture_2d<f32>;
@group(0) @binding(3) var mapSampler: sampler;
@group(0) @binding(4) var<storage, read> envelope: array<f32>;

struct VsOut {
  @builtin(position) position: vec4f,
  @location(0) ndc: vec2f,
};

@vertex
fn vs_main(@builtin(vertex_index) index: u32) -> VsOut {
  var corners = array<vec2f, 3>(vec2f(-1.0, -1.0), vec2f(3.0, -1.0), vec2f(-1.0, 3.0));
  var out: VsOut;
  out.position = vec4f(corners[index], 0.0, 1.0);
  out.ndc = corners[index];
  return out;
}

const PI: f32 = 3.14159265358979;

fn spinY(v: vec3f, a: f32) -> vec3f {
  let c = cos(a);
  let s = sin(a);
  return vec3f(v.x * c + v.z * s, v.y, -v.x * s + v.z * c);
}

fn pcg3(seed: vec3u) -> vec3u {
  var v = seed * 1664525u + 1013904223u;
  v.x += v.y * v.z; v.y += v.z * v.x; v.z += v.x * v.y;
  v ^= v >> vec3u(16u);
  v.x += v.y * v.z; v.y += v.z * v.x; v.z += v.x * v.y;
  return v;
}

fn rand3(seed: vec3u) -> vec3f {
  return vec3f(pcg3(seed)) * (1.0 / 4294967296.0);
}

// A sparse star field fixed in space: one possible star per cell of a cube-map grid, drawn as a small Gaussian
// about one pixel wide so it neither aliases nor shimmers.
fn stars(d: vec3f) -> vec3f {
  let a = abs(d);
  var face: u32;
  var st: vec2f;
  if (a.x >= a.y && a.x >= a.z) {
    face = select(1u, 0u, d.x > 0.0); st = d.yz / a.x;
  } else if (a.y >= a.z) {
    face = select(3u, 2u, d.y > 0.0); st = d.xz / a.y;
  } else {
    face = select(5u, 4u, d.z > 0.0); st = d.xy / a.z;
  }
  let cells = 360.0;
  let g = (st * 0.5 + 0.5) * cells;
  let cell = floor(g);
  let r = rand3(vec3u(u32(cell.x), u32(cell.y), face * 7919u + 17u));
  if (r.x > u.extra.y) { return vec3f(0.0); }
  let r2 = rand3(vec3u(u32(cell.y) + 101u, u32(cell.x) + 307u, face + 911u));
  let center = cell + vec2f(0.25) + 0.5 * r.yz;
  let distance = length(g - center) * (2.0 / cells);       // ~radians near the face centre
  let sigma = max(u.fwd.w * 0.75, 1e-6);
  let brightness = 0.04 + 0.96 * pow(r2.x, 7.0);
  let tint = mix(vec3f(0.75, 0.82, 1.0), vec3f(1.0, 0.85, 0.7), r2.y);
  return tint * brightness * exp(-0.5 * (distance * distance) / (sigma * sigma));
}

// The song's loudness at a song time (0 outside the song).
fn songLevel(songTime: f32) -> f32 {
  let f = songTime * u.waveA.z;
  if (f < 0.0 || f >= u.waveA.w - 1.0) { return 0.0; }
  let i = u32(f);
  return mix(envelope[i], envelope[i + 1u], fract(f));
}

// The sound wave at a point of the surface: for each loudspeaker, the song as it was when the ring now passing
// here left it (distance / speed earlier), fading with distance. pixelAngle = the pixel's size in radians, to
// soften rings too fine to show.
fn soundWave(p: vec3f, pixelAngle: f32) -> f32 {
  var total = 0.0;
  let count = i32(u.waveB.w);
  for (var k = 0; k < 4; k++) {
    if (k >= count) { break; }
    let a = u.sources[2 * k];
    let b = u.sources[2 * k + 1];
    let d = acos(clamp(dot(p, a.xyz), -1.0, 1.0));
    let emitted = u.waveA.x - d / b.y;
    if (emitted < a.w || emitted >= b.x) { continue; }
    let song = emitted - u.waveA.y;
    let span = pixelAngle / b.y; // seconds of song across one pixel
    let level = (songLevel(song - span * 0.5) + songLevel(song) + songLevel(song + span * 0.5)) / 3.0;
    let ripple = 1.0 - 0.3 * (0.5 - 0.5 * cos(6.2831853 * u.waveB.z * emitted)) * clamp(1.0 - span * u.waveB.z * 2.0, 0.0, 1.0);
    let reach = u.waveB.y;
    let falloff = 1.0 / (1.0 + (d / reach) * (d / reach));
    let born = smoothstep(0.0, 0.004, d);
    total += level * ripple * falloff * born;
  }
  return total;
}

fn encodeSrgb(c: vec3f) -> vec3f {
  let lo = c * 12.92;
  let hi = 1.055 * pow(c, vec3f(1.0 / 2.4)) - 0.055;
  return select(hi, lo, c <= vec3f(0.0031308));
}

@fragment
fn fs_main(in: VsOut) -> @location(0) vec4f {
  let spin = u.cam.w;
  let dirSpace = normalize(u.fwd.xyz + in.ndc.x * u.right.w * u.right.xyz + in.ndc.y * u.up.w * u.up.xyz);

  // Into the planet's frame.
  let o = spinY(u.cam.xyz, spin);
  let d = spinY(dirSpace, spin);

  // Ray against the unit sphere.
  let b = dot(o, d);
  let c = dot(o, o) - 1.0;
  let disc = b * b - c;
  let tHit = -b - sqrt(max(disc, 0.0));
  let hit = disc > 0.0 && tHit > 0.0;

  // The surface point (for a miss, the nearest point under the ray, so the map coordinates stay continuous and
  // their screen derivatives stay sane at the limb).
  var p = o + d * max(tHit, 0.0);
  if (!hit) { p = o + d * max(-b, 0.0); }
  p = normalize(p);
  let lon = atan2(p.x, p.z);
  let lat = asin(clamp(p.y, -1.0, 1.0));
  let uv = vec2f(lon / (2.0 * PI) + 0.5, 0.5 - lat / PI);

  // Mip selection without a seam where longitude wraps: take the smaller derivative of u or of u shifted by half.
  let uvShift = vec2f(fract(uv.x + 0.5), uv.y);
  let dx1 = dpdx(uv);
  let dy1 = dpdy(uv);
  let dx2 = dpdx(uvShift);
  let dy2 = dpdy(uvShift);
  let gx = vec2f(select(dx1.x, dx2.x, abs(dx2.x) < abs(dx1.x)), dx1.y);
  let gy = vec2f(select(dy1.x, dy2.x, abs(dy2.x) < abs(dy1.x)), dy1.y);
  let pixelAngle = max(length(dpdx(p)), length(dpdy(p)));

  let l = spinY(u.light.xyz, spin);
  let sun = u.lighting.x;

  // Haze shell: the length of the ray inside it (up to the surface), and how high the ray stays.
  let shell = 1.0 + u.haze.x;
  let cs = dot(o, o) - shell * shell;
  let discShell = b * b - cs;
  var hazePath = 0.0;
  var lowest = 1.0e9;
  var closest = p;
  if (discShell > 0.0) {
    let sq = sqrt(discShell);
    let t0 = max(-b - sq, 0.0);
    var t1 = -b + sq;
    if (hit) { t1 = tHit; }
    hazePath = max(t1 - t0, 0.0);
    let tc = clamp(-b, t0, max(t1, t0));
    let near = o + d * tc;
    lowest = max(length(near) - 1.0, 0.0);
    closest = normalize(near);
  }
  if (hit) { closest = p; }
  let heightFade = exp(-lowest / (u.haze.x * 0.4));
  let hazeAmount = (1.0 - exp(-hazePath * u.haze.y)) * heightFade;
  // In sunlight the haze glows where the sun reaches it, and shines through (forward scattering) when the
  // camera looks towards the sun past the planet - the bright ring of a "new" Venus.
  let forwardGlow = select(1.4 * pow(max(dot(d, l), 0.0), 8.0), 0.0, hit);
  let hazeSunlit = smoothstep(-0.3, 0.25, dot(closest, l)) + forwardGlow;
  let hazeColor = mix(u.hazeHigh.rgb, u.hazeLow.rgb, exp(-lowest / (u.haze.x * 0.3))) * u.haze.z * mix(1.0, hazeSunlit, sun);

  var color: vec3f;
  if (hit) {
    let albedo = textureSampleGrad(colorMap, mapSampler, uv, gx, gy).rgb;
    let rel = textureSampleGrad(reliefMap, mapSampler, uv, gx, gy).rg;
    let e = rel * (255.0 / 127.5) - 1.0;
    let slope = sign(e) * e * e * 0.25;
    let east = vec3f(cos(lon), 0.0, -sin(lon));
    let north = vec3f(-sin(lat) * sin(lon), cos(lat), -sin(lat) * cos(lon));
    let n = normalize(p - u.light.w * (slope.x * east + slope.y * north));
    let wrap = u.lighting.y * sun;
    var diffuse = max((dot(n, l) + wrap) / (1.0 + wrap), 0.0);
    // Relief must not light up the night side: the sphere's own terminator gates it.
    diffuse *= mix(1.0, smoothstep(-0.08, 0.12, dot(p, l)), sun);
    let lit = albedo * (u.hazeLow.w + u.hazeHigh.w * diffuse) * u.extra.x;
    color = mix(lit, hazeColor, hazeAmount);
    if (u.waveB.w > 0.5 && u.waveB.x > 0.0) {
      // The song spreading across the surface: deep indigo rings, drawn over the haze so they keep their colour.
      let w = soundWave(p, pixelAngle) * u.waveB.x;
      color = mix(color, u.waveColor.rgb, 0.92 * clamp(w * 1.4, 0.0, 1.0));
    }
  } else {
    color = stars(dirSpace) * u.haze.w * (1.0 - hazeAmount) + hazeColor * hazeAmount;
  }

  color = max(color * u.params.z, vec3f(0.0));
  // Soft shoulder so bright haze and highlights roll off instead of clipping.
  color = color / (1.0 + 0.18 * max(max(color.r, color.g), color.b));
  if (u.params.w > 0.5) { color = encodeSrgb(color); }

  // Tiny dither against banding in the haze gradients (the video is 8-bit).
  let noise = rand3(vec3u(u32(in.position.x), u32(in.position.y), u32(u.extra.z))).x - 0.5;
  color += vec3f(noise / 255.0);
  return vec4f(color, 1.0);
}
