================================================================================
README-INDEX: CodeBrix.WebGpu
Map of the README files in this repository
================================================================================

If you are an AI coding agent: find the NuGet package you are consuming below
and read its AGENT-README file in full. Read MAINTAINER-README.txt only if you
are changing this repository itself.

AGENT-README FILES (consumer documentation, one per NuGet package)
------------------------------------------------------------------
  AGENT-README.txt
      CodeBrix.WebGpu.Native.MitLicenseForever - thin, generated .NET
      bindings over the native WebGPU (wgpu) libraries shipped in the
      package, plus native-device access for Vulkan, Direct3D 12 and Metal,
      the device / texture sharing contracts, and the C headers, function
      table and hand-off structs for native modules.
  src/CodeBrix.WebGpu.Skia/AGENT-README.txt
      CodeBrix.WebGpu.Skia.MitLicenseForever - WebGPU rendering into
      SkiaSharp surfaces: one GPU device shared with a GRContext, zero copy /
      GPU copy / readback tiers chosen and reported, the frame protocol,
      headless rendering.
  src/CodeBrix.WebGpu.Graphics/AGENT-README.txt
      CodeBrix.WebGpu.Graphics.MitLicenseForever - a graphics layer over
      WebGPU: one context owning or sharing the device, tracked resources,
      WGSL shaders, pipeline builders, a batched 2D sprite and shape
      renderer, a lit 3D mesh renderer, image and animated-image textures,
      glTF 2.0 models with physically based shading, GPU skinning, morph
      targets and animation playback, image-based lighting with a skybox and
      tone mapping, headless targets and window surfaces.

MAINTAINER AND EXTRAS
---------------------
  MAINTAINER-README.txt
      Building, testing, the binding generator, the specifications, the
      native libraries, bumping wgpu, packaging and provenance notes for
      maintainers.
  EXTRAS-README.txt
      Samples, tools and other non-package content in this repository.

GENERAL
-------
  README.md
      Human-facing overview shown on GitHub and nuget.org
      (CodeBrix.WebGpu.Native's package readme).
  src/CodeBrix.WebGpu.Skia/README.md
      CodeBrix.WebGpu.Skia's package readme.
  src/CodeBrix.WebGpu.Graphics/README.md
      CodeBrix.WebGpu.Graphics' package readme.
  samples/WebGpuModelViewer/README.md
      The model-viewer sample: what it shows, how to run it (live window or
      headless PNG frames) and the credits for its 3D models.
  samples/WebGpuModelViewer.Android/README.md
      The same sample on Android: building, installing and what it logs.
  wgpu-native-tools/README.txt
      Building, gating and adopting the native codebrix_wgpu libraries.
  README-INDEX.txt
      This file.
  THIRD-PARTY-NOTICES.txt
      What came from where, and under which licences.

================================================================================
