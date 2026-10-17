# =============================================================================================
# build-win-x64.ps1 - build codebrix_wgpu.dll for the win-x64 runtime identifier
# =============================================================================================
#
#   Written on Linux on 2026-10-11 from CodeBrix.VideoEncoding.Av1's script of the same name
#   (the FlacWriter original ran successfully, unchanged, on Jeremy's x64 Windows 11 machine on
#   2026-10-10), adapted to a Rust (cargo, MSVC target) build. FIRST RUN the same day on the same
#   machine (Visual Studio 2026, rustup toolchain 1.93, Visual Studio's libclang via LIBCLANG_PATH):
#   built, passed the whole gate including the 47-check smoke test (Intel UHD 770, Vulkan), and
#   was adopted, with this script unchanged; the fixes were in build-common.ps1 and ..\smoke-test.c
#   (see their headers and ..\BUILD-PROVENANCE.txt). The 67 MB PDB stays in ..\output\win-x64\, not
#   in unstripped\. If something breaks on another machine, fix it IN THE SCRIPT (or
#   build-common.ps1) and record it in ..\BUILD-PROVENANCE.txt.
#
# USAGE (from any PowerShell prompt - the script sets up the compiler environment itself):
#
#     cd wgpu-native-tools\windows
#     .\build-win-x64.ps1                       # build, gate, and (gate COMPLETE) adopt into src\
#     .\build-win-x64.ps1 -NoAdopt              # build + gate only
#     .\build-win-x64.ps1 -CheckReproducible    # + a second build that must give identical bytes
#
# cargo +<RUST_TOOLCHAIN> build --release --locked --target x86_64-pc-windows-msvc, static CRT.
# Output: ..\output\win-x64\ ; adopted into ..\..\src\CodeBrix.WebGpu.Native\runtimes\win-x64\native\
# (codebrix_wgpu.dll + LICENSE-WgpuNative.txt), the .pdb into ..\unstripped\win-x64\.
# It installs nothing. Anything missing is named, with the command that installs it.
# Prerequisites (see build-common.ps1's header): Visual Studio 2022+ with "Desktop development
# with C++"; rustup with the pinned toolchain + x86_64-pc-windows-msvc
# (rustup toolchain install 1.93; rustup target add x86_64-pc-windows-msvc --toolchain 1.93);
# libclang - the recommended path is Visual Studio's own LLVM through LIBCLANG_PATH (see
# build-common.ps1's header; the alternative: winget install LLVM.LLVM); Git for Windows.
# THE RECOMMENDED RUN (Jeremy's rulings of 2026-10-17): LIBCLANG_PATH set as above, then
#     pwsh -NoProfile -ExecutionPolicy Bypass -File .\build-win-x64.ps1 -CheckReproducible
# and, once adopted, the managed proof (dotnet test --solution ..\..\CodeBrix.WebGpu.slnx
# -p:UseLocalNativeProject=true) - the whole sequence: ..\README.txt "WINDOWS: THE RECOMMENDED RUN".
# =============================================================================================

[CmdletBinding()]
param(
    # Where cargo builds and the gate runs. Kept out of the repository.
    [string] $BuildRoot = (Join-Path $env:TEMP 'codebrix-wgpu-build-win-x64'),
    [switch] $NoAdopt,
    [switch] $CheckReproducible
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. "$PSScriptRoot\build-common.ps1"

if ($env:PROCESSOR_ARCHITECTURE -ne 'AMD64') {
    throw "build-win-x64.ps1 runs on an x64 Windows machine; this host is $env:PROCESSOR_ARCHITECTURE."
}

Invoke-WindowsWgpuBuild -Rid 'win-x64' -Triple 'x86_64-pc-windows-msvc' -ExpectedMachine 0x8664 -VcVarsArch 'x64' `
                        -Route 'native (x64 host)' -CanRun $true -BuildRoot $BuildRoot `
                        -ScriptName 'build-win-x64.ps1' -NoAdopt:$NoAdopt -CheckReproducible:$CheckReproducible
