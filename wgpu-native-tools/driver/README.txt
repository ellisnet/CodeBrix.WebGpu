wgpu-native-tools/driver/ - WgpuNativeDriver, the CodeBrix.RustTools dog-food tool
================================================================================
A small .NET 10 console project (WgpuNativeDriver.csproj + Program.cs) through
which this folder USES the published packages

    CodeBrix.RustTools.MitLicenseForever          1.0.284.266  (nuget.org)
    CodeBrix.RustTools.Docker.MitLicenseForever   1.0.284.266  (nuget.org)

- its only dependencies. It is NOT part of CodeBrix.WebGpu.slnx, has no tests
and is never packed. The scripts run it; you can too:

    dotnet run --project driver/WgpuNativeDriver.csproj -- <command> [options]

  license-audit  RustWorkbench.AuditLicensesAsync per target triple + the set of
                 crates that link into the library -> TSV
                 (licenses/compose-license-wgpunative.sh --refresh)
  android-build  RustWorkbench.BuildAndroidAsync + AndroidLinkProfile
                 (android/build.sh)
  linux-build    CodeBrix.RustTools.Docker: engine, image, container, source
                 copy, cargo fetch, in-container licence audit, cargo build
                 (linux/build.sh: fills the cargo cache volume and produces the
                 reproducibility twin of the network-free shipped compile)
  cleanup        DockerRustWorkbench.CleanupAsync for this folder's label prefix
                 (codebrix.webgpu.natives); --volumes also drops the cargo cache

Options of each command: the header of Program.cs. Gaps and bugs found while dog-fooding the 1.0.283.434 packages are recorded in
../BUILD-PROVENANCE.txt and were fixed the same day (2026-10-10) in
CodeBrix.RustTools 1.0.284.266 and CodeBrix.Docker 1.0.284.250: RunCargoAsync
dropped cargo's JSON messages under podman (a CodeBrix.Docker exec-framing bug;
linux-build still copies the library out with ExecAsync, which works either
way), there was no network-off container option (BuildContainerOptions.
NetworkDisabled now exists; linux/build.sh keeps its own --network none
compile as the shipped path), and AndroidLinkProfile set no bindgen sysroot
(it does now; android/build.sh still passes BINDGEN_EXTRA_CLANG_ARGS_<triple>
explicitly, harmlessly). The scripts were left as written so that a rebuild
does not depend on the package upgrade.
