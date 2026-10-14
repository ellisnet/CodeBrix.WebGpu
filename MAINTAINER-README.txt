================================================================================
MAINTAINER-README: CodeBrix.WebGpu
Notes for people and agents MAINTAINING this repository - not for package
consumers
================================================================================

If you are CONSUMING the NuGet package, stop reading and open AGENT-README.txt
instead. Everything below is about the repository itself: how it is laid out,
how the binding is generated, how the natives are built and bumped, how it is
tested and packaged, and the conventions the source follows.


PURPOSE AND SCOPE
=================

  PackageId:        CodeBrix.WebGpu.Native.MitLicenseForever
  Assembly:         CodeBrix.WebGpu.Native (root namespace the same)
  Project:          src/CodeBrix.WebGpu.Native/CodeBrix.WebGpu.Native.csproj
  License:          MIT (repository LICENSE); natives: LICENSE-WgpuNative.txt
  Consumer doc:     AGENT-README.txt

  PackageId:        CodeBrix.WebGpu.Skia.MitLicenseForever
  Assembly:         CodeBrix.WebGpu.Skia (root namespace the same)
  Project:          src/CodeBrix.WebGpu.Skia/CodeBrix.WebGpu.Skia.csproj
  License:          MIT (repository LICENSE)
  Consumer doc:     src/CodeBrix.WebGpu.Skia/AGENT-README.txt (+ its own README.md)
  Design notes:     THE SKIA PACKAGE, below

  PackageId:        CodeBrix.WebGpu.Graphics.MitLicenseForever
  Assembly:         CodeBrix.WebGpu.Graphics (root namespace the same)
  Project:          src/CodeBrix.WebGpu.Graphics/CodeBrix.WebGpu.Graphics.csproj
  License:          MIT (repository LICENSE)
  Dependencies:     CodeBrix.WebGpu.Native, CodeBrix.Imaging.ApacheLicenseForever
                    (Apache-2.0; image decoding/encoding) and
                    CodeBrix.Graphics3D.Gltf2.MitLicenseForever (glTF reading),
                    both pinned in the csproj; nothing else, ever
  Consumer doc:     src/CodeBrix.WebGpu.Graphics/AGENT-README.txt (+ its own README.md)
  Design notes:     THE GRAPHICS PACKAGE, below

The repository produces THREE packages, all MIT, from one solution
(CodeBrix.WebGpu.slnx):

  CodeBrix.WebGpu.Native     the natives + the thin generated binding (built)
  CodeBrix.WebGpu.Skia       wgpu <-> SkiaSharp: zero-copy into a GPU SKSurface
                             (Vulkan / Metal / Direct3D 12), GPU copy or
                             readback otherwise (built - phase 2)
  CodeBrix.WebGpu.Graphics   the high-level graphics API over the binding:
                             context, resources, pipelines, 2D sprite batch,
                             lit 3D meshes, images + animated textures, glTF
                             models (PBR, GPU skinning + morphs, animation
                             playback), image-based lighting, skybox, tone
                             mapping, headless + window presentation
                             (built - phase 3, extended 2026-10-13)

The upstream pin is stated as "wgpu 29.0.3 via wgpu-native v29.0.1.1" (the
wgpu-native RELEASE, not trunk). Its webgpu-headers submodule commit is
673658bc2bd70ec39fc55ebe6bb0173cf6d0a603.


REPOSITORY LAYOUT
=================

  CodeBrix.WebGpu.slnx       the one solution. Solution Items = the canonical
                             ten files (.gitignore, AGENT-README.txt,
                             EXTRAS-README.txt, global.json,
                             icon-codebrix-128.png, LICENSE,
                             MAINTAINER-README.txt, README-INDEX.txt, README.md,
                             THIRD-PARTY-NOTICES.txt); folders Tests, Tools,
                             Samples (WebGpuModelViewer,
                             WebGpuModelViewer.Android).
  global.json                selects the Microsoft.Testing.Platform test runner;
                             pins no SDK.
  src/CodeBrix.WebGpu.Native/
      Generated/*.g.cs       GENERATED - never edit (see THE BINDING GENERATOR)
      include/               GENERATED codebrix_wgpu_procs.h / .rs (native
                             modules; packed as include/ with webgpu.h, wgpu.h,
                             codebrix_wgpu.h and LICENSE-WgpuNative.txt taken
                             from wgpu-native-tools/)
      Sharing/               IWebGpuDeviceSource, IWebGpuTextureTarget
      Modules/               hand-written halves of CodebrixDeviceHandoff /
                             CodebrixFrameHandoff (Fill / From / BeginWebGpuPass)
      CodeBrixWebGpuNative.cs, WebGpu.cs, WgpuString.cs, WgpuException.cs,
      StructLayoutInfo.cs, Interop/WgpuLibrary.cs (resolver),
      Interop/RuntimeMarshalling.cs ([assembly: DisableRuntimeMarshalling])
      runtimes/<rid>/native/ the committed natives + LICENSE-WgpuNative.txt
                             (adopted from wgpu-native-tools/output/ by agent /
                             maintainer after the gate; not built by MSBuild)
  src/CodeBrix.WebGpu.Skia/
      Devices/               WebGpuSkiaDevice (owned + adopted routes), options,
                             backend / route enums
      Targets/               WebGpuSkiaTarget (tiers, frame protocol, readback),
                             options, tier enum, WebGpuSkiaSurfaceBacking
      Backends/              internal SkiaBridge per backend: GRContext on the
                             wgpu device, GRBackendTexture over a wgpu texture,
                             native texture -> wgpu texture
      Interop/               internal: adapter/device/map requests, Vulkan
                             entry-point lookup, constants, pixel formats
      README.md, AGENT-README.txt   THIS package's docs (packed with
                             None Update, the CodeBrix.RustTools.Docker pattern)
  src/CodeBrix.WebGpu.Graphics/
      Context/               GraphicsContext (partial: core, resources,
                             pipelines, meshes, presentation factories),
                             options, backend enum, GraphicsResource base,
                             GraphicsError, GraphicsException +
                             ShaderCompilationException
      Resources/             GpuBuffer, GpuTexture, GpuSampler (+ description),
                             GpuShader, bind-group layouts / groups and their
                             entry structs, TextureFormats
      Pipelines/             VertexLayout (+ builder, VertexElementAttribute,
                             VertexFormats), render / compute builders and
                             pipelines, blend / depth / cull presets
      Frames/                GraphicsFrame, RenderPass, ComputePass
      Presentation/          RenderTargetView, HeadlessRenderTarget,
                             NativeWindowSource, WindowSurface
      Drawing2D/             SpriteBatch (+ internal SpriteVertex)
      Drawing3D/             Mesh, MeshVertex, MeshData, MeshShapes, Camera +
                             OrbitCamera, SceneLighting, LitMaterial,
                             MeshRenderer (+ internal InstanceData)
      Common/                Color4
      Images/                image files -> textures (CodeBrix.Imaging), GPU
                             mipmap generator, AnimatedTexture, readback/save
      Models/                glTF loading (GltfModelLoader over Gltf2), Model,
                             ModelInstance, ModelPoseEdit, ModelRenderer + the
                             internal PBR render path
      Animation/             AnimationPlayer, AnimationState, the allocation-
                             free channel sampler
      Lighting/              EnvironmentMap + baker and shaders, procedural
                             sky, RadianceHdrDecoder, FloatImage,
                             SkyboxRenderer, ToneMapper
      Queries/               GpuQuerySet, GpuTimer
      Bundles/               RenderBundleBuilder, GpuRenderBundle
      Diagnostics/           GraphicsDiagnostics (engine build info, logging,
                             reports)
      Sharing/               Wrap(IWebGpuDeviceSource), BeginFrame /
                             WrapTexture over IWebGpuTextureTarget, native
                             device interop (Vulkan / D3D12 / Metal, adoption)
      Shaders/               internal WGSL: BuiltInShaders (sprite batch, lit
                             mesh) and PbrShader (model renderer)
      Interop/               internal: adapter / device / map / error-scope
                             requests, task-returning waits, callback
                             registry, UTF-8 scratch memory
      README.md, AGENT-README.txt   THIS package's docs (None Update)
  samples/WebGpuModelViewer/  the model-viewer sample (not packed; Samples
                             solution folder) - Scene/, Hud/, Rendering/,
                             Hosting/, Assets/ (CC0 models + licence files)
  samples/WebGpuModelViewer.Android/  the same sample on Android (links the
                             desktop sources and assets; needs the android
                             workload to build the solution)
  tests/CodeBrix.WebGpu.Graphics.Tests/        the Graphics package on a real GPU
  tests/CodeBrix.WebGpu.Native.Tests/          the binding against a real native
  tests/CodeBrix.WebGpu.Skia.Tests/            the Skia package on a real GPU
  tests/CodeBrix.WebGpu.BindingGenerator.Tests/ the generator (+ Fixtures/,
                             Golden/*.golden, the header-parity test)
  tools/CodeBrix.WebGpu.BindingGenerator/      the generator (never packed)
      Templates/*.scriban    the Scriban templates (embedded resources)
  wgpu-native-tools/         everything native - see wgpu-native-tools/README.txt
      wgpu-native/           vendored, unmodified wgpu-native release
      wgpu-native/ffi/webgpu-headers/  vendored, unmodified webgpu-headers
      specs/wgpu.yml         hand-maintained transcription of wgpu.h (below)
      specs/codebrix.yml     the CodeBrix extras contract
      specs/expected-exports.txt  GENERATED export list (the symbol gate)
      specs/proc-table-notes.txt  hand-written per-function notes rendered into
                             the native-module function table (ABORTS: ...)
      codebrix-wgpu/         our Rust crate (the cdylib), patches/, licenses/,
      linux/ android/ windows/ macos/ build scripts, build-host.sh,
      refresh-upstream.sh, output/ unstripped/ (build products)


BUILDING
========

    dotnet build -c Release CodeBrix.WebGpu.slnx

0 warnings / 0 errors is the bar. GenerateDocumentationFile is on for the
library and the generator: CS1591 is fixed by writing the doc comment (for
generated members: by fixing the YAML doc or the fallback in the renderer),
never with NoWarn. The library needs AllowUnsafeBlocks; nothing else is
special. Building does NOT run the generator - Generated/ is committed.


TESTING
=======

xUnit v3 + SilverAssertions. The runner is Microsoft.Testing.Platform,
selected by global.json (do not delete it - without it `dotnet test` falls
back to the VSTest bridge, which fails on the .NET 10 SDK). No coverage
collector is referenced.

    dotnet test --solution CodeBrix.WebGpu.slnx -c Debug
    dotnet test --solution CodeBrix.WebGpu.slnx -c Release

If the SDK reports zero tests, run the test assemblies directly and quote
their counts:

    dotnet tests/CodeBrix.WebGpu.BindingGenerator.Tests/bin/Debug/net10.0/CodeBrix.WebGpu.BindingGenerator.Tests.dll
    dotnet tests/CodeBrix.WebGpu.Native.Tests/bin/Debug/net10.0/CodeBrix.WebGpu.Native.Tests.dll
    dotnet tests/CodeBrix.WebGpu.Skia.Tests/bin/Debug/net10.0/CodeBrix.WebGpu.Skia.Tests.dll
    dotnet tests/CodeBrix.WebGpu.Graphics.Tests/bin/Debug/net10.0/CodeBrix.WebGpu.Graphics.Tests.dll

Which native the tests load: each test project copies the committed
src/CodeBrix.WebGpu.Native/runtimes/** into its output (exactly what the
package ships). To test a native you have just built on this box, run
wgpu-native-tools/build-host.sh and adopt its output into
runtimes/linux-x64/native/ first (see wgpu-native-tools/README.txt).

The GPU tests need a WebGPU adapter. Every one of them SKIPS (with the reason)
when none is found - a skip is not a pass. The development laptop has Intel
UHD Vulkan + llvmpipe; do not install software rasterisers to make tests pass.
The fixture (GpuSession.PlatformBackend) requests Vulkan, or Metal on macOS,
where the natives compile only Metal (Jeremy's Apple Silicon Mac runs the GPU
round trips that way); the codebrix Vulkan-extras tests stay Linux-only.

The Native tests include the guards that tie the repository together:
  * GeneratedFilesDriftTests: Generated/*.g.cs and specs/expected-exports.txt are
    exactly what the generator produces from the committed inputs; every
    LibraryImport entry point is in expected-exports.txt and vice versa.
  * CodeBrixWebGpuNativeTests.CheckStructLayouts_*: size + alignment of EVERY
    generated struct equals what the native reports
    (wgpuCodebrixGetStructLayout) - the safety net of a generated binding.
  * PackageContentTests: the newest nupkg of the configuration has the assembly,
    the documents and exactly the committed runtimes/ files; include/ holds
    exactly the six native-module files, byte-identical to their sources; and
    (no package needed) every committed Linux/Android .so has the ELF SONAME
    libcodebrix_wgpu.so and both macOS dylibs the install name
    @rpath/libcodebrix_wgpu.dylib (tests/.../NativeBinaryReader.cs) - the
    identities the dynamic loaders reuse for directly linked native modules.
    A native rebuild that drops them fails here.
The generator tests include HeaderParityTests (see THE SPECIFICATIONS).

The Skia tests (tests/CodeBrix.WebGpu.Skia.Tests) copy the same committed
natives into their output and add SkiaSharp.NativeAssets.Linux (test-only).
Their fixture (SkiaGpuFixture) uses WebGpuSkiaDevice.Create() - Vulkan on
Linux, Direct3D 12 on Windows, Metal on macOS - and every GPU test skips with
the reason when there is no adapter or no GRContext. Platform-specific tests
skip elsewhere: AdoptMetal on non-macOS, the Windows backend-order and
Vulkan-on-Windows tests on non-Windows, AdoptVulkan where the platform has no
Vulkan. TimingTests prints frames per second per tier (test output and
bin/<config>/net10.0/skia-tier-timing.txt) and asserts only sanity.
PackageContentTests checks the newest Skia nupkg (documents, no runtimes/,
dependencies = SkiaSharp 4.153.1 + CodeBrix.WebGpu.Native.MitLicenseForever).

The Graphics tests (tests/CodeBrix.WebGpu.Graphics.Tests) copy the same
committed natives; their fixture (GraphicsGpuFixture) uses
GraphicsContext.TryCreate() on the platform default backend and every GPU test
skips with the reason when there is none. Pure tests (vertex layouts, camera
maths, generated shapes, colours, native window descriptions) need no GPU.
Pixel tests render to headless targets and assert colours at known
coordinates (sprite batch, lit cube, instancing, depth order, culling,
translucency). SkiaSharingTests reference the Skia project and
SkiaSharp.NativeAssets.Linux (test-only; the Graphics package references
neither): a GraphicsContext wraps a WebGpuSkiaDevice's device and renders into
a WebGpuSkiaTarget through the frame protocol. PackageContentTests checks the
newest Graphics nupkg (documents, no runtimes/, dependencies = exactly
CodeBrix.WebGpu.Native.MitLicenseForever, CodeBrix.Imaging.ApacheLicenseForever
and CodeBrix.Graphics3D.Gltf2.MitLicenseForever at their pinned versions).
ImageTextureTests / AnimatedTextureTests generate their images in-test with
CodeBrix.Imaging; ModelLoadingTests load the CC0 models in
tests/CodeBrix.WebGpu.Graphics.Tests/Assets (credited in THIRD-PARTY-NOTICES.txt,
copied to the output by the test csproj) and build further glTF documents
in-test. ModelCorpusTests is OPT-IN: set CODEBRIX_WEBGPU_MODEL_CORPUS to one
or more folders (path-list separated, e.g. the Kenney "3D assets" folder and
a Poly Haven download) and optionally CODEBRIX_WEBGPU_MODEL_CORPUS_REPORT to a
file for the per-pack timing summary; it is skipped otherwise.
ModelPbrTests compare the model renderer with a CPU reference of the glTF BRDF
(lit spheres) and with known answers (emissive, unlit, mask, sorted blending,
normal-map orientation for file / generated / derivative tangents, texture
transforms, double-sided faces, lights, ambient, exposure, sRGB vs UNORM
targets, the environment hook, pipeline caching); ModelSkinningTests pose the
committed archer (bind pose = unskinned draw, a joint pose against CPU
skinning, a root rotation against the same world rotation); ModelMorphTests
use an in-test morph quad. AnimationSamplingTests check channel evaluation
(STEP, LINEAR, CUBICSPLINE, quaternion edge cases, the key cursor) without a
GPU; AnimationPlayerTests play clips of an in-test glTF (loop / once /
ping-pong, cross-fades, layers, hemisphere-corrected blends, morph weights,
pose edits) and the archer (zero allocation per update);
ModelAnimationRenderTests render the animated indicator and archer against
the same poses set by hand; ModelMirroringTests cover mirroring node and
instance transforms. RadianceHdrDecoderTests decode in-test .hdr bytes (flat,
old-style and new-style RLE, orientation, header errors) and the committed CC0
small_cathedral_1k.hdr; EnvironmentMapTests read the baked cubes back as half
floats (constant environments, a bright direction per face, irradiance of a
half-bright sky vs (1 + n.y) / 2, prefiltered mip 0 = radiance, mean kept per
mip, the BRDF LUT vs a double-precision CPU integration, the procedural sky's
shape and sun units, panorama sources); ModelEnvironmentTests run white
furnaces (dielectric and metal, three roughnesses: every pixel = the furnace
value), mirror-sphere reflections, rotation, intensity, clear coat, the
default sky, the bind-group cache and the HDR path vs the direct path;
SkyboxRendererTests and ToneMapperTests check the background and the
tone-map operators (CPU known values, GPU = CPU). Tests that assert direct
lighting set Ambient = AmbientLighting.Hemisphere (the default is the
image-based default sky). Opt-in, skipped otherwise: CODEBRIX_WEBGPU_POLYHAVEN
(a Poly Haven download folder, read in place) runs the cannon_01 morph test;
CODEBRIX_WEBGPU_RENDER_OUTPUT (a folder) receives PNG renders of the test
models (plus a few Poly Haven models when CODEBRIX_WEBGPU_POLYHAVEN is set)
and four-frame animation strips; CODEBRIX_WEBGPU_MODEL_TIMING (a file)
receives per-frame costs for many instances; CODEBRIX_WEBGPU_ANIMATION_TIMING
(a file) receives animation update costs for many instances. With
CODEBRIX_WEBGPU_RENDER_OUTPUT set, EnvironmentGalleryTests also render the
models and a metal / roughness sphere grid under small_cathedral (skybox, HDR
path) and under the procedural sky; CODEBRIX_WEBGPU_ENVIRONMENT_TIMING (a file)
receives environment build times (LUT, decode, bakes at several cube sizes)
and per-frame costs with and without an environment.


THE BINDING GENERATOR
=====================

    dotnet run --project tools/CodeBrix.WebGpu.BindingGenerator -- --check
    dotnet run --project tools/CodeBrix.WebGpu.BindingGenerator -- --write
        [--repo <repository root>]   (default: the folder holding the .slnx)

--check regenerates in memory and exits 1 (printing the first differing lines
of each file) when a committed output differs or is missing, when a stale
*.cs sits in Generated/ (or a stale file in src/CodeBrix.WebGpu.Native/
include/), or when the HAND-WRITTEN wgpu-native-tools/codebrix-wgpu/include/
codebrix_wgpu.h disagrees with specs/codebrix.yml ("HAND-WRITTEN ...": the
API-version define, every enum entry + value, every struct member declaration,
every WGPU_EXPORT declaration and WGPUProc typedef, rendered from the model in
webgpu.h's style and compared ignoring whitespace and WGPU_NULLABLE; nothing
extra may be declared). That header ships to native-module authors, so fix it
by hand (it is also an input of the native build) - the generator never
writes it. --write writes only the files whose bytes changed.
Exit 2 = invalid input (the message names the file and the YAML path of the
offending node). Dependencies: CodeBrix.YamlParse + CodeBrix.Templating only.

INPUTS, in merge order (the core spec must be first, as in the Go generator):
    wgpu-native-tools/wgpu-native/ffi/webgpu-headers/webgpu.yml
    wgpu-native-tools/specs/wgpu.yml
    wgpu-native-tools/specs/codebrix.yml
  validated first against wgpu-native-tools/wgpu-native/ffi/webgpu-headers/
  schema.json by a small JSON-Schema validator over YamlParse's node model
  (YAML 1.2 core typing: a quoted '0x0003' is a STRING and fails an integer
  property - write numbers unquoted).
  Plus wgpu-native-tools/specs/proc-table-notes.txt (one '<C symbol or
  pattern> <note>' per line, '*' wildcard, '#' comments; every entry must
  match an exported function or the generator refuses it - exit 2). Keep it
  in step with the LIMITS section of AGENT-README.txt.

OUTPUTS (all start with a banner: tool name, sha256 of schema.json, of each
YAML and of proc-table-notes.txt, "DO NOT EDIT"; LF line endings;
byte-deterministic):
    src/CodeBrix.WebGpu.Native/Generated/Enums.g.cs, Bitflags.g.cs,
      Structs.g.cs, Callbacks.g.cs, Functions.g.cs, Handles.g.cs,
      Constants.g.cs, StructLayouts.g.cs, ModuleInterop.g.cs
    src/CodeBrix.WebGpu.Native/include/codebrix_wgpu_procs.h
    src/CodeBrix.WebGpu.Native/include/codebrix_wgpu_procs.rs
    wgpu-native-tools/specs/expected-exports.txt  ('#' lines are comments)

THE NATIVE-MODULE OUTPUTS (Rendering/ModuleRenderer.cs, from ONE model -
Model/ModuleInterop.cs - so the three cannot drift):
  * The function table: one entry per exported function in ordinal order of
    the C symbol (= expected-exports.txt order), after a 24-byte header {u32
    size, apiVersion, functionCount, reserved; u64 signatureHash}.
    signatureHash = FNV-1a 64 of "ret wgpuName(type, type)" (C types, one
    line each, '\n'-terminated) in table order. Rendered as the C# struct
    CodebrixProcTable (delegate* unmanaged[Cdecl] fields named like the
    WebGpu methods, EntryCount / ExpectedSignatureHash / HeaderSize / Symbols),
    the C typedef WGPUCodebrixProcTable (function-pointer members, camelCase =
    the symbol without "wgpu") and the Rust #[repr(C)] struct (Option<unsafe
    extern "C" fn(..)> members, same names). Notes from proc-table-notes.txt
    become comments on each entry.
  * The hand-off structs WGPUCodebrixDeviceHandoff / WGPUCodebrixFrameHandoff
    (field lists in Model/ModuleInterop.cs; C# halves generated as partial
    structs, Fill helpers hand-written in src/.../Modules/). A field change
    bumps the struct's VERSION constant in ModuleInterop.cs.
  * C types come from TypeRef (Model/ApiModel.cs), set beside every C# type
    in ApiModelBuilder (the gen.go CType rules: T const * / T *, WGPUBool,
    size_t, arrays as count + T const *). Layouts for the asserts come from
    Model/LayoutCalculator.cs (64-bit natural alignment).
  * Checks: the C header carries _Static_assert size/offset checks (any
    compiler that includes it verifies them); the Rust file asserts size +
    alignment of EVERY struct and the table/hand-off offsets
    (cfg(target_pointer_width = "64")). Tests: ModuleInteropTests (order,
    hash, notes, renderings; the rendered C declarations equal webgpu.h's and
    wgpu.h's), CodebrixHeaderCheckTests, and in the Native tests
    CodebrixProcTableTests (every entry == NativeLibrary.GetExport of the
    loaded handle, slot offsets and function-pointer signatures == the
    LibraryImports, the generator's layouts == the managed structs, calls and
    a GPU round trip through the table) and CodebrixHandoffTests (layouts ==
    the header's asserts).
  * Not run by the gate but worth doing after a generator change (verified
    2026-10-13 with gcc/clang/g++ and rustc 1.99): compile a file that
    includes codebrix_wgpu_procs.h (with webgpu.h, wgpu.h, codebrix_wgpu.h
    beside it) as C11 and C++17, and assign &wgpuX to every table member in
    C++ (exact type check); rustc --crate-type lib a module that includes the
    .rs, with and without --cfg codebrix_wgpu_direct, assigning
    Some(direct::wgpuX) to every member.

MERGE RULES (the reference Go generator's validator.go): duplicate constants,
structs, callbacks, functions, enum/bitflag entries and object methods are
errors; re-declaring an enum / bitflag / object requires `extended: true`;
extended bitflag entries need explicit values; an extension struct's `extends`
must name a declared struct. Stricter than Go: unresolved type references and
`extended` without a base are errors too.

MAPPING RULES (C -> C#), all in tools/.../Model/ApiModelBuilder.cs:
  * Names: the Go generator's PascalCase (upper-case the first character and
    each character after '_', drop those '_', keep every other character's
    case; "__" yields a literal '_'). C type = WGPU + PascalCase(name); C# type
    = PascalCase(name) - the WGPU prefix becomes the namespace. Enum value C
    name = WGPUEnum_Entry; C# member = PascalCase(entry), prefixed with the
    enum's last word when it starts with a digit (TextureDimension.Dimension2D).
    Parameters: camelCase, C# keywords escaped with '@'. The extension prefix is
    OFF (the Go generator's -ext-prefix=false), so wgpu/codebrix names are not
    prefixed: codebrix_get_api_version -> wgpuCodebrixGetApiVersion.
  * Functions: global `functions` -> wgpu<Name>; object methods ->
    wgpu<Object><Method> with the object handle first; every non-extended
    object gets <Object>AddRef / <Object>Release; every struct with
    free_members gets wgpu<Struct>FreeMembers(struct BY VALUE); webgpu.h's
    hand-written wgpuGetProcAddress is added. Each is
    `[LibraryImport("codebrix_wgpu", EntryPoint = "<C name>")] public static
    partial <ret> <CNameWithoutWgpu>(...)` on `public static unsafe partial
    class WebGpu`. SetLastError false, nothing marshalled (the assembly has
    DisableRuntimeMarshalling). A function with `callback:` returns Future and
    takes <Callback>CallbackInfo by value as its last parameter.
  * Types: bool -> uint (WGPUBool); the three string kinds -> StringView
    {byte* Data; nuint Length}; uint16/32/64 -> ushort/uint/ulong; usize ->
    nuint; int16/32 -> short/int; float32 (+nullable) -> float; float64
    (+supertype) -> double; c_void -> void (only behind a pointer); enum.x /
    bitflag.x / struct.x / object.x -> the generated type; typedef.x -> its
    primitive; `pointer:` adds '*' (const or not). array<T> -> nuint
    <singular>Count + T* <name>, count FIRST, the data always a pointer (even
    without `pointer:` - matches the native header's rendering).
  * Structs: [StructLayout(Sequential)] public unsafe struct, members in C
    order. extensible / extensible_callback_arg: first field ChainedStruct*
    NextInChain. extension: first field ChainedStruct Chain, plus
    `public const SType StructType` when SType has the matching member.
    StringView and ChainedStruct {Next; SType} are built-ins (the Go template
    hand-writes them). A callback.x member -> <X>CallbackInfo.
  * Enums: `public enum X : uint`; value = block << 16 | (explicit value or
    entry index; null entries reserve an index). The block comes from the
    entry's namespace, else its enum's namespace, else the file: the file's own
    name -> its enum_prefix; `webgpu` -> 0x0000; `compatibility_mode` ->
    0x2000 (schema.json: an entry namespace applies "both its name prefix and
    its enum block"). Force32 is never emitted. `extended` enums merge into the
    base enum (SType gets the wgpu/codebrix tags).
  * Bitflags: [Flags] enum X : ulong (WGPUFlags is uint64_t); implicit value of
    entry i = 2^(i-1) (entry 0 = 0), value_combination = OR of the named
    entries. Names are the WebGPU names (BufferUsage, ShaderStage - no "Flags"
    suffix).
  * Callbacks: the function pointer type is
    delegate* unmanaged[Cdecl]<args..., void* userdata1, void* userdata2, void>
    and <X>CallbackInfo = {ChainedStruct* NextInChain; [CallbackMode Mode - only
    style callback_mode]; Callback; void* Userdata1; void* Userdata2}. LEGACY
    rule: a callback whose LAST declared argument is named `userdata` (wgpu.h's
    WGPULogCallback) keeps exactly its declared arguments, gets no userdata1/2
    and no info struct; a function argument of type callback.x is that bare
    function pointer.
  * Handles: one `public readonly struct <Object> : IEquatable<>` per
    non-extended object, { IntPtr Handle; ctor; Null; IsNull; Equals;
    GetHashCode; ToString; == / != }.
  * Constants: `public static class Constants`; usize_max -> static readonly
    nuint (nuint.MaxValue is not a constant), uint32_max / uint64_max / numbers
    -> const uint / ulong, nan -> const float.
  * Docs: YAML `doc` -> XML. @ref X / \ref X -> <see cref> when X is a
    generated symbol (type, enum value, function, constant, Struct::member),
    <c>X</c> for other WGPU/wgpu names, plain text for documentation-page
    names; `code` -> <c>; [text](link) -> text; **bold** / *em* -> text; XML
    escaped; blank lines -> <para>. Empty / "TODO" docs become a one-line
    summary naming the C symbol ("Mirrors <c>WGPUFoo_Bar</c>.").
  * StructLayouts.g.cs lists EVERY struct (spec structs, the two built-ins and
    every callback-info struct) by C name; the native's
    wgpuCodebrixGetStructLayout must know all of them.

TESTS OF THE GENERATOR (tests/CodeBrix.WebGpu.BindingGenerator.Tests):
fixture YAMLs (Fixtures/webgpu.yml + ext.yml) cover enum blocks, bitflags,
chained structs, callback info, string views, optional pointers, 64-bit and
size_t fields, arrays, typedefs and the legacy callback; the model tests pin
the rules above; CSharpRendererTests compare every output with
Golden/<file>.golden. After an INTENDED template change:

    CODEBRIX_WEBGPU_UPDATE_GOLDEN=1 dotnet tests/CodeBrix.WebGpu.BindingGenerator.Tests/bin/Debug/net10.0/CodeBrix.WebGpu.BindingGenerator.Tests.dll
    (rebuild, run again without the variable, review the Golden/ diff)


THE SPECIFICATIONS
==================

specs/wgpu.yml - HAND-MAINTAINED. The release has no wgpu.yml; this file is a
schema-valid transcription of the release's ffi/wgpu.h (all 26 functions, every
struct, enum, bitflag, the SubmissionIndex typedef and the log callback), with
the header's own comments as docs. Its header comment records its provenance
(trunk commit 6f7d734be9bb5d1b3f0f48c9eb3019eec1eaa21a's wgpu.yml, which was
neither schema-valid nor the release API) and the transcription rules (acronym
casing, `namespace: webgpu` for wgpu.h's 0-based enums, the display-handle
union typed as its largest variant, const uint8_t* as c_void, Force32 omitted).
HeaderParityTests parse the release webgpu.h and wgpu.h with a small C scanner
and require, in BOTH directions, every function name + parameter count and
every struct name + member list (array members as count + data, chain headers
included) to match the model. After a bump: run the generator tests and edit
wgpu.yml until parity passes.

specs/codebrix.yml - THE contract of the codebrix_wgpu extras (enum_prefix
0x000C). Change it deliberately: any change of shape or meaning bumps the
`api_version` constant (the managed side refuses a native whose
wgpuCodebrixGetApiVersion differs from Constants.ApiVersion), and any added
function changes the export set, so all nine natives must be rebuilt before
the next publish.

specs/expected-exports.txt - GENERATED. The native gate compares every built
library's export table with it (ignoring '#' lines): nothing missing, nothing
extra. A binding importing a function no native exports, or a native exporting
one the binding lacks, fails at gate time.


NATIVE LIBRARIES
================

One native per RID, named codebrix_wgpu, built from wgpu-native-tools/
codebrix-wgpu (our cdylib; links wgpu-native as an rlib via the scratch-only
patch and re-exports its symbols; adds the wgpuCodebrix* extras). Beside every
binary: LICENSE-WgpuNative.txt (identical bytes on every RID). Details, gates
and build records: wgpu-native-tools/README.txt and BUILD-PROVENANCE.txt.

    RID             built by        where / how
    linux-x64       agent/maint.    wgpu-native-tools/linux (container, manylinux 2.28)
    linux-arm64     agent/maint.    wgpu-native-tools/linux (container, manylinux 2.28)
    linux-riscv64   agent/maint.    wgpu-native-tools/linux (manylinux 2.39 sysroot)
    android-arm64   agent/maint.    wgpu-native-tools/android (NDK, API 33, 16 KB pages)
    android-x64     agent/maint.    wgpu-native-tools/android
    win-x64         JEREMY          wgpu-native-tools/windows/build-win-x64.ps1 (MSVC)
    win-arm64       JEREMY          wgpu-native-tools/windows/build-win-arm64.ps1 (MSVC)
    osx-arm64       JEREMY          wgpu-native-tools/macos/build-osx-arm64.sh
    osx-x64         JEREMY          wgpu-native-tools/macos/build-osx-x64.sh

The four Windows/macOS scripts check and NAME their prerequisites (rustup with
the pinned toolchain and target, LLVM/libclang for bindgen, VS C++ tools /
Xcode CLT); they never install anything. Run them as:
    cd wgpu-native-tools\windows ; .\build-win-x64.ps1 ; .\build-win-arm64.ps1
    cd wgpu-native-tools/macos   && ./build-osx-arm64.sh && ./build-osx-x64.sh
(Linux and Android: wgpu-native-tools/linux/build.sh, android/build.sh; all
commands and the per-RID gate in wgpu-native-tools/README.txt.) Publish only when all nine are
committed under src/CodeBrix.WebGpu.Native/runtimes/.

wgpuGetVersion reads WGPU_NATIVE_VERSION at COMPILE time; every build must set
it (v29.0.1.1), or the library reports 0 and the version test fails.

crates.io RULING: crates on crates.io are treated like NuGet packages -
Cargo.lock IS the pin (version + checksum per crate). Builds run `cargo fetch`
with the network on, then compile offline. `cargo update` is never run
casually: a lock change is a deliberate, recorded upstream bump. Anything NOT
on crates.io (wgpu-native itself, webgpu-headers) is vendored unmodified.


BUMPING WGPU
============

The whole point of the tooling: a new wgpu-native release is a sequence of
commands, with hand edits only where the C API itself changed.

  1. wgpu-native-tools/refresh-upstream.sh <wgpu-native tag>
       downloads the tag + the webgpu-headers commit it pins, records sha256s
       (UPSTREAM.txt, upstream-downloads/), replaces the vendored trees,
       re-applies patches/ to a scratch copy (fails loudly if one no longer
       applies) and prints the function/struct/enum delta.
  2. Edit wgpu-native-tools/specs/wgpu.yml for whatever wgpu.h changed, until
         dotnet test tests/CodeBrix.WebGpu.BindingGenerator.Tests (HeaderParityTests)
     passes. (webgpu.yml needs no edits - it is upstream's.)
  3. dotnet run --project tools/CodeBrix.WebGpu.BindingGenerator -- --write
     (if the YAML SCHEMA itself changed, the generator model is the one place
     to fix: Model/SpecFileReader.cs + Model/ApiModelBuilder.cs, then the
     golden files.) This also regenerates the native-module function table,
     codebrix_wgpu_procs.h / .rs - an added or changed function changes the
     table's count / signature hash, so modules must be rebuilt against the
     new package; review proc-table-notes.txt against the engine's new
     limits (an entry naming a removed function fails the generator).
  4. Update the codebrix-wgpu crate for the new wgpu-core/hal APIs; if
     codebrix.yml changes, bump its api_version and edit codebrix_wgpu.h to
     match (--check names every difference).
  5. wgpu-native-tools/build-host.sh   (host build + smoke test)
  6. dotnet test --solution CodeBrix.WebGpu.slnx   (Debug and Release; every
     struct layout, the export list, the GPU round trips)
  7. The nine builds (five here, four by Jeremy), each through the gate
     (exports == expected-exports.txt, dependencies, floor, licence file,
     smoke test, reproducibility); adopt into src/.../runtimes/.
  8. Update the version facts in this file, THIRD-PARTY-NOTICES.txt and
     wgpu-native-tools/README.txt; build Release; check the nupkg; publish.


PACKAGING / PUBLISHING
======================

The library csproj carries the canonical date-stamped version block (1.<years
since 2026>.<day of year>.<minute of day>, UTC); GeneratePackageOnBuild makes a
fresh nupkg on every build. The package contains lib/net10.0 (dll + xml),
README.md, AGENT-README.txt, THIRD-PARTY-NOTICES.txt, LICENSE,
icon-codebrix-128.png, runtimes/<rid>/native/{native, LICENSE-WgpuNative.txt}
and include/ (webgpu.h, wgpu.h, codebrix_wgpu.h straight from
wgpu-native-tools/, the generated codebrix_wgpu_procs.h / .rs, and
LICENSE-WgpuNative.txt from wgpu-native-tools/licenses/ - it carries the
webgpu.h BSD-3 and wgpu-native MIT texts). include/ is in DefaultItemExcludes
like runtimes/, and is a root folder NuGet gives to no consumer build.
The runtimes item uses PackagePath="runtimes\" BARE - spelling %(RecursiveDir)
out as well doubles the path; check the nupkg after any csproj change
(PackageContentTests). The generator and the test projects are not packable.
Jeremy publishes.

PUBLISHING THE SKIA PACKAGE. src/CodeBrix.WebGpu.Skia has a switch:

  * default (no property): ProjectReference to ../CodeBrix.WebGpu.Native. This
    is how the repository builds and tests. A package packed this way depends
    on CodeBrix.WebGpu.Native.MitLicenseForever at the version the Native
    project computes AT THAT PACK (the date-stamped minute) - a version that
    was never published. Never publish a Skia package packed this way.
  * -p:WebGpuNativePackageVersion=<v>: PackageReference to the PUBLISHED
    CodeBrix.WebGpu.Native.MitLicenseForever <v> instead; the nuspec then
    carries version="<v>" (NuGet's usual minimum-version form, >= <v>).

  Order: (1) publish CodeBrix.WebGpu.Native (note its version V); (2) wait
  until nuget.org lists V; (3)
      dotnet pack src/CodeBrix.WebGpu.Skia -c Release -p:WebGpuNativePackageVersion=V -o <folder>
  (4) check the nuspec: dependencies CodeBrix.WebGpu.Native.MitLicenseForever
  V and SkiaSharp 4.153.1, nothing else; (5) publish. Afterwards run a plain
  `dotnet restore` so obj/ is back on the ProjectReference form.
  Before V exists on nuget.org step (3) fails at restore with
      error NU1101: Unable to find package CodeBrix.WebGpu.Native.MitLicenseForever.
      No packages exist with this id in source(s): ..., nuget.org
  (or NU1102 "Unable to find package ... with version (= V)" once some other
  version exists). Verified 2026-10-11: with a scratch folder feed holding a
  locally packed Native 1.0.284.372, the switch produced a Skia nuspec that
  depends on exactly 1.0.284.372 and SkiaSharp 4.153.1. Re-verified
  2026-10-13 for Skia and Graphics with a scratch Native 1.0.0-check (feed via
  -p:RestoreAdditionalProjectSources=<folder>, packages via
  -p:RestorePackagesPath=<scratch folder> so the global cache stays clean; a
  prerelease V only adds NuGet warning NU5104).


PUBLISHING THE GRAPHICS PACKAGE. src/CodeBrix.WebGpu.Graphics has the same
switch as the Skia package (default ProjectReference to the Native project;
-p:WebGpuNativePackageVersion=<v> turns it into a PackageReference to the
published Native package). Same order: publish Native V, wait for nuget.org,
    dotnet pack src/CodeBrix.WebGpu.Graphics -c Release -p:WebGpuNativePackageVersion=V -o <folder>
check the nuspec (exactly three dependencies: CodeBrix.WebGpu.Native.MitLicenseForever
V, CodeBrix.Imaging.ApacheLicenseForever 1.0.287.220 and
CodeBrix.Graphics3D.Gltf2.MitLicenseForever 1.0.261.293), publish, then a plain
`dotnet restore`. The Graphics package does NOT depend on
the Skia package, so the two can be published in either order after Native.


THE PUBLISH SEQUENCE (all three packages, from the repository root)
  Never `dotnet nuget push` from an agent session - Jeremy publishes.
  0. Gate: Release + Debug builds 0 warnings / 0 errors, `dotnet test
     --solution CodeBrix.WebGpu.slnx` in both configurations (0 failed; only
     the documented skips), `dotnet run --project
     tools/CodeBrix.WebGpu.BindingGenerator -- --check` clean, the
     forbidden-word grep empty. Commit and push; the nuspec records the HEAD
     commit.
  1. dotnet pack src/CodeBrix.WebGpu.Native -c Release -o <folder>
     -> CodeBrix.WebGpu.Native.MitLicenseForever.<V>.nupkg (about 23 MB).
     Check: NO dependencies; lib/net10.0/CodeBrix.WebGpu.Native.dll + .xml;
     README.md (# CodeBrix.WebGpu.Native), AGENT-README.txt,
     THIRD-PARTY-NOTICES.txt, LICENSE, icon-codebrix-128.png;
     runtimes/<rid>/native/ for all nine RIDs, each native beside
     LICENSE-WgpuNative.txt; include/ = webgpu.h, wgpu.h, codebrix_wgpu.h,
     codebrix_wgpu_procs.h, codebrix_wgpu_procs.rs, LICENSE-WgpuNative.txt;
     nothing else (no pdb, no test assets). Publish it; note V.
  2. Wait until nuget.org lists V (the next restores need it).
  3. dotnet pack src/CodeBrix.WebGpu.Skia -c Release -p:WebGpuNativePackageVersion=V -o <folder>
     Check: dependencies = CodeBrix.WebGpu.Native.MitLicenseForever V and
     SkiaSharp 4.153.1 only; lib dll + xml and the five documents (README.md
     = src/CodeBrix.WebGpu.Skia/README.md); no runtimes/. Publish.
  4. dotnet pack src/CodeBrix.WebGpu.Graphics -c Release -p:WebGpuNativePackageVersion=V -o <folder>
     Check: dependencies = CodeBrix.WebGpu.Native.MitLicenseForever V,
     CodeBrix.Imaging.ApacheLicenseForever 1.0.287.220 and
     CodeBrix.Graphics3D.Gltf2.MitLicenseForever 1.0.261.293 only; lib dll +
     xml and the five documents (README.md =
     src/CodeBrix.WebGpu.Graphics/README.md); no runtimes/. Publish.
     (3 and 4 are independent of each other.)
  5. `dotnet restore CodeBrix.WebGpu.slnx` so obj/ is back on the
     ProjectReference form; tag the commit as usual.
  Inspect a nupkg with `unzip -l <file>` and `unzip -p <file> '*.nuspec'`.
  The PackageContentTests classes (one per package test project) assert the same against
  the newest nupkg in each project's bin/<configuration>/ (the ProjectReference
  form, so their Native dependency check only asserts "1."): exact file set,
  documents byte-identical to their sources, metadata (id, date-stamped
  version, MIT expression, icon, readme, repository URL) and dependencies.

VERSIONS AND DEPENDENCY PINS
  Package versions need no bump: every pack stamps 1.<years since
  2026>.<day of year>.<minute of day> (UTC). Two packs in one UTC minute get
  the same version - never publish two from one minute.
  A dependency pin moves deliberately, in one change: the csproj
  PackageReference, the PackageContentTests dependency assertion, this
  file (PURPOSE AND SCOPE, the publish sequence above) and, for SkiaSharp,
  the test / sample SkiaSharp.NativeAssets.* versions (same version as
  SkiaSharp). The package docs name no versions.
  JPEG XL FOLLOW-UP: DONE. The CodeBrix.Imaging pin moved from 1.0.285.619 to
  1.0.287.220 (JPEG XL stills, animation and HDR). Graphics decodes JPEG XL
  textures to sRGB (Images/ImageUpload.JxlTextureDecoder: TargetColor Srgb,
  PQ/HLG tone mapped for a 255-nit display), reads JPEG XL frame durations
  in AnimatedTexture.ReadDurations (ticks at the file's ticks-per-second),
  and decodes .jxl environments to linear light with the HDR range kept
  (ImageUpload.JxlHdrDecoder + the extended inverse sRGB curve). Every JXL
  test file is made in-test with Imaging's encoder (tests/.../TestJxl.cs).
  Still not animated by Imaging: animated WebP (refused by its decoder) and
  APNG (the PNG decoder reads the default image only). Releasing this = the
  usual gate, then republish Graphics only (against the already published
  Native).


CODING CONVENTIONS
==================

The CodeBrix family rules: net10.0 only; NRT off (no `?` on reference types,
no `!`); no ImplicitUsings, no global usings; file-scoped namespaces; usings at
the top, System first; XML docs on every public member; no NoWarn, no pragma
suppressions; no third-party NuGet in the Native library (the Skia library
references SkiaSharp only - never SkiaSharp.* siblings or native assets).
Tests: <Class>Tests.cs,
snake_case names (Member_does_x or plain snake_case), //Arrange //Act //Assert,
expression bodies for one-statement tests, SilverAssertions,
TestContext.Current.CancellationToken on cancellable calls. Generated code
follows the same rules - fix the templates, never the output.


THE SKIA PACKAGE
================

Goal (Jeremy): an SKSurface from any C# application on a supported platform is
handed to the library and WebGPU writes into it as fast as possible; a surface
created on a GRContext over Vulkan, Metal or Direct3D 12 gets the zero-copy
path. The library picks the tier and REPORTS it.

SkiaSharp facts that shaped the design (probed by reflection over SkiaSharp
4.153.1, net10.0):
  * GRContext.CreateVulkan / CreateMetal / CreateDirect3D exist; the Linux and
    Windows natives contain the Vulkan backend, Windows also Direct3D, macOS
    Metal (macOS has no Vulkan in SkiaSharp).
  * GRVkBackendContext: VkInstance, VkPhysicalDevice, VkDevice, VkQueue,
    GraphicsQueueIndex (the FAMILY), MaxAPIVersion, Extensions
    (GRVkExtensions), VkPhysicalDeviceFeatures / Features2, GetProcedureAddress
    (delegate(string name, IntPtr instance, IntPtr device)), ProtectedContext.
    GRD3DBackendContext: Adapter, Device, Queue, ProtectedContext.
    GRMtlBackendContext: DeviceHandle, QueueHandle.
  * GRBackendTexture(w, h, GRVkImageInfo) / (w, h, GRD3DTextureResourceInfo) /
    (w, h, mipmapped, GRMtlTextureInfo); SKSurface.Create(GRRecordingContext,
    GRBackendTexture, origin, colorType); SKImage.FromTexture /
    FromAdoptedTexture.
  * MISSING: no way to get the texture behind an SKSurface or SKImage
    (no GetBackendTexture / GetBackendRenderTarget; GRBackendTexture /
    GRBackendRenderTarget only return GL info); no mutable-state API (Skia's
    setBackendTextureState, flush with a new MutableTextureState) - the
    Vulkan layout / D3D12 state Skia leaves a texture in can neither be read
    nor requested; GRVkExtensions cannot list its extensions. SkiaSharp also
    exposes Graphite (SKGraphiteContext, incl. a Vulkan backend texture with
    an explicit layout) - not used; Ganesh (GRContext) is what applications
    have.

Tiers (WebGpuSkiaTier): Unavailable < Headless < Readback < GpuCopy < ZeroCopy.
  ZeroCopy  the SKSurface is created by the library over a wgpu texture on the
            shared device (Create(device, grContext, w, h)), or the app
            describes its surface's native texture (WebGpuSkiaSurfaceBacking)
            and the library wraps it with CreateTextureFrom* (initial state
            Undefined - the only one this native accepts - so the surface's
            content at creation is not preserved).
  GpuCopy   app surface on the shared GRContext: wgpu renders into its own
            texture; SkiaSharp draws a cached SKImage over that texture onto
            the surface (BlendMode.Src).
  Readback  everything else: texture -> buffer -> MapAsync -> drawn from the
            mapped memory.
  ReadbackCount counts every GPU-to-CPU transfer; tests assert 0 for ZeroCopy
  and GpuCopy.

Synchronisation model (one native queue shared by wgpu and Skia on the owned
route, so submission order orders the GPU work; barriers handle memory):
  * BeginWebGpuPass: GRContext.Flush() + Submit(sync = !SharesQueueWithSkia),
    then a fresh wgpu encoder. ZeroCopy adds TransitionTexture(CopySrc) +
    TransitionTexture(ColorTarget): Skia leaves the image in COLOR_ATTACHMENT,
    which wgpu already believes, so wgpu would record NO barrier before its
    pass - the detour records the barrier that orders Skia's writes before
    wgpu's (two transitions per frame; the timing test includes them).
  * EndWebGpuPass: ZeroCopy -> TransitionTexture(ColorTarget) (what Skia was
    told when the surface was wrapped: VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL
    / D3D12_RESOURCE_STATE_RENDER_TARGET); GpuCopy -> ShaderResource (Skia's
    SKImage was wrapped as SHADER_READ_ONLY_OPTIMAL / PIXEL_SHADER_RESOURCE, so
    sampling needs no Skia-side transition and both sides agree); submit.
    Not sharing a queue (adopted Metal): DevicePoll(wait) after the submit.
  * New textures are cleared once by wgpu at creation: otherwise wgpu's lazy
    zero-init would wipe whatever Skia drew before the first wgpu pass.
  * The one rule the app must keep: in ZeroCopy, between passes only DRAW into
    the surface (a Skia ReadPixels/Snapshot moves the image to TRANSFER_SRC /
    SHADER_READ and neither side can tell the other). Fix would need the
    SkiaSharp mutable-state API.
  * Skia on the owned Vulkan route is given MaxAPIVersion = min(reported, 1.1)
    and an all-false VkPhysicalDeviceFeatures (the engine does not report
    which core features it enabled). With the reported 1.4 Skia asked for
    vkTransitionImageLayout/vkCopyMemoryToImage, got NULL and refused the
    context (FIXLIST_webgpu_found_along_the_way_2026-10-11.txt).

Per-platform backend table:
  Linux/Android  owned: Vulkan, ZeroCopy (proven here, Intel UHD / Mesa)
                 adopted: AdoptVulkan, ZeroCopy + GpuCopy (proven here)
  Windows        owned: Direct3D 12 (default) - ZeroCopy, UNTESTED here;
                 owned: Vulkan (PreferredBackend = Vulkan) - ZeroCopy
                 adopted: AdoptVulkan works like Linux; AdoptDirect3D always
                 unavailable (the native's adopt returns Error - known limit)
  macOS          owned: Metal - ZeroCopy, UNTESTED here
                 adopted: AdoptMetal - wgpu gets its own MTLCommandQueue, so
                 hand-overs wait (SharesQueueWithSkia = false), UNTESTED here

What Jeremy runs on the other machines (from the repository root):
  Windows (D3D12 primary; Vulkan secondary + Vulkan owned when a Vulkan
  driver is present):
      dotnet build -c Release CodeBrix.WebGpu.slnx
      dotnet test --solution CodeBrix.WebGpu.slnx -c Release
      (or) dotnet tests\CodeBrix.WebGpu.Skia.Tests\bin\Release\net10.0\CodeBrix.WebGpu.Skia.Tests.dll
    Expect: device on Direct3D12, GRContext Direct3D, ZeroCopy/GpuCopy/
    Readback/backing tests green, AdoptVulkan + Create_with_vulkan_preferred
    green or skipped with a reason, AdoptDirect3D test asserting the
    "not supported by this native library" reason, AdoptMetal skipped.
  macOS (Metal):
      dotnet build -c Release CodeBrix.WebGpu.slnx
      dotnet test --solution CodeBrix.WebGpu.slnx -c Release
    Expect: device on Metal, GRContext Metal, AdoptMetal_shares_the_
    applications_device runs, AdoptVulkan tests skip (no Vulkan backend).
  Send back: the test summary and bin/Release/net10.0/skia-tier-timing.txt.


THE GRAPHICS PACKAGE
====================

Goal (Jeremy): a high-level graphics API over WebGPU - resources, pipelines, a
fast 2D path and a basic 3D path - usable without raw binding calls, sharing a
device with the Skia package without referencing it.

Design decisions:
  * Context: GraphicsContext.Create / TryCreate request instance (backends
    from GraphicsBackend; Default = Primary), adapter and device with an
    uncaptured-error and a device-lost callback. Callbacks carry an integer id
    (Interop/ContextRegistry, weak references) instead of a GCHandle, so a late
    callback never dereferences a freed handle. They only enqueue (the native
    holds the device's error lock while calling); events are raised from
    Poll / GraphicsFrame.Submit. Wrap(Device, Queue, Instance, Adapter) AddRefs
    every handle and Releases only its own references - dispose order against
    the owner does not matter - and installs no callbacks.
  * Errors: every object creation runs inside a Validation error scope
    (push / create / pop; the native calls the pop callback synchronously) and
    throws GraphicsException / ShaderCompilationException with the native
    message. wgpuShaderModuleGetCompilationInfo and every *SetLabel are
    unimplemented in this native (abort) - never call them; labels go in the
    descriptors.
  * Lifetimes: GraphicsResource (private protected constructor) registers with
    its context and a sequence number; Dispose untracks; context Dispose
    releases survivors newest first. Composite resources (SpriteBatch,
    MeshRenderer, Mesh, LitMaterial, HeadlessRenderTarget, WindowSurface)
    create their children after registering themselves, so children are
    released first and the composite's own release is a no-op for them.
  * Presentation abstraction: RenderTargetView, a struct of colour view +
    format + size (+ optional depth view + format). Pipelines are cached per
    target format combination by the renderers. GraphicsFrame either owns its
    encoder (Submit finishes and submits) or borrows one (Submit only closes) -
    the borrowed form is how a WebGpuSkiaTarget's BeginWebGpuPass encoder is
    used; no Skia types anywhere.
  * Per-frame data: queue writes land before the next submit, ahead of the
    frame's recorded commands, so the sprite batch and mesh renderer keep each
    Begin/End's vertices / instances / uniforms in its own region of a growing
    buffer (uniforms in 256-byte dynamic-offset slots), reset when a frame
    with a new Id begins. Growing a buffer mid-frame releases the old one; the
    recorded commands keep it alive.
  * Shaders: the sprite and lit-mesh WGSL sources (Shaders/BuiltInShaders.cs). The sprite
    shader multiplies the vertex colour by mix(1, texture, textured) so shapes
    (textured = 0) never break a texture run; the lit shader is Blinn-Phong
    with per-instance model + inverse-transpose rows, front_facing-flipped
    normals for double-sided materials.
  * Model shader (Shaders/PbrShader.cs, driven by Models/PbrModelRenderPath.cs):
    one WGSL source specialised per feature set by prepended `const` bools
    (skinned, morphed, normal map, unlit, mask, blend, double-sided,
    clearcoat, shader sRGB encode). Bind groups: 0 frame (dynamic-offset
    uniforms + irradiance cube, prefiltered cube, BRDF LUT and sampler; one
    cached bind group per EnvironmentMap in use, plus one with 1 x 1
    placeholders for the hemispheric ambient - the shader reads the maps only
    when frame.environment.x = 1; frame.environment = switch, intensity,
    prefiltered mip count, rotation about +Y; the internal
    PbrModelRenderPath.SetEnvironment is a test override with raw textures), 1 material
    (256-byte uniforms, five textures, five samplers; missing textures are the
    context's white texture), 2 per-frame storage (instance records, matrices
    = node matrices then joint palettes per instance, morph weights minus the
    baked defaults), 3 per model (dynamic-offset draw uniforms, the skin
    stream read as u32 words, packed morph deltas). The vertex shader reads
    instance data with instance_index (firstInstance selects the batch), so
    no instance vertex buffer is bound. The matrices buffer is declared as
    array<vec4<f32>> and read four columns at a time (load_matrix): the
    Adreno 640 Vulkan driver (Pixel 4 XL) read a runtime-sized storage array
    of bare mat4x4 wrongly (every model drew as garbage); PbrShaderTests keeps
    matrix arrays out of every variant. Normals use the cofactor matrix of the
    final model matrix. Lighting units: radiance = BRDF x light x PI x N.L, so
    intensity 1 lights a white Lambert surface to 1. Image-based ambient:
    irradiance(N) for the diffuse part, prefiltered(R, roughness x mips) x
    (F0 x LUT.r + LUT.g) for the specular part, plus Fdez-Aguera's
    multiple-scattering compensation (white furnaces stay white).
  * Environments (Lighting/): EnvironmentBaker renders full-screen triangles
    into one face (layer) and mip of RGBA16Float six-layer textures: radiance
    mip 0 from the equirectangular panorama (u = 0.5 + atan2(x, -z) / 2PI,
    v = 0.5 - asin(y) / PI, sampled at the lod matching the cube texel) or
    from the analytic sky; the radiance mip chain through GenerateMipmaps;
    irradiance = every texel of one small radiance mip (IrradianceSourceSize)
    weighted by solid angle x cosine and normalised by the weight sum (exact
    for constants, no noise); prefiltered mip m = GGX roughness m / (levels -
    1), filtered importance sampling (Hammersley; each sample reads the
    radiance lod 0.5 log2(sample solid angle / texel solid angle) + 1); mip 0
    samples the radiance at the matching lod. The BRDF LUT (128 x 128
    RG16Float, 4096 samples, height-correlated Smith - the direct-light terms)
    is rendered once per context. Cube layout +X, -X, +Y, -Y, +Z, -Z with
    texel row 0 at the top (EnvironmentShaders.face_direction; the tests carry
    the same table on the CPU). The skybox and tone map are full-screen
    triangles too (skybox at z = 1 with depth LessEqual, no writes).
  * Vertex layouts: reflection over the struct's fields ordered by
    Marshal.OffsetOf, stride Unsafe.SizeOf<T>; Matrix4x4 expands to four
    Float32x4 rows (read by WGSL as columns, so `m * v` = Vector4.Transform).
  * Matrices are System.Numerics row-vector matrices uploaded as-is.
  * Window surfaces: the native treats surface creation / configuration
    failures as fatal, so Configure validates size, format and present mode
    against the capabilities first. Capabilities need the adapter; a wrapped
    context without one falls back to BGRA8Unorm + Fifo.

The sample (samples/WebGpuModelViewer, and samples/WebGpuModelViewer.Android
which links its sources): one WebGpuSkiaDevice (owned route, GRContext on it)
+ GraphicsContext.Wrap(device); each frame is ONE context.BeginFrame(target)
on a zero-copy WebGpuSkiaTarget: the models (LoadModel with one shared
ModelTextureCache, ModelInstance per actor, AnimationPlayer.UpdateAll +
CrossFade routines, ModelRenderer) and the skybox render into an RGBA16Float
HeadlessRenderTarget, the ToneMapper (Khronos PBR Neutral) writes the Skia
target, the sprite-batch HUD follows in the same pass; SkiaSharp then draws
its overlay on the same texture. Lighting = a procedural sky baked once
(CreateProceduralSky, sun direction = the key light) + the key light;
--environment <file.hdr> swaps in a panorama, --ldr skips the float texture
(models + sky drawn straight into the UNORM target, shader-encoded),
--no-skybox clears to a flat colour. Headless reads the target back
(SKImage.Encode PNG); live wraps the target once (WrapTexture(target)) and
draws it as one full-screen sprite onto the window surface in a second
BeginFrame(target) frame. The ground is a one-quad glTF built in memory with
Gltf2's Schema2 toolkit. Live windows: CodeBrix.Sdl3 window properties ->
NativeWindowSource (x11 / wayland / windows / cocoa via SDL_Metal_CreateView +
GetLayer; Android: the ANativeWindow).

What Jeremy runs on the other machines (from the repository root):
      dotnet build -c Release CodeBrix.WebGpu.slnx
      dotnet test --solution CodeBrix.WebGpu.slnx -c Release
      (or) dotnet tests/CodeBrix.WebGpu.Graphics.Tests/bin/Release/net10.0/CodeBrix.WebGpu.Graphics.Tests.dll
      dotnet run --project samples/WebGpuModelViewer -c Release -- --headless frames --frames 24
      dotnet run --project samples/WebGpuModelViewer -c Release -- --seconds 10
      (either mode also takes --ldr, --no-skybox, --environment <file.hdr>)
    Windows: Direct3D 12 by default (the Skia device picks it); the live
    window uses the Win32 HWND path. macOS: Metal; the live window uses an SDL
    Metal view's CAMetalLayer. Linux Wayland desktop: add
    -- --video-driver wayland to force the Wayland path.


PROVENANCE / VENDORED SOURCES
=============================

wgpu-native-tools/wgpu-native/ (wgpu-native release, MIT OR Apache-2.0, used
as MIT) and its ffi/webgpu-headers/ (BSD-3-Clause) are vendored unmodified;
specs/wgpu.yml is a transcription of wgpu.h; the generator re-implements the
rules of webgpu-headers' Go generator. Details and licence texts:
THIRD-PARTY-NOTICES.txt and LICENSE-WgpuNative.txt.


NOTES
=====

  * A number of exported functions of this wgpu-native release are
    unimplemented!() and abort the process (Rust panic = abort): wgpuInstanceWaitAny,
    wgpuGetProcAddress, every *SetLabel, the *Async pipeline creators,
    wgpuDeviceGetAdapterInfo, wgpuShaderModuleGetCompilationInfo,
    wgpuBufferRead/WriteMappedRange, the instance-feature and WGSL-feature
    queries, ... (wgpu-native/src/unimplemented.rs + lib.rs). The binding still
    imports them (the export set is the API); AGENT-README lists them. Re-check
    the list on every bump.
  * The device-lost callback is only reached for a bare DeviceError::Lost;
    using a destroyed device reports a Validation error through the
    uncaptured-error callback. A Native test pins this behaviour so a bump that
    changes it is noticed.
  * wgpuDeviceCreateShaderModuleSpirV is passthrough and needs a feature the
    release does not expose; the test pins the reported validation error.
  * samples/WebGpuModelViewer and samples/WebGpuModelViewer.Android are the
    samples (see THE GRAPHICS PACKAGE); the Android one also proved the
    packaged Android natives load from the APK's lib/<abi>/ folders.

================================================================================
END OF MAINTAINER-README
================================================================================
