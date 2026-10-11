# =============================================================================================
# build-common.ps1 - shared machinery for the Windows codebrix_wgpu builds (win-x64, win-arm64)
# =============================================================================================
#
#   Written on Linux on 2026-10-11 from CodeBrix.VideoEncoding.Av1's
#   av1-native-tools\windows\build-common.ps1 (itself a copy of CodeBrix.Audio.FlacWriter's, which
#   Jeremy ran UNCHANGED and successfully on 2026-10-10 on his x64 Windows 11 machine: Visual Studio
#   Professional 2026 18.10.3, PowerShell 7.6; win-x64 native, win-arm64 cross with
#   -AdoptCrossBuild). Same structure, prerequisite checks, gate and adoption; adapted where a
#   Rust build differs from a CMake one:
#     * the compiler is rustc through cargo (MSVC targets x86_64-pc-windows-msvc /
#       aarch64-pc-windows-msvc, toolchain pinned by ..\linux\pins.env RUST_TOOLCHAIN); MSVC's
#       link.exe is the linker, so the Visual Studio developer environment is imported first;
#     * wgpu-native's build script runs bindgen, which needs LIBCLANG (LLVM's libclang.dll);
#     * static CRT (-C target-feature=+crt-static) so the DLL needs no Visual C++ Redistributable;
#       /Brepro so two builds of the same source give the same bytes; the .pdb comes from
#       Cargo.toml's debug = true and is referenced by file name only (rustc's /PDBALTPATH);
#     * the smoke test (..\smoke-test.c) is compiled with cl.exe for the target architecture;
#     * the export set is checked against ..\specs\expected-exports.txt (243 functions).
#
#   NOT YET RUN. If something breaks on the Windows machine, fix it IN THE SCRIPT and record it
#   in ..\BUILD-PROVENANCE.txt.
#
# Dot-source this from an architecture script; do not run it directly.
#
#     . "$PSScriptRoot\build-common.ps1"
#
# Everything it builds is in this repository: ..\wgpu-native (the unmodified wgpu-native release
# tree, webgpu-headers included), ..\codebrix-wgpu (our crate), ..\patches (applied to a scratch
# copy) and ..\linux\pins.env (the pins shared by every platform). cargo downloads the crates.io
# crates ..\codebrix-wgpu\Cargo.lock pins (exact versions + checksums; --locked) into
# %USERPROFILE%\.cargo the first time - crates.io is the one network source a build may use.
#
# PREREQUISITES (checked, named, never installed):
#   * Visual Studio 2022 or newer (2026 is what Jeremy runs) or the Build Tools, with the
#     "Desktop development with C++" workload; for win-arm64 also "MSVC ... ARM64/ARM64EC build
#     tools".
#   * rustup + cargo (winget install Rustlang.Rustup), then
#         rustup toolchain install 1.93
#         rustup target add x86_64-pc-windows-msvc aarch64-pc-windows-msvc --toolchain 1.93
#     (the toolchain is the RUST_TOOLCHAIN pin in ..\linux\pins.env).
#   * LLVM's libclang.dll for bindgen: winget install LLVM.LLVM   (or choco install llvm). The
#     scripts find C:\Program Files\LLVM\bin\libclang.dll themselves; anywhere else, set
#     LIBCLANG_PATH to the folder that holds libclang.dll.
#   * Git for Windows (git apply, for ..\patches): winget install Git.Git
# =============================================================================================

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------------------------
# Pins: ..\linux\pins.env.
# ---------------------------------------------------------------------------------------------
function Read-Pins {
    param([Parameter(Mandatory)][string] $Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "pins.env was not found at $Path. It is part of the repository - if it is missing after a clone, check the root .gitignore's blanket '*.env' rule (see ..\.gitignore)."
    }
    $pins = @{}
    foreach ($line in Get-Content -LiteralPath $Path) {
        $trimmed = $line.Trim()
        if ($trimmed -eq '' -or $trimmed.StartsWith('#')) { continue }
        $eq = $trimmed.IndexOf('=')
        if ($eq -lt 1) { continue }
        $key = $trimmed.Substring(0, $eq).Trim()
        $value = $trimmed.Substring($eq + 1).Trim()
        if ($value.Length -ge 2 -and $value.StartsWith('"') -and $value.EndsWith('"')) {
            $value = $value.Substring(1, $value.Length - 2)
        }
        $pins[$key] = $value
    }
    foreach ($required in @('WGPU_NATIVE_DIR', 'WGPU_NATIVE_TAG', 'RUST_TOOLCHAIN', 'LICENSE_NAME', 'EXPECTED_EXPORTS_FILE')) {
        if (-not $pins.ContainsKey($required)) { throw "pins.env has no $required. The Windows scripts read it from ..\linux\pins.env." }
    }
    return $pins
}

# Every DLL the shipped codebrix_wgpu.dll may import: Windows' own system libraries. wgpu reaches
# Direct3D 12 / DXGI through the windows crate's import declarations, and loads Vulkan / OpenGL
# (vulkan-1.dll, opengl32.dll, EGL) and the shader compilers at run time. A Visual C++ runtime
# (vcruntime*, msvcp*, ucrtbase, api-ms-win-crt-*) means +crt-static did not take - always a
# failure. Any DLL not listed is a failure too: look at it, and if it is a Windows system DLL,
# add it here deliberately and say so in ..\BUILD-PROVENANCE.txt.
$script:AllowedDependents = @(
    'KERNEL32.dll', 'ntdll.dll', 'ADVAPI32.dll', 'bcryptprimitives.dll', 'bcrypt.dll', 'USER32.dll', 'GDI32.dll',
    'ole32.dll', 'OLEAUT32.dll', 'SHELL32.dll', 'USERENV.dll', 'WS2_32.dll', 'd3d12.dll', 'dxgi.dll', 'dcomp.dll',
    'opengl32.dll', 'd3dcompiler_47.dll', 'dxcore.dll', 'SETUPAPI.dll', 'CFGMGR32.dll', 'VERSION.dll', 'WINMM.dll',
    'synchronization.dll'
)

# ---------------------------------------------------------------------------------------------
# Native commands. Windows PowerShell 5.1 turns every STDERR line of a native command into an
# ErrorRecord when redirected with 2>&1, and with $ErrorActionPreference = 'Stop' the first one
# would abort the script. So native tools always run through this helper.
# ---------------------------------------------------------------------------------------------
function Invoke-Native {
    param(
        [Parameter(Mandatory)][string] $Command,
        [string[]] $Arguments = @()
    )
    $saved = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & $Command @Arguments 2>&1 | ForEach-Object { "$_" }
    }
    finally { $ErrorActionPreference = $saved }
    return $output
}

# ---------------------------------------------------------------------------------------------
# Visual Studio discovery (unchanged from the sibling scripts).
# ---------------------------------------------------------------------------------------------
function Find-VisualStudio {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere)) {
        throw @"
vswhere.exe was not found at
    $vswhere
which means no Visual Studio 2017-or-newer installer is present. Install Visual Studio 2022 or
newer (or the Build Tools) with the "Desktop development with C++" workload - see ..\README.txt.
"@
    }
    $installPath = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath | Select-Object -First 1
    if (-not $installPath) {
        throw 'No Visual Studio installation with the C++ toolset was found. Install the "Desktop development with C++" workload.'
    }
    $version = & $vswhere -path $installPath -property installationVersion | Select-Object -First 1
    $display = & $vswhere -path $installPath -property catalog_productDisplayVersion | Select-Object -First 1
    $name    = & $vswhere -path $installPath -property displayName | Select-Object -First 1
    return [pscustomobject]@{
        Path           = $installPath
        Version        = $version
        Major          = [int](($version -split '\.')[0])
        DisplayVersion = $display
        DisplayName    = $name
    }
}

function Get-VcVarsAllPath {
    param([Parameter(Mandatory)][string] $VsInstallPath)
    $vcvarsall = Join-Path $VsInstallPath 'VC\Auxiliary\Build\vcvarsall.bat'
    if (-not (Test-Path -LiteralPath $vcvarsall)) {
        throw "vcvarsall.bat was not found in $VsInstallPath. The C++ workload is not installed in THIS instance."
    }
    return $vcvarsall
}

# Does the SELECTED installation actually have an ARM64-targeting cl.exe? Checked on disk.
function Test-Arm64ToolsPresent {
    param([Parameter(Mandatory)][string] $VsInstallPath)
    $msvcRoot = Join-Path $VsInstallPath 'VC\Tools\MSVC'
    if (-not (Test-Path -LiteralPath $msvcRoot)) { return $false }
    foreach ($toolset in Get-ChildItem -LiteralPath $msvcRoot -Directory) {
        foreach ($hostDir in @('Hostx64', 'Hostarm64')) {
            if (Test-Path -LiteralPath (Join-Path $toolset.FullName "bin\$hostDir\arm64\cl.exe")) { return $true }
        }
    }
    return $false
}

# Import a developer environment into THIS PowerShell process: cargo then finds MSVC's link.exe
# (and the Windows SDK libraries) for the target, and the gate finds cl and dumpbin.
# $ArchArgument is what vcvarsall takes: x64, arm64, or x64_arm64 (cross from an x64 host).
function Import-DeveloperEnvironment {
    param(
        [Parameter(Mandatory)][string] $VcVarsAll,
        [Parameter(Mandatory)][string] $ArchArgument
    )
    Write-Host "  developer environment: vcvarsall.bat $ArchArgument"
    # This exact invocation is llama-native-tools' (run for real 2026-09-15); keep it.
    $output = & "$env:COMSPEC" /s /c "`"$VcVarsAll`" $ArchArgument >nul 2>&1 && set"
    if ($LASTEXITCODE -ne 0) {
        throw "vcvarsall.bat $ArchArgument failed (exit $LASTEXITCODE). The toolset for that target is probably not installed in this Visual Studio instance."
    }
    foreach ($line in $output) {
        $eq = $line.IndexOf('=')
        if ($eq -lt 1) { continue }
        Set-Item -Path "Env:$($line.Substring(0, $eq))" -Value $line.Substring($eq + 1)
    }
}

function Get-ClVersionLine {
    $line = Invoke-Native 'cl' | Where-Object { $_ -match 'Compiler Version' } | Select-Object -First 1
    if (-not $line) { return 'cl (version banner not recognised)' }
    return $line.Trim()
}

function Assert-OnPath {
    param(
        [Parameter(Mandatory)][string] $Tool,
        [Parameter(Mandatory)][string] $InstallHint
    )
    $found = Get-Command $Tool -ErrorAction SilentlyContinue
    if (-not $found) {
        throw @"
$Tool is not on PATH.

$InstallHint

This script does not install anything for you. See ..\README.txt.
"@
    }
    return $found.Source
}

# ---------------------------------------------------------------------------------------------
# Rust prerequisites: rustup, the pinned toolchain, the target. Named, never installed.
# ---------------------------------------------------------------------------------------------
function Assert-RustToolchain {
    param(
        [Parameter(Mandatory)][string] $Toolchain,
        [Parameter(Mandatory)][string] $Triple
    )
    $rustup = Assert-OnPath 'rustup' 'Install rustup:   winget install Rustlang.Rustup   (then open a new prompt)'
    $null   = Assert-OnPath 'cargo'  'cargo comes with rustup:   winget install Rustlang.Rustup   (then open a new prompt)'
    $toolchains = Invoke-Native 'rustup' @('toolchain', 'list')
    if (-not ($toolchains | Where-Object { $_ -match "^$([regex]::Escape($Toolchain))-" })) {
        throw @"
The pinned Rust toolchain $Toolchain is not installed (rustup toolchain list shows: $($toolchains -join ', ')).
Install it:
    rustup toolchain install $Toolchain
    rustup target add x86_64-pc-windows-msvc aarch64-pc-windows-msvc --toolchain $Toolchain
"@
    }
    $targets = Invoke-Native 'rustup' @('target', 'list', '--installed', '--toolchain', $Toolchain)
    if (-not ($targets | Where-Object { $_.Trim() -eq $Triple })) {
        throw "The Rust target $Triple is not installed for toolchain $Toolchain. Install it:   rustup target add $Triple --toolchain $Toolchain"
    }
    $rustc = (Invoke-Native 'rustup' @('run', $Toolchain, 'rustc', '--version') | Select-Object -First 1)
    $cargo = (Invoke-Native 'rustup' @('run', $Toolchain, 'cargo', '--version') | Select-Object -First 1)
    return [pscustomobject]@{ Rustup = $rustup; Rustc = "$rustc"; Cargo = "$cargo" }
}

# libclang.dll for bindgen (wgpu-native's build script). LIBCLANG_PATH wins; else LLVM's default
# install folder is used (and LIBCLANG_PATH set for the build).
function Assert-Libclang {
    $candidates = @()
    if ($env:LIBCLANG_PATH) { $candidates += $env:LIBCLANG_PATH }
    $candidates += (Join-Path $env:ProgramFiles 'LLVM\bin')
    foreach ($dir in $candidates) {
        if (Test-Path -LiteralPath (Join-Path $dir 'libclang.dll')) {
            $env:LIBCLANG_PATH = $dir
            return (Join-Path $dir 'libclang.dll')
        }
    }
    throw @"
libclang.dll was not found (wgpu-native's build runs bindgen over webgpu.h / wgpu.h, which needs LLVM's libclang).
Install LLVM:   winget install LLVM.LLVM      (or: choco install llvm)
It lands in C:\Program Files\LLVM\bin, which this script finds by itself; anywhere else, set
LIBCLANG_PATH to the folder holding libclang.dll.
"@
}

# ---------------------------------------------------------------------------------------------
# Source: wgpu-native\ and codebrix-wgpu\ copied side by side to scratch and built there, so the
# vendored tree stays unmodified. patches\*.patch apply to the copy, in name order.
# ---------------------------------------------------------------------------------------------
function Copy-SourceToScratch {
    param(
        [Parameter(Mandatory)][string]    $ToolsDir,
        [Parameter(Mandatory)][string]    $ScratchDir,
        [Parameter(Mandatory)][hashtable] $Pins
    )
    if (Test-Path -LiteralPath $ScratchDir) { Remove-Item -LiteralPath $ScratchDir -Recurse -Force }
    New-Item -ItemType Directory -Path $ScratchDir -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $ToolsDir $Pins['WGPU_NATIVE_DIR']) -Destination (Join-Path $ScratchDir 'wgpu-native') -Recurse
    Copy-Item -LiteralPath (Join-Path $ToolsDir 'codebrix-wgpu')          -Destination (Join-Path $ScratchDir 'codebrix-wgpu') -Recurse
    Write-Host "  copied wgpu-native\ codebrix-wgpu\ -> $ScratchDir"

    $applied = @()
    $git = Get-Command git -ErrorAction SilentlyContinue
    foreach ($patch in Get-ChildItem -LiteralPath (Join-Path $ToolsDir 'patches') -Filter '*.patch' | Sort-Object Name) {
        if (-not $git) { throw "patches\$($patch.Name) needs applying and git is not available. Install Git for Windows (winget install Git.Git)." }
        Write-Host "  applying $($patch.Name)"
        Invoke-Native 'git' @('-C', $ScratchDir, 'apply', '-p1', '--whitespace=nowarn', $patch.FullName) | ForEach-Object { Write-Host "    $_" }
        if ($LASTEXITCODE -ne 0) { throw "patch $($patch.Name) did not apply. Fix it; patches are never applied best-effort." }
        $applied += $patch.Name
    }
    if ($applied.Count -eq 0) { throw 'patches\ is empty - the build needs 0001 and 0002 (see ..\patches\README.txt).' }
    return ($applied -join ' ')
}

# ---------------------------------------------------------------------------------------------
# cargo build. Returns the built DLL's full path (the .pdb lies beside it).
#   --locked      Cargo.lock is the pin; cargo fails rather than change it
#   +crt-static   the static CRT: the DLL imports no Visual C++ runtime
#   /Brepro       no timestamps in the PE / PDB, so a rebuild gives the same bytes
#   --remap-path-prefix   no scratch or cargo-home path in the binary or the PDB
# ---------------------------------------------------------------------------------------------
function Invoke-CargoBuild {
    param(
        [Parameter(Mandatory)][string]    $ScratchDir,
        [Parameter(Mandatory)][string]    $TargetDir,
        [Parameter(Mandatory)][string]    $Triple,
        [Parameter(Mandatory)][hashtable] $Pins,
        [Parameter(Mandatory)][string]    $LogPath
    )
    $cargoHome = if ($env:CARGO_HOME) { $env:CARGO_HOME } else { Join-Path $env:USERPROFILE '.cargo' }
    # CARGO_ENCODED_RUSTFLAGS (0x1F-separated) so a path with spaces stays ONE argument. With
    # --target given, cargo applies it to the target's crates only, never to build scripts.
    $flags = @('-C', 'target-feature=+crt-static', '-C', 'link-arg=/Brepro',
               "--remap-path-prefix=$ScratchDir=wgpu-native-tools", "--remap-path-prefix=$cargoHome=cargo-home")
    $env:CARGO_ENCODED_RUSTFLAGS = ($flags -join [string][char]0x1F)
    $rustflags = $flags -join ' '
    $envName = 'CARGO_ENCODED_RUSTFLAGS'
    $env:WGPU_NATIVE_VERSION = $Pins['WGPU_NATIVE_TAG']
    $env:CARGO_TARGET_DIR = $TargetDir
    $env:CARGO_TERM_COLOR = 'never'
    $script:CargoCommandLine = "cargo +$($Pins['RUST_TOOLCHAIN']) build --release --locked --target $Triple   ($envName=`"$rustflags`")"
    Write-Host "  $($script:CargoCommandLine)"
    Push-Location (Join-Path $ScratchDir 'codebrix-wgpu')
    try {
        $build = Invoke-Native 'cargo' @("+$($Pins['RUST_TOOLCHAIN'])", 'build', '--release', '--locked', '--target', $Triple)
        $buildExit = $LASTEXITCODE
    }
    finally { Pop-Location }
    $build | Set-Content -LiteralPath $LogPath -Encoding UTF8
    if ($buildExit -ne 0) {
        $build | Select-Object -Last 40 | ForEach-Object { Write-Host "    $_" }
        throw "cargo build failed (exit $buildExit) - see $LogPath"
    }
    $script:BuildWarnings = @($build | Where-Object { $_ -match '^warning' }).Count
    Write-Host "  built ($($script:BuildWarnings) warning lines in the cargo log)"
    $dll = Join-Path $TargetDir "$Triple\release\codebrix_wgpu.dll"
    if (-not (Test-Path -LiteralPath $dll)) { throw "The build reported success but $dll does not exist." }
    return $dll
}

# ---------------------------------------------------------------------------------------------
# Helpers for the gate.
# ---------------------------------------------------------------------------------------------

# PE machine type, read straight from the header: 0x8664 x64, 0xAA64 ARM64.
function Get-PeMachine {
    param([Parameter(Mandatory)][string] $Path)
    $fs = [System.IO.File]::OpenRead($Path)
    try {
        $br = New-Object System.IO.BinaryReader($fs)
        $fs.Position = 0x3C
        $fs.Position = $br.ReadInt32()
        if ($br.ReadUInt32() -ne 0x00004550) { throw "$Path is not a PE image." }
        return [int]$br.ReadUInt16()
    }
    finally { $fs.Dispose() }
}

$script:GateFailures = New-Object System.Collections.Generic.List[string]
function Add-GatePass { param([string] $Message) Write-Host "  [ok] $Message" }
function Add-GateFail { param([string] $Message) Write-Host "  [FAIL] $Message"; $script:GateFailures.Add($Message) }

function Invoke-Dumpbin {
    param([Parameter(Mandatory)][string[]] $Arguments)
    $output = Invoke-Native 'dumpbin' $Arguments
    if ($LASTEXITCODE -ne 0) { throw "dumpbin $($Arguments -join ' ') failed (exit $LASTEXITCODE)." }
    return $output
}

# The expected export list (one symbol per line; '#' comments and blank lines skipped).
function Read-ExpectedExports {
    param([Parameter(Mandatory)][string] $Path)
    if (-not (Test-Path -LiteralPath $Path)) { throw "$Path is missing - it is generated by the binding generator and committed." }
    return @(Get-Content -LiteralPath $Path | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne '' -and -not $_.StartsWith('#') } | Sort-Object -Unique -CaseSensitive)
}

# ..\smoke-test.c compiled with cl.exe for the TARGET architecture (the developer environment
# imported for it), static CRT (/MT) so it runs on a clean machine.
function Build-SmokeTest {
    param(
        [Parameter(Mandatory)][string] $ToolsDir,
        [Parameter(Mandatory)][string] $ScratchDir,
        [Parameter(Mandatory)][string] $GateDir
    )
    if (Test-Path -LiteralPath $GateDir) { Remove-Item -LiteralPath $GateDir -Recurse -Force }
    New-Item -ItemType Directory -Path $GateDir -Force | Out-Null
    $exe = Join-Path $GateDir 'smoke-test.exe'
    $arguments = @('/nologo', '/std:c11', '/O1', '/MT', '/W3',
                   "/I$(Join-Path $ScratchDir 'wgpu-native\ffi')", "/I$(Join-Path $ScratchDir 'wgpu-native\ffi\webgpu-headers')",
                   "/I$(Join-Path $ScratchDir 'codebrix-wgpu\include')", (Join-Path $ToolsDir 'smoke-test.c'),
                   "/Fe:$exe", "/Fo:$(Join-Path $GateDir 'smoke-test.obj')")
    $out = Invoke-Native 'cl' $arguments
    if ($LASTEXITCODE -ne 0) {
        $out | ForEach-Object { Write-Host "    $_" }
        throw "cl failed to build smoke-test.exe (exit $LASTEXITCODE)."
    }
    return $exe
}

# ---------------------------------------------------------------------------------------------
# THE GATE. The same checks as the Linux build, expressed with the tools Windows has.
# Returns the smoke test's output lines (for BUILD-INFO.txt).
# ---------------------------------------------------------------------------------------------
function Test-CodebrixWgpuDll {
    param(
        [Parameter(Mandatory)][string]    $DllPath,             # the STAGED dll (the file that ships)
        [Parameter(Mandatory)][int]       $ExpectedMachine,     # 0x8664 | 0xAA64
        [Parameter(Mandatory)][string]    $ExpectedMachineName, # x64 | ARM64 (as dumpbin prints it)
        [Parameter(Mandatory)][hashtable] $Pins,
        [Parameter(Mandatory)][string]    $ToolsDir,
        [Parameter(Mandatory)][string]    $GateDir,             # holds smoke-test.exe
        [bool] $CanRunTargetBinaries = $true
    )

    # --- 1. machine type, two ways ---------------------------------------------------------------
    $machine = Get-PeMachine $DllPath
    if ($machine -eq $ExpectedMachine) { Add-GatePass ('PE machine type 0x{0:X4} ({1})' -f $machine, $ExpectedMachineName) }
    else { Add-GateFail ('PE machine type is 0x{0:X4}, expected 0x{1:X4} ({2})' -f $machine, $ExpectedMachine, $ExpectedMachineName) }
    $machineLine = Invoke-Dumpbin @('/nologo', '/headers', $DllPath) | Where-Object { $_ -match 'machine \(' } | Select-Object -First 1
    if ($machineLine -and $machineLine -match [regex]::Escape($ExpectedMachineName)) { Add-GatePass "dumpbin agrees: $($machineLine.Trim())" }
    else { Add-GateFail "dumpbin /headers machine line is '$machineLine', expected $ExpectedMachineName" }

    # --- 2. exports: EXACTLY ..\specs\expected-exports.txt ------------------------------------------
    # dumpbin prints "ordinal hint RVA name"; with the .pdb beside the DLL it appends
    # " = name (undecorated)", so the name is the FIRST token after the RVA.
    $exports = @()
    foreach ($line in Invoke-Dumpbin @('/nologo', '/exports', $DllPath)) {
        if ($line -match '^\s*\d+\s+[0-9A-Fa-f]+\s+[0-9A-Fa-f]+\s+(\S+)') { $exports += $Matches[1] }
    }
    if ($exports.Count -eq 0) { Add-GateFail 'no exports could be parsed from dumpbin /exports - run it by hand and compare the line format with the parser in Test-CodebrixWgpuDll' }
    $expected = Read-ExpectedExports (Join-Path $ToolsDir $Pins['EXPECTED_EXPORTS_FILE'])
    $missing = @($expected | Where-Object { $exports -cnotcontains $_ })
    $extra   = @($exports  | Where-Object { $expected -cnotcontains $_ })
    if ($missing.Count -gt 0) { Add-GateFail "missing exports ($($missing.Count)): $(($missing | Select-Object -First 20) -join ' ')" }
    if ($extra.Count -gt 0)   { Add-GateFail "unexpected exports ($($extra.Count)) - only the C API may be exported: $(($extra | Select-Object -First 20) -join ' ')" }
    if ($missing.Count -eq 0 -and $extra.Count -eq 0 -and $exports.Count -gt 0) {
        Add-GatePass "exports are exactly the $($expected.Count) functions of specs\expected-exports.txt"
    }
    $script:ExportCount = $exports.Count

    # --- 3. dependents: Windows system DLLs only, no VC runtime ---------------------------------
    $dependents = @()
    $inList = $false
    foreach ($line in Invoke-Dumpbin @('/nologo', '/dependents', $DllPath)) {
        $text = $line.Trim()
        if ($text -like 'Image has the following dependencies*') { $inList = $true; continue }
        if ($text -like 'Image has the following delay load dependencies*') { $inList = $true; continue }
        if ($inList) {
            if ($text -eq '') { continue }
            if ($text -like 'Summary*') { break }
            $dependents += $text
        }
    }
    $crt = @($dependents | Where-Object { $_ -match '^(vcruntime|msvcp|msvcr|ucrtbase|api-ms-win-crt)' })
    $unexpected = @($dependents | Where-Object { ($script:AllowedDependents -notcontains $_) -and ($_ -notmatch '^api-ms-win-core-') -and ($crt -notcontains $_) })
    if ($crt.Count -gt 0) { Add-GateFail "the DLL imports the Visual C++ runtime ($($crt -join ' ')) - +crt-static did not take" }
    if ($unexpected.Count -gt 0) { Add-GateFail "unexpected dependencies: $($unexpected -join ' ') (allowed: Windows system DLLs listed in build-common.ps1)" }
    if ($dependents | Where-Object { $_ -match '^(vulkan-1|libEGL|libGLESv2)' }) { Add-GateFail 'the DLL links a GPU driver library at load time - wgpu must load it at run time' }
    if ($dependents.Count -eq 0) { Add-GateFail 'no dependents parsed from dumpbin /dependents - check the parser' }
    elseif ($crt.Count -eq 0 -and $unexpected.Count -eq 0) { Add-GatePass "dependencies: $($dependents -join ' ') (static CRT, no redistributable needed)" }
    $script:Dependents = $dependents

    # --- 4. licence file -----------------------------------------------------------------------
    # LICENSE-WgpuNative.txt must be the committed licenses\LICENSE-WgpuNative.txt byte for byte
    # (composed ONCE from Cargo.lock by ..\licenses\compose-license-wgpunative.sh; never per build).
    $outDir = Split-Path -Parent $DllPath
    $outHash       = (Get-FileHash -LiteralPath (Join-Path $outDir $Pins['LICENSE_NAME'])).Hash
    $committedHash = (Get-FileHash -LiteralPath (Join-Path $ToolsDir "licenses\$($Pins['LICENSE_NAME'])")).Hash
    $bytes = [System.IO.File]::ReadAllBytes((Join-Path $ToolsDir "licenses\$($Pins['LICENSE_NAME'])"))
    $hasCr = $bytes -contains 13
    if ($outHash -eq $committedHash -and -not $hasCr) { Add-GatePass "$($Pins['LICENSE_NAME']) is the committed file (LF line endings)" }
    else { Add-GateFail "$($Pins['LICENSE_NAME']) differs from licenses\$($Pins['LICENSE_NAME']) or has CR bytes (a CRLF checkout? see ..\.gitattributes)" }

    if (-not $CanRunTargetBinaries) {
        Add-GateFail 'smoke test NOT RUN - this machine cannot execute the target architecture. (Reported as a failure on purpose: an unrun check is not a passed check.)'
        return @('  not run: this machine cannot execute the target architecture')
    }

    # --- 5. LoadLibrary smoke test ---------------------------------------------------------------
    # THE PATH TRAP: the staged DLL is copied beside smoke-test.exe and passed by FULL path, so the
    # exact bytes that ship are what loads - never a codebrix_wgpu.dll found elsewhere on PATH.
    $gateDll = Join-Path $GateDir 'codebrix_wgpu.dll'
    Copy-Item -LiteralPath $DllPath -Destination $gateDll -Force
    Write-Host '  --- LoadLibrary smoke test ---'
    Push-Location $GateDir
    try {
        $smoke = Invoke-Native (Join-Path $GateDir 'smoke-test.exe') @($gateDll, (Join-Path $ToolsDir $Pins['EXPECTED_EXPORTS_FILE']))
        $smokeExit = $LASTEXITCODE
    }
    finally { Pop-Location }
    $smoke | ForEach-Object { Write-Host "    $_" }
    if ($smokeExit -eq 0) { Add-GatePass "smoke test ($(($smoke | Where-Object { $_ -match '^\d+ checks' } | Select-Object -First 1)))" }
    else { Add-GateFail "smoke test exited $smokeExit" }
    return $smoke
}

function Get-Sha256 {
    param([Parameter(Mandatory)][string] $Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

# ---------------------------------------------------------------------------------------------
# THE WHOLE BUILD, for one RID. The two architecture scripts decide the route and call this.
#   -Triple       x86_64-pc-windows-msvc | aarch64-pc-windows-msvc
#   -VcVarsArch   x64 | arm64 | x64_arm64     (vcvarsall argument: link.exe for cargo, cl/dumpbin)
#   -CanRun       can this host execute the target's code (false only for the arm64 cross route)
#   -NoAdopt      build + gate only; do not copy into src\...\runtimes
#   -AdoptCrossBuild  adopt a cross-built win-arm64 whose static checks passed but whose smoke
#                 test could not run (recorded as such in BUILD-INFO.txt).
#   -CheckReproducible  build a SECOND time into a fresh target folder and require identical DLL bytes
# Exit code: 0 = every check ran and passed (or -AdoptCrossBuild and only the smoke test unrun).
#            1 = a check failed OR could not run.
# ---------------------------------------------------------------------------------------------
function Invoke-WindowsWgpuBuild {
    param(
        [Parameter(Mandatory)][string] $Rid,
        [Parameter(Mandatory)][string] $Triple,
        [Parameter(Mandatory)][int]    $ExpectedMachine,
        [Parameter(Mandatory)][string] $VcVarsArch,
        [Parameter(Mandatory)][string] $Route,
        [Parameter(Mandatory)][bool]   $CanRun,
        [Parameter(Mandatory)][string] $BuildRoot,
        [Parameter(Mandatory)][string] $ScriptName,
        [switch] $NoAdopt,
        [switch] $AdoptCrossBuild,
        [switch] $CheckReproducible
    )

    $toolsDir   = Split-Path -Parent $PSScriptRoot          # wgpu-native-tools\
    $repoDir    = Split-Path -Parent $toolsDir
    $pins       = Read-Pins (Join-Path $toolsDir 'linux\pins.env')
    $scratchDir = Join-Path $BuildRoot 'src'
    $targetDir  = Join-Path $BuildRoot 'target'
    $gateDir    = Join-Path $BuildRoot 'gate'
    $outDir     = Join-Path $toolsDir "output\$Rid"
    $startedAt  = (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd HH:mm:ss') + ' UTC'
    $stopwatch  = [System.Diagnostics.Stopwatch]::StartNew()
    $osCaption  = (Get-CimInstance Win32_OperatingSystem).Caption
    $osVersion  = [System.Environment]::OSVersion.Version.ToString()

    Write-Host '=============================================================================='
    Write-Host " codebrix_wgpu (wgpu-native $($pins['WGPU_NATIVE_TAG'])) - $Rid"
    Write-Host '=============================================================================='
    Write-Host "  started  : $startedAt"
    Write-Host "  host     : $env:PROCESSOR_ARCHITECTURE, $osCaption $osVersion"
    Write-Host "  route    : $Route"
    Write-Host "  source   : $toolsDir\{wgpu-native,codebrix-wgpu}  (vendored; crates.io crates per Cargo.lock)"
    Write-Host ''

    # --- 1. prerequisites, all of them, before anything is compiled ------------------------------
    Write-Host '--- prerequisites ---'
    foreach ($required in @((Join-Path $toolsDir "$($pins['WGPU_NATIVE_DIR'])\src\lib.rs"), (Join-Path $toolsDir "$($pins['WGPU_NATIVE_DIR'])\ffi\webgpu-headers\webgpu.h"),
                            (Join-Path $toolsDir 'codebrix-wgpu\Cargo.toml'), (Join-Path $toolsDir 'codebrix-wgpu\Cargo.lock'),
                            (Join-Path $toolsDir 'codebrix-wgpu\src\codebrix.rs'), (Join-Path $toolsDir 'codebrix-wgpu\bridge\bridge.rs'),
                            (Join-Path $toolsDir 'smoke-test.c'), (Join-Path $toolsDir $pins['EXPECTED_EXPORTS_FILE']),
                            (Join-Path $toolsDir "licenses\$($pins['LICENSE_NAME'])"))) {
        if (-not (Test-Path -LiteralPath $required)) { throw "$required is missing. It is part of the repository; restore it from git." }
    }
    $vs = Find-VisualStudio
    Write-Host "  Visual Studio: $($vs.DisplayName) $($vs.DisplayVersion) [$($vs.Version)] at $($vs.Path)"
    $vcvarsall = Get-VcVarsAllPath $vs.Path
    if ($Triple -like 'aarch64-*' -and -not (Test-Arm64ToolsPresent $vs.Path)) {
        throw @"
The ARM64 C++ build tools are not installed in THIS Visual Studio instance ($($vs.Path)).
Add the component "MSVC Build Tools for ARM64/ARM64EC" (older: "MSVC v143 - VS 2022 C++ ARM64/ARM64EC
build tools (Latest)") to that instance. (The check reads this instance's filesystem on purpose.)
"@
    }
    Import-DeveloperEnvironment -VcVarsAll $vcvarsall -ArchArgument $VcVarsArch
    $clPath  = Assert-OnPath 'cl'      'cl ships with the "Desktop development with C++" workload; it should be on PATH inside the developer environment.'
    $dumpbin = Assert-OnPath 'dumpbin' 'dumpbin ships with the C++ toolset; it should be on PATH inside the developer environment.'
    $null    = Assert-OnPath 'link'    'link.exe ships with the C++ toolset; cargo uses it as the MSVC linker.'
    $rust = Assert-RustToolchain -Toolchain $pins['RUST_TOOLCHAIN'] -Triple $Triple
    $libclang = Assert-Libclang
    $clVersion = Get-ClVersionLine
    $sdkVersion = if ($env:WindowsSDKVersion) { $env:WindowsSDKVersion.TrimEnd('\') } else { 'unknown' }

    Write-Host "  rustc    : $($rust.Rustc)"
    Write-Host "  cargo    : $($rust.Cargo)   [rustup $($rust.Rustup)]"
    Write-Host "  target   : $Triple"
    Write-Host "  libclang : $libclang"
    Write-Host "  cl       : $clVersion  [$clPath]"
    Write-Host "  dumpbin  : $dumpbin"
    Write-Host "  Win SDK  : $sdkVersion"
    if ($CanRun) { Write-Host '  execute  : this host runs the target architecture - the full gate will be applied' }
    else { Write-Host '  execute  : this host CANNOT run the target architecture - the smoke test cannot run and will be reported as a failure' }
    Write-Host ''

    # --- 2. source + build -------------------------------------------------------------------------
    Write-Host '--- source ---'
    $patchesApplied = Copy-SourceToScratch -ToolsDir $toolsDir -ScratchDir $scratchDir -Pins $pins
    Write-Host "  patches applied: $patchesApplied"
    Write-Host ''

    Write-Host '--- building ---'
    if (Test-Path -LiteralPath $targetDir) { Remove-Item -LiteralPath $targetDir -Recurse -Force }
    New-Item -ItemType Directory -Path $BuildRoot -Force | Out-Null
    $builtDll = Invoke-CargoBuild -ScratchDir $scratchDir -TargetDir $targetDir -Triple $Triple -Pins $pins -LogPath (Join-Path $BuildRoot 'build.log')
    Write-Host ''

    Write-Host '--- smoke test program ---'
    $smokeExe = Build-SmokeTest -ToolsDir $toolsDir -ScratchDir $scratchDir -GateDir $gateDir
    $smokeMachine = Get-PeMachine $smokeExe
    if ($smokeMachine -ne $ExpectedMachine) {
        throw ('smoke-test.exe was compiled for machine 0x{0:X4}, not 0x{1:X4}: the developer environment ({2}) is not the target''s.' -f $smokeMachine, $ExpectedMachine, $VcVarsArch)
    }
    Write-Host "  $smokeExe (machine 0x$('{0:X4}' -f $smokeMachine))"
    Write-Host ''

    # --- 3. collect ---------------------------------------------------------------------------------
    Write-Host '--- collecting ---'
    if (Test-Path -LiteralPath $outDir) { Remove-Item -LiteralPath $outDir -Recurse -Force }
    New-Item -ItemType Directory -Path $outDir -Force | Out-Null
    $dllPath = Join-Path $outDir 'codebrix_wgpu.dll'
    Copy-Item -LiteralPath $builtDll -Destination $dllPath
    $builtPdb = [System.IO.Path]::ChangeExtension($builtDll, '.pdb')
    $pdbPath = Join-Path $outDir 'codebrix_wgpu.pdb'
    if (Test-Path -LiteralPath $builtPdb) {
        Copy-Item -LiteralPath $builtPdb -Destination $pdbPath
        Write-Host '  codebrix_wgpu.pdb kept beside the DLL (crash triage; NOT shipped - goes to ..\unstripped\<rid>\ on adoption)'
    }
    else { Write-Host '  [warn] no codebrix_wgpu.pdb was produced - Cargo.toml asks for debug = true; check build.log.' }
    Copy-Item -LiteralPath (Join-Path $toolsDir "licenses\$($pins['LICENSE_NAME'])") -Destination (Join-Path $outDir $pins['LICENSE_NAME'])
    Copy-Item -LiteralPath (Join-Path $BuildRoot 'build.log') -Destination (Join-Path $outDir 'build.log')
    Copy-Item -LiteralPath $smokeExe -Destination (Join-Path $outDir 'smoke-test.exe')
    Write-Host "  $($pins['LICENSE_NAME']) = licenses\$($pins['LICENSE_NAME'])"
    Write-Host "  smoke-test.exe (target architecture) kept in output\$Rid\"
    Write-Host ''

    # --- 4. the gate --------------------------------------------------------------------------------
    Write-Host '--- verifying ---'
    $machineName = if ($ExpectedMachine -eq 0xAA64) { 'ARM64' } else { 'x64' }
    $smokeOutput = Test-CodebrixWgpuDll -DllPath $dllPath -ExpectedMachine $ExpectedMachine -ExpectedMachineName $machineName `
                                        -Pins $pins -ToolsDir $toolsDir -GateDir $gateDir -CanRunTargetBinaries $CanRun

    $reproducible = 'not checked (run with -CheckReproducible)'
    if ($CheckReproducible) {
        Write-Host '  --- reproducibility: a second build into a fresh target folder ---'
        $secondTarget = Join-Path $BuildRoot 'target-2'
        if (Test-Path -LiteralPath $secondTarget) { Remove-Item -LiteralPath $secondTarget -Recurse -Force }
        $second = Invoke-CargoBuild -ScratchDir $scratchDir -TargetDir $secondTarget -Triple $Triple -Pins $pins -LogPath (Join-Path $BuildRoot 'build-2.log')
        if ((Get-Sha256 $second) -eq (Get-Sha256 $builtDll)) { Add-GatePass 'second build is byte-identical'; $reproducible = 'yes - a second build gave identical DLL bytes' }
        else { Add-GateFail 'second build differs - record the differing bytes (fc /b) in ..\BUILD-PROVENANCE.txt'; $reproducible = 'NO - see the gate output' }
    }

    $complete = ($script:GateFailures.Count -eq 0)
    $staticFailures = @($script:GateFailures | Where-Object { $_ -notlike 'smoke test NOT RUN*' })
    if ($staticFailures.Count -gt 0) {
        Write-Host ''
        Write-Host "VERIFICATION FAILED for $Rid. $outDir is left for inspection, but this build must not be adopted."
        exit 1
    }
    $gateStatus = if ($complete) { 'COMPLETE - every check ran and passed' }
                  else { 'INCOMPLETE - cross-built; the static checks passed, the smoke test is UNRUN (this host cannot execute ARM64).' }

    # --- 5. record ----------------------------------------------------------------------------------
    $sha = Get-Sha256 $dllPath
    $size = (Get-Item -LiteralPath $dllPath).Length
    $pdbLine = if (Test-Path -LiteralPath $pdbPath) { "codebrix_wgpu.pdb, $((Get-Item -LiteralPath $pdbPath).Length) bytes, sha256 $(Get-Sha256 $pdbPath) (not shipped)" } else { 'none produced' }
    $sumLines = foreach ($name in @('codebrix_wgpu.dll', 'codebrix_wgpu.pdb', $pins['LICENSE_NAME'], 'smoke-test.exe')) {
        $p = Join-Path $outDir $name
        if (Test-Path -LiteralPath $p) { "$(Get-Sha256 $p)  $name" }
    }
    # LF line endings and no BOM, so `sha256sum -c` on Linux or macOS can check the file as is.
    [System.IO.File]::WriteAllText((Join-Path $outDir 'SHA256SUMS.txt'), (($sumLines -join "`n") + "`n"))
    $stopwatch.Stop()

    $buildInfoText = @"
codebrix_wgpu native library - build information
==============================================================================
RID              : $Rid
Gate status      : $gateStatus
Built            : $startedAt
Build duration   : $([int]$stopwatch.Elapsed.TotalSeconds)s
Built by         : wgpu-native-tools\windows\$ScriptName  (route: $Route)

Build machine
------------------------------------------------------------------------------
OS               : $osCaption $osVersion ($env:PROCESSOR_ARCHITECTURE)
Visual Studio    : $($vs.DisplayName) $($vs.DisplayVersion) [$($vs.Version)]
                   $($vs.Path)
cl / link        : $clVersion (vcvarsall $VcVarsArch)
Windows SDK      : $sdkVersion
rustc            : $($rust.Rustc)
cargo            : $($rust.Cargo)
libclang         : $libclang

Source
------------------------------------------------------------------------------
wgpu-native      : $($pins['WGPU_NATIVE_TAG']) (wgpu-native\; tarball sha256 $($pins['WGPU_NATIVE_TARBALL_SHA256']))
webgpu-headers   : $($pins['WEBGPU_HEADERS_COMMIT'])
Crates           : codebrix-wgpu\Cargo.lock (--locked), wgpu-core $($pins['WGPU_CORE_VERSION'])
Patches applied  : $patchesApplied

Configuration
------------------------------------------------------------------------------
cargo            : $($script:CargoCommandLine)
Profile          : release - opt-level 3, lto fat, codegen-units 1, panic abort, debug true
CRT              : static (+crt-static) - no Visual C++ Redistributable needed
Build warnings   : $($script:BuildWarnings) warning lines in the cargo log

Result
------------------------------------------------------------------------------
File             : codebrix_wgpu.dll  (DllImport("codebrix_wgpu") probes this name)
Size             : $size bytes
SHA256           : $sha
Debug symbols    : $pdbLine
Exports          : $($script:ExportCount) (== specs\expected-exports.txt)
Dependents       : $($script:Dependents -join ' ')
Reproducible     : $reproducible
Licence beside   : $($pins['LICENSE_NAME'])

Smoke test ($(if ($CanRun) { 'ran on this machine' } else { 'NOT RUN - cross build' }))
------------------------------------------------------------------------------
$(($smokeOutput | ForEach-Object { "  $_" }) -join "`n")
"@
    Set-Content -LiteralPath (Join-Path $outDir 'BUILD-INFO.txt') -Value $buildInfoText -Encoding UTF8

    Write-Host ''
    Write-Host '--- done ---'
    Write-Host "  $dllPath"
    Write-Host "  $size bytes, sha256 $sha"
    Write-Host "  $([int]$stopwatch.Elapsed.TotalSeconds)s"
    Write-Host ''

    # --- 6. adopt -----------------------------------------------------------------------------------
    $adopt = (-not $NoAdopt) -and ($complete -or $AdoptCrossBuild)
    if ($adopt) {
        $dest = Join-Path $repoDir "src\CodeBrix.WebGpu.Native\runtimes\$Rid\native"
        New-Item -ItemType Directory -Path $dest -Force | Out-Null
        foreach ($name in @('codebrix_wgpu.dll', $pins['LICENSE_NAME'])) {
            Copy-Item -LiteralPath (Join-Path $outDir $name) -Destination (Join-Path $dest $name) -Force
        }
        Write-Host "adopted -> src\CodeBrix.WebGpu.Native\runtimes\$Rid\native\ (codebrix_wgpu.dll + $($pins['LICENSE_NAME']))"
        if (Test-Path -LiteralPath $pdbPath) {
            $unstripped = Join-Path $toolsDir "unstripped\$Rid"
            New-Item -ItemType Directory -Path $unstripped -Force | Out-Null
            Copy-Item -LiteralPath $pdbPath -Destination (Join-Path $unstripped 'codebrix_wgpu.pdb') -Force
            Write-Host "        -> wgpu-native-tools\unstripped\$Rid\codebrix_wgpu.pdb"
        }
        if (-not $complete) { Write-Host '  [note] adopted with -AdoptCrossBuild: the smoke test has NOT run on ARM64. Say so in ..\BUILD-PROVENANCE.txt.' }
        Write-Host "Record the build in ..\BUILD-PROVENANCE.txt (replace the NOT YET BUILT block for $Rid) from"
        Write-Host "output\$Rid\BUILD-INFO.txt, then commit the adopted files."
    }
    elseif ($NoAdopt) { Write-Host "-NoAdopt: not copied into the package. Result stays in output\$Rid." }

    if (-not $complete -and -not $AdoptCrossBuild) {
        Write-Host 'GATE INCOMPLETE: this DLL was cross-built and has NOT been executed, so it was NOT adopted.'
        Write-Host 'Run build-win-arm64.ps1 on an ARM64 Windows machine, or re-run here with -AdoptCrossBuild.'
        exit 1
    }
    exit 0
}
