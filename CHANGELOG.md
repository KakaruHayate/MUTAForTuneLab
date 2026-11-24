# Changelog

## 0.2.0

- Linux x64 and macOS arm64 packages: the native HTS, worldline and Sinsy libraries build
  with the platform toolchain (gcc / clang), and R2 uses the WebGPU plugin execution
  provider of the latest ONNX Runtime. Windows keeps DirectML.
- Every platform now has a lite build next to the full one.
- Documentation and release notes shortened.

## 0.1.0

First release. Chinese and Japanese singing from MUTA voice banks, worldline R1.1 and
optional R2 vocoders, pinned phonemes, continuations, breaths, per-note bank register
offset, seven automation lanes, incremental rendering.
