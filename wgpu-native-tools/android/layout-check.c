/*
 * layout-check.c - compile-time ABI asserts for the CodeBrix structs of codebrix_wgpu.h
 *
 * android/build.sh compiles this (never links or runs it) with the NDK clang for EACH Android ABI
 * with -Werror: a failing assert stops the build. The values are the LP64 / LLP64 layouts every
 * one of the nine RIDs has (8-byte pointers and size_t), which the managed binding hard-codes as
 * explicit-layout structs; the smoke test then compares the same structs with the NATIVE side's
 * own sizes at run time (wgpuCodebrixGetStructLayout). Builds with any C11 compiler.
 *
 * Copyright (c) 2026 Jeremy Ellis and contributors. SPDX-License-Identifier: MIT
 */
#include <stddef.h>
#include "webgpu.h"
#include "codebrix_wgpu.h"

_Static_assert(sizeof(void *) == 8 && sizeof(size_t) == 8, "a 64-bit ABI");
_Static_assert(sizeof(WGPUStringView) == 16, "WGPUStringView");
_Static_assert(sizeof(WGPUCodebrixName) == 16 && _Alignof(WGPUCodebrixName) == 8, "WGPUCodebrixName == one WGPUStringView");
_Static_assert(sizeof(WGPUCodebrixBuildInfo) == 104 && offsetof(WGPUCodebrixBuildInfo, wgpuNativeVersion) == 8
               && offsetof(WGPUCodebrixBuildInfo, profile) == 72 && offsetof(WGPUCodebrixBuildInfo, patches) == 88, "WGPUCodebrixBuildInfo");
_Static_assert(sizeof(WGPUCodebrixVulkanDeviceInfo) == 88 && offsetof(WGPUCodebrixVulkanDeviceInfo, queueFamilyIndex) == 32
               && offsetof(WGPUCodebrixVulkanDeviceInfo, getInstanceProcAddr) == 48
               && offsetof(WGPUCodebrixVulkanDeviceInfo, enabledInstanceExtensionCount) == 56
               && offsetof(WGPUCodebrixVulkanDeviceInfo, enabledDeviceExtensions) == 80, "WGPUCodebrixVulkanDeviceInfo");
_Static_assert(sizeof(WGPUCodebrixVulkanTextureInfo) == 16 && offsetof(WGPUCodebrixVulkanTextureInfo, sampleCount) == 12, "WGPUCodebrixVulkanTextureInfo");
_Static_assert(sizeof(WGPUCodebrixD3d12DeviceInfo) == 24, "WGPUCodebrixD3d12DeviceInfo");
_Static_assert(sizeof(WGPUCodebrixD3d12TextureInfo) == 16 && offsetof(WGPUCodebrixD3d12TextureInfo, format) == 8, "WGPUCodebrixD3d12TextureInfo");
_Static_assert(sizeof(WGPUCodebrixVulkanAdoptDescriptor) == 96 && offsetof(WGPUCodebrixVulkanAdoptDescriptor, instanceApiVersion) == 32
               && offsetof(WGPUCodebrixVulkanAdoptDescriptor, getInstanceProcAddr) == 40
               && offsetof(WGPUCodebrixVulkanAdoptDescriptor, requiredFeatures) == 88, "WGPUCodebrixVulkanAdoptDescriptor");
_Static_assert(sizeof(WGPUCodebrixD3d12AdoptDescriptor) == 40 && offsetof(WGPUCodebrixD3d12AdoptDescriptor, requiredFeatures) == 32, "WGPUCodebrixD3d12AdoptDescriptor");
_Static_assert(sizeof(WGPUCodebrixMetalAdoptDescriptor) == 24 && offsetof(WGPUCodebrixMetalAdoptDescriptor, requiredFeatures) == 16, "WGPUCodebrixMetalAdoptDescriptor");
_Static_assert(sizeof(WGPUCodebrixDownlevelCapabilities) == 16 && offsetof(WGPUCodebrixDownlevelCapabilities, shaderModel) == 8
               && offsetof(WGPUCodebrixDownlevelCapabilities, webgpuCompliant) == 12, "WGPUCodebrixDownlevelCapabilities");
_Static_assert(sizeof(WGPUCodebrixTextureFormatFeatures) == 16 && offsetof(WGPUCodebrixTextureFormatFeatures, flags) == 8, "WGPUCodebrixTextureFormatFeatures");
_Static_assert(sizeof(WGPUCodebrixVulkanAdoptRequirements) == 32 && offsetof(WGPUCodebrixVulkanAdoptRequirements, physicalDeviceFeatures2) == 16
               && offsetof(WGPUCodebrixVulkanAdoptRequirements, physicalDeviceApiVersion) == 24, "WGPUCodebrixVulkanAdoptRequirements");
_Static_assert(sizeof(WGPUCodebrixFaultCallbacks) == 72 && offsetof(WGPUCodebrixFaultCallbacks, deviceLostCallbackInfo) == 32, "WGPUCodebrixFaultCallbacks");
_Static_assert(sizeof(WGPUCodebrixTextureState) == 4 && WGPUCodebrixTextureState_Present == 0x000C0008, "WGPUCodebrixTextureState");
