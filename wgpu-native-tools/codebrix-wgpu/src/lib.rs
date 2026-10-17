// =================================================================================================
// codebrix_wgpu - wgpu-native + the CodeBrix extensions, as ONE native library
// =================================================================================================
//
// The library exports exactly:
//   * every function wgpu-native implements (webgpu.h + wgpu.h): they are wgpu-native's own
//     `#[no_mangle] extern "C"` items, compiled in its rlib and exported from this cdylib - the
//     re-export below keeps the dependency linked and documents the intent; the gate's export-set
//     check (nm / dumpbin against specs/expected-exports.txt) is the proof on every RID;
//   * the wgpuCodebrix* functions of specs/codebrix.yml (codebrix.rs; C header
//     include/codebrix_wgpu.h).
// Nothing else: a cdylib exports only C-ABI items; Rust std and every crate's Rust symbols stay
// local (checked by the gate).
//
// Copyright (c) 2026 Jeremy Ellis and contributors. SPDX-License-Identifier: MIT
// =================================================================================================

#![allow(clippy::missing_safety_doc)]

pub use wgpu_native::*;

pub mod codebrix;
mod layouts;
mod proc_status;
#[cfg(codebrix_vulkan)]
mod vk_format;
