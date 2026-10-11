# =============================================================================================
# build-win-arm64.ps1 - build codebrix_wgpu.dll for the win-arm64 runtime identifier
# =============================================================================================
#
#   Written on Linux on 2026-10-11 from CodeBrix.VideoEncoding.Av1's script of the same name
#   (the FlacWriter original ran successfully, unchanged, on Jeremy's x64 Windows 11 machine on
#   2026-10-10, as a cross build with -AdoptCrossBuild), adapted to a Rust (cargo, MSVC target)
#   build. FIRST RUN the same day, CrossFromX64 route, on the same machine (Visual Studio 2026,
#   rustup toolchain 1.93 with the aarch64-pc-windows-msvc target, Visual Studio's libclang via
#   LIBCLANG_PATH) with -AdoptCrossBuild: built, every static check passed, adopted, with this
#   script unchanged; the fixes were in build-common.ps1 and ..\smoke-test.c (see their headers and
#   ..\BUILD-PROVENANCE.txt). The smoke test is UNRUN until it is executed on an ARM64 Windows
#   device (see below). The 67 MB PDB stays in ..\output\win-arm64\, not in unstripped\. The Native
#   route has never run. If something breaks, fix it IN THE SCRIPT (or build-common.ps1) and record
#   it in ..\BUILD-PROVENANCE.txt.
#
# USAGE (from any PowerShell prompt - the script sets up the compiler environment itself):
#
#     cd wgpu-native-tools\windows
#     .\build-win-arm64.ps1                          # route chosen from the host (see below)
#     .\build-win-arm64.ps1 -Route CrossFromX64      # force the cross route on an x64 machine
#     .\build-win-arm64.ps1 -AdoptCrossBuild         # x64 host: adopt even though the smoke test
#                                                    # could not run (static checks must pass)
#     .\build-win-arm64.ps1 -NoAdopt                 # build + gate only
#
# cargo +<RUST_TOOLCHAIN> build --release --locked --target aarch64-pc-windows-msvc, static CRT,
# MSVC's ARM64 linker. The two routes differ only in the HOST:
#   Native (an ARM64 Windows machine): vcvarsall arm64; the whole gate runs, smoke test included.
#   CrossFromX64 (an x64 Windows machine): vcvarsall x64_arm64. Builds the DLL and an ARM64
#       smoke-test.exe (..\smoke-test.c through cl for ARM64) and runs every static check
#       (machine type, exact exports, dependents, licence), but CANNOT run the smoke test. That is
#       reported as a FAILURE and the script exits 1 without adopting - unless -AdoptCrossBuild
#       is given, in which case it adopts and BUILD-INFO.txt says "Gate status: INCOMPLETE". The
#       gate can be finished later by copying output\win-arm64\codebrix_wgpu.dll,
#       output\win-arm64\smoke-test.exe and ..\specs\expected-exports.txt to an ARM64 Windows
#       machine (Jeremy's Surface Pro X), then:
#           smoke-test.exe <full path to codebrix_wgpu.dll> <full path to expected-exports.txt>
#       It prints "SMOKE TEST PASSED" and exits 0 when every check passes (a machine without a
#       usable GPU adapter prints SKIPPED for the GPU part). Check first that the DLL's sha256 is
#       the one recorded for win-arm64 in ..\BUILD-PROVENANCE.txt, then record the result
#       (device, date, exit code) in that block. win-arm64's smoke test is UNRUN until then.
#   -Route Auto (default) picks Native on ARM64 hosts and CrossFromX64 on x64 hosts.
#
# Prerequisites: as build-win-x64.ps1, plus the "MSVC Build Tools for ARM64/ARM64EC" component and
#     rustup target add aarch64-pc-windows-msvc --toolchain 1.93
# Output: ..\output\win-arm64\
# =============================================================================================

[CmdletBinding()]
param(
    [ValidateSet('Auto', 'Native', 'CrossFromX64')]
    [string] $Route = 'Auto',
    [string] $BuildRoot = (Join-Path $env:TEMP 'codebrix-wgpu-build-win-arm64'),
    [switch] $NoAdopt,
    [switch] $AdoptCrossBuild,
    [switch] $CheckReproducible
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. "$PSScriptRoot\build-common.ps1"

$hostIsArm64 = ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64')
if ($Route -eq 'Auto') { $Route = if ($hostIsArm64) { 'Native' } else { 'CrossFromX64' } }
if ($Route -eq 'Native' -and -not $hostIsArm64) {
    throw "-Route Native must run on an ARM64 Windows machine; this host is $env:PROCESSOR_ARCHITECTURE. Use -Route CrossFromX64 here, or run on the ARM64 machine."
}
if ($Route -eq 'CrossFromX64' -and $env:PROCESSOR_ARCHITECTURE -ne 'AMD64') {
    throw "-Route CrossFromX64 runs on an x64 Windows machine; this host is $env:PROCESSOR_ARCHITECTURE. On ARM64 use -Route Native."
}

$vcvarsArch = if ($Route -eq 'Native') { 'arm64' } else { 'x64_arm64' }
$routeText  = if ($Route -eq 'Native') { 'Native (ARM64 host)' } else { 'CrossFromX64 (x64 host, --target aarch64-pc-windows-msvc)' }

Invoke-WindowsWgpuBuild -Rid 'win-arm64' -Triple 'aarch64-pc-windows-msvc' -ExpectedMachine 0xAA64 -VcVarsArch $vcvarsArch `
                        -Route $routeText -CanRun $hostIsArm64 -BuildRoot $BuildRoot `
                        -ScriptName 'build-win-arm64.ps1' -NoAdopt:$NoAdopt -AdoptCrossBuild:$AdoptCrossBuild -CheckReproducible:$CheckReproducible
