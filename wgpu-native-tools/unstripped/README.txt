================================================================================
wgpu-native-tools/unstripped - the durable home for pre-strip binaries
================================================================================

UNLIKE output/, THIS FOLDER IS COMMITTED. It keeps the unstripped mate of every
codebrix_wgpu binary that is shipped in the package, so crash dumps of a
stripped release binary can be symbolised after the build machine is gone.
Nothing here is shipped, and nothing here is an input to any build or pack step.

One folder per runtime identifier, mirroring
../../src/CodeBrix.WebGpu.Native/runtimes/<rid>/native/:

  <rid>/libcodebrix_wgpu.so      pre-strip ELF with DWARF (Linux, Android). The
                                 debug sections are zlib-COMPRESSED
                                 (--compress-debug-sections=zlib, set by
                                 codebrix-wgpu/build.rs): ~24 MB instead of ~70 MB.
                                 gdb, lldb, addr2line, eu-stack and
                                 llvm-symbolizer read compressed sections directly.
  <rid>/libcodebrix_wgpu.dylib   pre-strip Mach-O (macOS; the .dSYM stays in output/)
  <rid>/codebrix_wgpu.pdb        the PDB of the shipped DLL (Windows)

The build scripts copy the twin here automatically when they adopt a binary
(ADOPT=1 / no -NoAdopt). Refresh SHA256SUMS after adding one:

    sha256sum */libcodebrix_wgpu.so */*.dylib */*.pdb > SHA256SUMS   # (whichever exist)

Verify a twin: its sha256 is the "SHA256 unstripped" (or "PDB") line of that
RID in ../BUILD-PROVENANCE.txt, and its build-id equals the shipped file's:

    readelf -n <rid>/libcodebrix_wgpu.so | grep 'Build ID'
    readelf -n ../../src/CodeBrix.WebGpu.Native/runtimes/<rid>/native/libcodebrix_wgpu.so | grep 'Build ID'

For Windows the link is the PDB's GUID + age: the DLL's CodeView (RSDS) record
names "codebrix_wgpu.pdb" with a GUID, and the .pdb carries the same GUID.
From a developer prompt:

    dumpbin /headers ../../src/CodeBrix.WebGpu.Native/runtimes/<rid>/native/codebrix_wgpu.dll | findstr RSDS

STORED SO FAR: see ../BUILD-PROVENANCE.txt (one block per RID, build-id and
sha256 of each twin).
