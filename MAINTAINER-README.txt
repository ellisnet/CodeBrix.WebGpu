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

The repository will produce THREE packages, all MIT, from one solution
(CodeBrix.WebGpu.slnx):

  CodeBrix.WebGpu.Native     the natives + the thin generated binding (this one)
  CodeBrix.WebGpu.Skia       wgpu <-> SkiaSharp: zero-copy into a GPU SKSurface
                             (Vulkan / Metal / Direct3D 12), readback otherwise
  CodeBrix.WebGpu.Graphics   the high-level, high-performance graphics API

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
                             Samples (empty for now).
  global.json                selects the Microsoft.Testing.Platform test runner;
                             pins no SDK.
  src/CodeBrix.WebGpu.Native/
      Generated/*.g.cs       GENERATED - never edit (see THE BINDING GENERATOR)
      CodeBrixWebGpuNative.cs, WebGpu.cs, WgpuString.cs, WgpuException.cs,
      StructLayoutInfo.cs, Interop/WgpuLibrary.cs (resolver),
      Interop/RuntimeMarshalling.cs ([assembly: DisableRuntimeMarshalling])
      runtimes/<rid>/native/ the committed natives + LICENSE-WgpuNative.txt
                             (adopted from wgpu-native-tools/output/ by agent /
                             maintainer after the gate; not built by MSBuild)
  tests/CodeBrix.WebGpu.Native.Tests/          the binding against a real native
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

Which native the Native tests load: the test project copies
src/CodeBrix.WebGpu.Native/runtimes/** into its output (exactly what the
package ships). While runtimes/linux-x64 has not been adopted yet, it copies
the developer host build wgpu-native-tools/output/host/libcodebrix_wgpu.so
(wgpu-native-tools/build-host.sh) into runtimes/linux-x64/native/ instead.

The GPU tests need a WebGPU adapter. Every one of them SKIPS (with the reason)
when none is found - a skip is not a pass. The development laptop has Intel
UHD Vulkan + llvmpipe; do not install software rasterisers to make tests pass.

The Native tests include the guards that tie the repository together:
  * GeneratedFilesDrift: Generated/*.g.cs and specs/expected-exports.txt are
    exactly what the generator produces from the committed inputs; every
    LibraryImport entry point is in expected-exports.txt and vice versa.
  * CodeBrixWebGpuNativeTests.CheckStructLayouts_*: size + alignment of EVERY
    generated struct equals what the native reports
    (wgpuCodebrixGetStructLayout) - the safety net of a generated binding.
  * PackageContent: the newest nupkg of the configuration has the assembly,
    the documents and exactly the committed runtimes/ files.
The generator tests include HeaderParityTests (see THE SPECIFICATIONS).


THE BINDING GENERATOR
=====================

    dotnet run --project tools/CodeBrix.WebGpu.BindingGenerator -- --check
    dotnet run --project tools/CodeBrix.WebGpu.BindingGenerator -- --write
        [--repo <repository root>]   (default: the folder holding the .slnx)

--check regenerates in memory and exits 1 (printing the first differing lines
of each file) when a committed output differs or is missing, or when a stale
*.cs sits in Generated/. --write writes only the files whose bytes changed.
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

OUTPUTS (all start with a banner: tool name, sha256 of schema.json and of each
YAML, "DO NOT EDIT"; LF line endings; byte-deterministic):
    src/CodeBrix.WebGpu.Native/Generated/Enums.g.cs, Bitflags.g.cs,
      Structs.g.cs, Callbacks.g.cs, Functions.g.cs, Handles.g.cs,
      Constants.g.cs, StructLayouts.g.cs
    wgpu-native-tools/specs/expected-exports.txt  ('#' lines are comments)

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
Xcode CLT); they never install anything. Publish only when all nine are
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
     golden files.)
  4. Update the codebrix-wgpu crate for the new wgpu-core/hal APIs; if
     codebrix.yml changes, bump its api_version.
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
icon-codebrix-128.png and runtimes/<rid>/native/{native, LICENSE-WgpuNative.txt}.
The runtimes item uses PackagePath="runtimes\" BARE - spelling %(RecursiveDir)
out as well doubles the path; check the nupkg after any csproj change
(PackageContent test). The generator and the test projects are not packable.
Jeremy publishes.


CODING CONVENTIONS
==================

The CodeBrix family rules: net10.0 only; NRT off (no `?` on reference types,
no `!`); no ImplicitUsings, no global usings; file-scoped namespaces; usings at
the top, System first; XML docs on every public member; no NoWarn, no pragma
suppressions; no third-party NuGet in the library. Tests: <Class>Tests.cs,
snake_case names (Member_does_x or plain snake_case), //Arrange //Act //Assert,
expression bodies for one-statement tests, SilverAssertions,
TestContext.Current.CancellationToken on cancellable calls. Generated code
follows the same rules - fix the templates, never the output.


PROVENANCE / VENDORED SOURCES
=============================

wgpu-native-tools/wgpu-native/ (wgpu-native release, MIT OR Apache-2.0, used
as MIT) and its ffi/webgpu-headers/ (BSD-3-Clause) are vendored unmodified;
specs/wgpu.yml is a transcription of wgpu.h; the generator re-implements the
rules of webgpu-headers' Go generator. Details and licence texts:
THIRD-PARTY-NOTICES.txt and LICENSE-WgpuNative.txt.


NOTES
=====

  * Forty exported functions of this wgpu-native release are unimplemented!()
    and abort the process (Rust panic = abort): wgpuInstanceWaitAny,
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
  * Samples/WebGpuDiagnostics (Android device-proof app) is planned; the
    Samples solution folder is empty until then.

================================================================================
END OF MAINTAINER-README
================================================================================
