# worldline: the vocoder, built from the vendored sources.
#
# Two upstream trees are involved:
#   third_party/worldline  the OpenUtau implementation (MIT), plus a stub for its pyin
#                          F0 estimator, which we never call
#   third_party/world      WORLD at the pinned commit (modified BSD), already carrying
#                          OpenUtau's world.patch
#
# Usage (from the repository root):
#   pwsh tools/build-native.ps1
#
cmake_minimum_required(VERSION 3.20)
project(worldline CXX C)

# ELF and Mach-O interpose every global symbol by default, and Sinsy links its own copy of
# the HTS engine: only the wrapper's exported functions may be visible, or the two engines
# bind to each other's symbols.
if(NOT MSVC)
  set(CMAKE_C_VISIBILITY_PRESET hidden)
  set(CMAKE_CXX_VISIBILITY_PRESET hidden)
  set(CMAKE_VISIBILITY_INLINES_HIDDEN ON)
endif()
set(WORLDLINE_SRC "" CACHE PATH "Vendored OpenUtau worldline directory")
set(WORLD_SRC "" CACHE PATH "Vendored WORLD directory (contains world/*.h)")
if(WORLDLINE_SRC STREQUAL "" OR WORLD_SRC STREQUAL "")
  message(FATAL_ERROR "WORLDLINE_SRC and WORLD_SRC are required")
endif()

add_library(worldline SHARED
  ${WORLDLINE_SRC}/worldline.cpp
  ${WORLDLINE_SRC}/common/vec_utils.cpp
  ${WORLDLINE_SRC}/model/continuous_noise.cpp
  ${WORLDLINE_SRC}/model/effects.cpp
  ${WORLDLINE_SRC}/f0/dio_estimator.cpp
  ${WORLDLINE_SRC}/f0/dio_ss_estimator.cpp
  ${WORLDLINE_SRC}/f0/harvest_estimator.cpp
  ${WORLDLINE_SRC}/pyin_stub.cpp
  ${WORLD_SRC}/cheaptrick.cpp
  ${WORLD_SRC}/codec.cpp
  ${WORLD_SRC}/common.cpp
  ${WORLD_SRC}/d4c.cpp
  ${WORLD_SRC}/dio.cpp
  ${WORLD_SRC}/fft.cpp
  ${WORLD_SRC}/harvest.cpp
  ${WORLD_SRC}/matlabfunctions.cpp
  ${WORLD_SRC}/stonemask.cpp
  ${WORLD_SRC}/synthesis.cpp
)

# "worldline/..." resolves from the vendored tree's parent; "world/..." from WORLD itself.
target_include_directories(worldline PRIVATE
  ${WORLDLINE_SRC}/..
  ${WORLDLINE_SRC}/include
  ${WORLD_SRC}
)

target_compile_features(worldline PRIVATE cxx_std_17)
# MSVC does not expose M_PI from <cmath> unless asked.
target_compile_definitions(worldline PRIVATE _USE_MATH_DEFINES)
if(MSVC)
  # /fp:precise keeps the compiler from reassociating or contracting our arithmetic.
  target_compile_options(worldline PRIVATE /O2 /W2 /fp:precise)
else()
  target_compile_options(worldline PRIVATE -O2 -ffp-contract=off)
endif()

set_target_properties(worldline PROPERTIES
  PREFIX ""
  OUTPUT_NAME "worldline"
)
