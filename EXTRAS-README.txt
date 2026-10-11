================================================================================
EXTRAS-README: CodeBrix.WebGpu
Samples, tools and other content in this repository that is not part of a NuGet
package
================================================================================

Three projects are packable - src/CodeBrix.WebGpu.Native,
src/CodeBrix.WebGpu.Skia and src/CodeBrix.WebGpu.Graphics - and everything else
listed below exists to build, test, demonstrate or document them. None of it
is included in the CodeBrix.WebGpu.Native.MitLicenseForever,
CodeBrix.WebGpu.Skia.MitLicenseForever or
CodeBrix.WebGpu.Graphics.MitLicenseForever packages.

For runnable, compilable usage of the libraries, read the test projects: the
"WORKING EXAMPLES ON GITHUB" sections of AGENT-README.txt,
src/CodeBrix.WebGpu.Skia/AGENT-README.txt and
src/CodeBrix.WebGpu.Graphics/AGENT-README.txt map each feature area to the
test file that exercises it.


TOOLS
=====
    tools/CodeBrix.WebGpu.BindingGenerator/

The binding generator: reads the WebGPU, wgpu and CodeBrix YAML specifications,
validates them against the webgpu-headers schema and renders
src/CodeBrix.WebGpu.Native/Generated/*.g.cs and
wgpu-native-tools/specs/expected-exports.txt. Run it with --check or --write
(MAINTAINER-README.txt, THE BINDING GENERATOR).

    wgpu-native-tools/

Everything that builds the native codebrix_wgpu libraries: the vendored
wgpu-native and webgpu-headers trees, the codebrix-wgpu Rust crate, patches,
licence composition, per-platform build scripts, the host build and the smoke
test. See wgpu-native-tools/README.txt.


TEST PROJECTS
=============
    tests/CodeBrix.WebGpu.Native.Tests/
    tests/CodeBrix.WebGpu.Skia.Tests/
    tests/CodeBrix.WebGpu.Graphics.Tests/
    tests/CodeBrix.WebGpu.BindingGenerator.Tests/

xUnit v3; run them as described in MAINTAINER-README.txt (TESTING). The Skia
tests reference SkiaSharp.NativeAssets.Linux (test-only; the Skia package does
not depend on it). The Graphics tests also reference the Skia project and
SkiaSharp.NativeAssets.Linux, for the tests that share one device between the
two packages (the Graphics package references neither).


SAMPLES
=======

    samples/WebGpuModelViewer/

A console application (not packed) in the solution's Samples folder: animated
glTF models (Kenney Cube Pets walking, idling, eating and dancing around an
opening chest, the Avian flying overhead) rendered by
CodeBrix.WebGpu.Graphics, a sprite-batch HUD, and a SkiaSharp overlay drawn
through CodeBrix.WebGpu.Skia on the same GPU texture. It loads the models with
CodeBrix.Graphics3D.Gltf2 and opens its live window with CodeBrix.Sdl3 (X11,
Wayland, Win32 or Metal handles handed to a Graphics window surface); with
--headless <folder> [--frames N] it writes PNG frames instead. Its README.md
says how to run it and credits the CC0 models (THIRD-PARTY-NOTICES.txt
section 5).

A small Android device-proof application (samples/WebGpuDiagnostics) is
planned.
