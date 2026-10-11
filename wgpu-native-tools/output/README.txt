wgpu-native-tools/output/ - build results (NOT committed, except this README and SHA256SUMS)
================================================================================
Every build script writes here, one folder per RID:

  output/<rid>/<library>                the stripped library, exactly as the package ships it
  output/<rid>/LICENSE-WgpuNative.txt   licenses/LICENSE-WgpuNative.txt (wgpu-native MIT +
                                        webgpu-headers BSD-3 + every linked crate's licence)
  output/<rid>/unstripped/              the pre-strip twin (Linux / Android / macOS)
  output/<rid>/BUILD-INFO.txt           toolchain, pins, flags, sizes, sha256, gate results,
                                        reproducibility, the smoke test's output
  output/<rid>/smoke-test*              the gate's smoke-test program, built for the TARGET (on
                                        Windows also kept so a cross-built win-arm64 can finish
                                        its gate on a device)
  output/<rid>/build/                   Linux only: the network-free compile's raw output,
                                        cargo log and compile-info.txt

  output/SHA256SUMS                     the stripped libraries of every RID built so far
  output/host/libcodebrix_wgpu.so       a plain build with this machine's own cargo, for running
                                        the managed tests on a Linux dev box. NEVER shipped (its
                                        glibc floor is whatever the dev box has; unstripped).
                                        Rebuild:  ./build-host.sh

See ../README.txt.
