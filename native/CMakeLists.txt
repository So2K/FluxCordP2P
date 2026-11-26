cmake_minimum_required(VERSION 3.21)
project(FluxCordMedia VERSION 1.0.0 LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_INTERPROCEDURAL_OPTIMIZATION_RELEASE ON)

# Reuse prebuilt static vcpkg libs (opus, libsodium) from Discord2 when present
set(D2_DEPS "C:/Users/User/Projects/Discord2/vcpkg_installed/x64-windows-static-md" CACHE PATH "Prebuilt vcpkg static deps")
set(DAVE_DIR "${CMAKE_SOURCE_DIR}/third_party/libdave")

find_library(OPUS_LIB opus PATHS "${D2_DEPS}/lib" NO_DEFAULT_PATH REQUIRED)
find_library(SODIUM_LIB libsodium PATHS "${D2_DEPS}/lib" NO_DEFAULT_PATH REQUIRED)
find_library(DAVE_LIB libdave PATHS "${DAVE_DIR}/lib" NO_DEFAULT_PATH REQUIRED)

add_library(FluxCord.Media SHARED
    src/FluxCordMedia.h
    src/FluxCordMedia.cpp
    src/NoiseSuppressor.h
    src/NoiseSuppressor.cpp
    src/VoiceEngine.h
    src/VoiceEngine.cpp
    src/ScreenEngine.h
    src/ScreenEngine.cpp
    src/VoiceTransport.h
    src/VoiceTransport.cpp
    src/StreamReceiver.h
    src/StreamReceiver.cpp
)

target_include_directories(FluxCord.Media PRIVATE
    "${D2_DEPS}/include"
    "${D2_DEPS}/include/opus"
    "${DAVE_DIR}/include"
    src
)

target_compile_definitions(FluxCord.Media PRIVATE
    FLUXCORD_MEDIA_EXPORTS
    SODIUM_STATIC
    _CRT_SECURE_NO_WARNINGS
    WIN32_LEAN_AND_MEAN
    NOMINMAX
    UNICODE
    _UNICODE
)

target_compile_options(FluxCord.Media PRIVATE
    /utf-8
    /W3
    /permissive-
    $<$<CONFIG:Release>:/O2 /Oi /Ot /arch:AVX2 /fp:fast>
)

target_link_libraries(FluxCord.Media PRIVATE
    ${OPUS_LIB}
    ${SODIUM_LIB}
    ${DAVE_LIB}
    d3d11
    dxgi
    mfplat
    mf
    mfuuid
    mfreadwrite
    evr
    wmcodecdspuuid
    ole32
    avrt
    ws2_32
    user32
    gdi32
    dwmapi
    winmm
    mmdevapi
)

set_target_properties(FluxCord.Media PROPERTIES
    PREFIX ""
    OUTPUT_NAME "FluxCord.Media"
)

add_executable(FluxCord.Tests
    tests/media_tests.cpp
    src/NoiseSuppressor.cpp
    src/VoiceEngine.cpp
    src/VoiceTransport.cpp
)
target_include_directories(FluxCord.Tests PRIVATE
    "${D2_DEPS}/include"
    "${D2_DEPS}/include/opus"
    "${DAVE_DIR}/include"
    src
)
target_compile_definitions(FluxCord.Tests PRIVATE
    SODIUM_STATIC
    _CRT_SECURE_NO_WARNINGS
    WIN32_LEAN_AND_MEAN
    NOMINMAX
    UNICODE
    _UNICODE
)
target_compile_options(FluxCord.Tests PRIVATE /utf-8 /W3 /permissive- $<$<CONFIG:Release>:/O2 /Oi /Ot /arch:AVX2 /fp:fast>)
target_link_libraries(FluxCord.Tests PRIVATE
    ${OPUS_LIB}
    ${SODIUM_LIB}
    ${DAVE_LIB}
    ole32
    avrt
    ws2_32
    winmm
)
