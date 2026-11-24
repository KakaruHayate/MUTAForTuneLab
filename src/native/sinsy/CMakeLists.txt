# Sinsy's label builder, as a static library.
#
# Sinsy ships its own copy of the HTS engine API, which must not end up sharing symbols
# with our own muta_hts build: the library is static and only the wrapper in this
# directory is allowed to export anything.
cmake_minimum_required(VERSION 3.20)
project(muta_sinsy CXX C)

# ELF and Mach-O interpose every global symbol by default, and Sinsy links its own copy of
# the HTS engine: only the wrapper's exported functions may be visible, or the two engines
# bind to each other's symbols.
if(NOT MSVC)
  # The static Sinsy library ends up inside a shared object, so every object must be PIC.
  set(CMAKE_POSITION_INDEPENDENT_CODE ON)
  set(CMAKE_C_VISIBILITY_PRESET hidden)
  set(CMAKE_CXX_VISIBILITY_PRESET hidden)
  set(CMAKE_VISIBILITY_INLINES_HIDDEN ON)
endif()
set(SINSY_SRC "" CACHE PATH "Vendored Sinsy tree (contains lib/ and include/)")
if(SINSY_SRC STREQUAL "")
  message(FATAL_ERROR "SINSY_SRC is required")
endif()

file(GLOB_RECURSE SINSY_SOURCES CONFIGURE_DEPENDS ${SINSY_SRC}/lib/*.cpp)

add_library(sinsy STATIC ${SINSY_SOURCES})
target_include_directories(sinsy PUBLIC
  ${SINSY_SRC}/include
  ${SINSY_SRC}/lib
  ${SINSY_SRC}/lib/util
  ${SINSY_SRC}/lib/score
  ${SINSY_SRC}/lib/label
  ${SINSY_SRC}/lib/converter
  ${SINSY_SRC}/lib/japanese
  ${SINSY_SRC}/lib/xml
  ${SINSY_SRC}/lib/temporary
  ${SINSY_SRC}/lib/hts_engine_API
  ${SINSY_SRC}/lib/hts_engine_API/include
  ${SINSY_SRC}/lib/hts_engine_API/lib
  ${SINSY_SRC}/lib/hts_engine_API-1.10/include
  ${SINSY_SRC}/lib/hts_engine_API-1.10/lib
)
if(MSVC)
  target_compile_features(sinsy PUBLIC cxx_std_17)
else()
  # Sinsy 0.92 still uses `throw(Type)` specifications, which C++17 removed: MSVC takes
  # them, GCC and clang do not. C++14 covers everything in the tree.
  set_target_properties(sinsy PROPERTIES CXX_STANDARD 14 CXX_STANDARD_REQUIRED ON)
endif()
if(MSVC)
  target_compile_options(sinsy PRIVATE /W1 /fp:precise /bigobj)
  target_compile_definitions(sinsy PRIVATE _CRT_SECURE_NO_WARNINGS _USE_MATH_DEFINES)
endif()
set_target_properties(sinsy PROPERTIES PREFIX "" OUTPUT_NAME "sinsy")
# The wrapper: the only thing that exports a symbol. Sinsy's HTS engine stays inside.
add_library(muta_sinsy SHARED muta_sinsy.cpp)
target_link_libraries(muta_sinsy PRIVATE sinsy)
if(MSVC)
  target_compile_features(muta_sinsy PRIVATE cxx_std_17)
  target_compile_options(muta_sinsy PRIVATE /W3 /fp:precise)
  target_compile_definitions(muta_sinsy PRIVATE _CRT_SECURE_NO_WARNINGS)
else()
  # Same language level as Sinsy: the wrapper includes its headers.
  set_target_properties(muta_sinsy PROPERTIES CXX_STANDARD 14 CXX_STANDARD_REQUIRED ON)
endif()
set_target_properties(muta_sinsy PROPERTIES PREFIX "" OUTPUT_NAME "muta_sinsy")