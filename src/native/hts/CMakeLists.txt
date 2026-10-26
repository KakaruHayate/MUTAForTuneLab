# muta_hts: the HTS engine built for this plugin.
#
# The HTS sources are not compiled in place: build-native.ps1 copies
# third_party/hts_engine_API into a scratch directory and applies the patches from
# its patches/ folder there, so the vendored tree stays pristine and the diff
# against upstream stays reviewable. Pass the patched copy as -DHTS_SRC=<dir>.
#
# Usage (from the repository root):
#   pwsh tools/build-native.ps1
#
cmake_minimum_required(VERSION 3.20)
project(muta_hts C)

set(HTS_SRC "" CACHE PATH "Patched hts_engine_API source directory")
if(HTS_SRC STREQUAL "")
  message(FATAL_ERROR "HTS_SRC is required: -DHTS_SRC=<patched hts_engine_API dir>")
endif()

# WORLD_vocoder.c is deliberately absent: it would pull in the WORLD library.
# hts_world_stubs.c provides the same three symbols as no-ops, because this plugin
# never asks HTS for a waveform (worldline does that).
add_library(muta_hts SHARED
  muta_hts.c
  hts_world_stubs.c
  ${HTS_SRC}/lib/HTS_audio.c
  ${HTS_SRC}/lib/HTS_engine.c
  ${HTS_SRC}/lib/HTS_gstream.c
  ${HTS_SRC}/lib/HTS_label.c
  ${HTS_SRC}/lib/HTS_misc.c
  ${HTS_SRC}/lib/HTS_model.c
  ${HTS_SRC}/lib/HTS_pstream.c
  ${HTS_SRC}/lib/HTS_sstream.c
  ${HTS_SRC}/lib/HTS_vocoder.c
)

target_include_directories(muta_hts PRIVATE
  ${HTS_SRC}/include
  ${HTS_SRC}/lib
)

# HTS_audio.c reaches for winmm even though we never open an audio device.
if(WIN32)
  target_link_libraries(muta_hts PRIVATE winmm)
endif()

# Frames are indexed in hot loops; keep the optimiser on and the CRT quiet about
# the upstream code's pre-C99 habits.
target_compile_definitions(muta_hts PRIVATE _CRT_SECURE_NO_WARNINGS)
if(MSVC)
  target_compile_options(muta_hts PRIVATE /O2 /W3)
  # Keeps a .map beside the binary so a crash address can be named.
  target_link_options(muta_hts PRIVATE "/MAP")
else()
  target_compile_options(muta_hts PRIVATE -O2)
endif()

set_target_properties(muta_hts PROPERTIES
  PREFIX ""
  OUTPUT_NAME "muta_hts"
)
