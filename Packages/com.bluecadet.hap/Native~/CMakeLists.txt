cmake_minimum_required(VERSION 3.15)
project(bluecadet_hap C)

set(CMAKE_C_STANDARD 11)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)
set(CMAKE_C_VISIBILITY_PRESET hidden)

# Sources
set(PLUGIN_SOURCES
    src/bluecadet_hap.c
    src/hap_demux.c
    src/hap_decode.c
    src/ring_buffer.c
    vendor/hap/hap.c
    vendor/snappy/snappy-c.c
)

add_library(bluecadet_hap SHARED ${PLUGIN_SOURCES})

target_include_directories(bluecadet_hap PRIVATE
    src
    vendor/hap
    vendor/snappy
    vendor/minimp4
)

# Platform-specific settings
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
    target_link_libraries(bluecadet_hap PRIVATE pthread)
else()
    set_target_properties(bluecadet_hap PROPERTIES
        OUTPUT_NAME "bluecadet_hap"
        PREFIX "lib"
        SUFFIX ".so"
    )
    target_link_libraries(bluecadet_hap PRIVATE pthread)
endif()

# Install/copy to Plugins directory (root — Unity auto-detects platform)
set(PLUGIN_DEST "${CMAKE_SOURCE_DIR}/../Plugins")

install(TARGETS bluecadet_hap DESTINATION "${PLUGIN_DEST}")

# Convenience: copy after build
add_custom_command(TARGET bluecadet_hap POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E make_directory "${PLUGIN_DEST}"
    COMMAND ${CMAKE_COMMAND} -E copy "$<TARGET_FILE:bluecadet_hap>" "${PLUGIN_DEST}/"
)
