# =============================================================================================
# build-win-x64.ps1 - build codebrix_wgpu.dll for the win-x64 runtime identifier
# =============================================================================================
#
#   Written on Linux on 2026-10-11 from CodeBrix.VideoEncoding.Av1's script of the same name
#   (the FlacWriter original ran successfully, unchanged, on Jeremy's x64 Windows 11 machine on
#   2026-10-10), adapted to a Rust (cargo, MSVC target) build. NOT YET RUN. If something breaks,
#   fix it IN THE SCRIPT (or build-common.ps1) and record it in ..\BUILD-PROVENANCE.txt.
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
# LLVM for libclang (winget install LLVM.LLVM, or set LIBCLANG_PATH); Git for Windows.
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
