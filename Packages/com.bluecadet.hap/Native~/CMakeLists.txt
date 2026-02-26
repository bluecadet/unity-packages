cmake_minimum_required(VERSION 3.15)
project(bluecadet_hap C CXX)

set(CMAKE_C_STANDARD 11)
set(CMAKE_CXX_STANDARD 11)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)
set(CMAKE_C_VISIBILITY_PRESET hidden)
set(CMAKE_CXX_VISIBILITY_PRESET hidden)

# ─────────────────────────────────────────────────────────────────────────────
# Snappy (Google's official library)
# ─────────────────────────────────────────────────────────────────────────────

# Disable snappy's tests, benchmarks, and install targets
set(SNAPPY_BUILD_TESTS OFF CACHE BOOL "" FORCE)
set(SNAPPY_BUILD_BENCHMARKS OFF CACHE BOOL "" FORCE)
set(SNAPPY_INSTALL OFF CACHE BOOL "" FORCE)

# Enable AVX2 SIMD in Snappy's decompressor (speeds up bulk memcpy operations
# inside Snappy's copy instructions). Requires a Haswell-or-later CPU (2013+).
set(SNAPPY_REQUIRE_AVX2 ON CACHE BOOL "" FORCE)

add_subdirectory(vendor/snappy)

# ─────────────────────────────────────────────────────────────────────────────
# Plugin sources
# ─────────────────────────────────────────────────────────────────────────────

set(PLUGIN_SOURCES
    src/bluecadet_hap.c
    src/hap_demux.c
    src/hap_decode.c
    vendor/hap/hap.c
)

add_library(bluecadet_hap SHARED ${PLUGIN_SOURCES})

target_include_directories(bluecadet_hap PRIVATE
    src
    vendor/hap
    vendor/snappy
    ${CMAKE_BINARY_DIR}/vendor/snappy  # For generated snappy-stubs-public.h
    vendor/minimp4
)

target_link_libraries(bluecadet_hap PRIVATE snappy)

# ─────────────────────────────────────────────────────────────────────────────
# Release optimizations
# ─────────────────────────────────────────────────────────────────────────────

if(MSVC)
    # /O2       — maximum speed (inlining, loop unrolling, auto-vectorisation)
    # /GL       — whole-program optimisation (inlines across translation units)
    # /arch:AVX2 — emit AVX2 instructions; enables auto-vectorisation of our
    #              decode loops and matches the Snappy AVX2 SIMD paths above.
    #              Requires Haswell or later (Intel 2013+, AMD 2015+).
    target_compile_options(bluecadet_hap PRIVATE
        $<$<CONFIG:Release>:/O2 /GL /arch:AVX2>
        $<$<CONFIG:RelWithDebInfo>:/O2 /GL /arch:AVX2>
    )
    # /LTCG — link-time code generation, required companion to /GL
    target_link_options(bluecadet_hap PRIVATE
        $<$<CONFIG:Release>:/LTCG>
        $<$<CONFIG:RelWithDebInfo>:/LTCG>
    )
endif()

# ─────────────────────────────────────────────────────────────────────────────
# Platform-specific settings
# ─────────────────────────────────────────────────────────────────────────────

if(WIN32)
    set_target_properties(bluecadet_hap PROPERTIES
        OUTPUT_NAME "bluecadet_hap"
        SUFFIX ".dll"
    )
    target_compile_definitions(bluecadet_hap PRIVATE _CRT_SECURE_NO_WARNINGS)
elseif(APPLE)
    set_target_properties(bluecadet_hap PROPERTIES
        OUTPUT_NAME "bluecadet_hap"
        PREFIX ""
        SUFFIX ".bundle"
    )
else()
    set_target_properties(bluecadet_hap PROPERTIES
        OUTPUT_NAME "bluecadet_hap"
        PREFIX "lib"
        SUFFIX ".so"
    )
endif()

# ─────────────────────────────────────────────────────────────────────────────
# Output to Plugins directory
# ─────────────────────────────────────────────────────────────────────────────

set(PLUGIN_DEST "${CMAKE_SOURCE_DIR}/../Plugins")

install(TARGETS bluecadet_hap DESTINATION "${PLUGIN_DEST}")

# Copy after build for convenience
add_custom_command(TARGET bluecadet_hap POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E make_directory "${PLUGIN_DEST}"
    COMMAND ${CMAKE_COMMAND} -E copy "$<TARGET_FILE:bluecadet_hap>" "${PLUGIN_DEST}/"
)
