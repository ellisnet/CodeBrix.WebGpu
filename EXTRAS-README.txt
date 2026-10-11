================================================================================
EXTRAS-README: CodeBrix.WebGpu
Samples, tools and other content in this repository that is not part of a NuGet
package
================================================================================

Exactly one project is packable today - src/CodeBrix.WebGpu.Native - and
everything else listed below exists to build, test or document it. None of it
is included in the CodeBrix.WebGpu.Native.MitLicenseForever package.

For runnable, compilable usage of the library, read the test project: the
"WORKING EXAMPLES ON GITHUB" section of AGENT-README.txt maps each feature area
to the test file that exercises it.


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
    tests/CodeBrix.WebGpu.BindingGenerator.Tests/

xUnit v3; run them as described in MAINTAINER-README.txt (TESTING).


SAMPLES
=======

None yet. A small Android device-proof application (samples/WebGpuDiagnostics)
is planned; the solution's Samples folder will hold it.
