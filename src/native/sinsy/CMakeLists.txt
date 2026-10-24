# Sinsy's label builder, as a static library.
#
# Sinsy ships its own copy of the HTS engine API, which must not end up sharing symbols
# with our own muta_hts build: the library is static and only the wrapper in this
# directory is allowed to export anything.
cmake_minimum_required(VERSION 3.20)
project(muta_sinsy CXX C)

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
target_compile_features(sinsy PUBLIC cxx_std_17)
if(MSVC)
  target_compile_options(sinsy PRIVATE /W1 /fp:precise /bigobj)
  target_compile_definitions(sinsy PRIVATE _CRT_SECURE_NO_WARNINGS _USE_MATH_DEFINES)
endif()
set_target_properties(sinsy PROPERTIES PREFIX "" OUTPUT_NAME "sinsy")
