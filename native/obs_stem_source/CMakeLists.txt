cmake_minimum_required(VERSION 3.20)

project(mimir_obs_stem_source LANGUAGES CXX)

find_package(libobs REQUIRED CONFIG)
include(CTest)

add_library(mimir_obs_stem_source MODULE
    mimir_obs_muninn_source.cpp
    mimir_obs_program_texture_source.cpp
    mimir_obs_stem_source.cpp)

target_compile_features(mimir_obs_stem_source PRIVATE cxx_std_20)
target_compile_definitions(mimir_obs_stem_source PRIVATE
    WIN32_LEAN_AND_MEAN
    NOMINMAX
    _WIN32_WINNT=0x0A00)
target_link_libraries(mimir_obs_stem_source PRIVATE OBS::libobs d3d11 d3d12 dxgi ws2_32)

if(EXISTS "${CMAKE_CURRENT_SOURCE_DIR}/../../tools/muninn_media_decoder_probe.cpp")
    add_executable(muninn_media_decoder_probe
        ../../tools/muninn_media_decoder_probe.cpp)
    target_compile_features(muninn_media_decoder_probe PRIVATE cxx_std_20)
    target_include_directories(muninn_media_decoder_probe PRIVATE
        ${CMAKE_CURRENT_SOURCE_DIR})
endif()

add_executable(muninn_rudp_url_tests
    tests/muninn_rudp_url_tests.cpp)
target_compile_features(muninn_rudp_url_tests PRIVATE cxx_std_20)
target_include_directories(muninn_rudp_url_tests PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR})
add_test(NAME muninn_rudp_url_tests COMMAND muninn_rudp_url_tests)

add_executable(muninn_media_wire_tests
    tests/muninn_media_wire_tests.cpp)
target_compile_features(muninn_media_wire_tests PRIVATE cxx_std_20)
target_include_directories(muninn_media_wire_tests PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR})
add_test(NAME muninn_media_wire_tests COMMAND muninn_media_wire_tests)

add_executable(muninn_audio_fec_tests
    tests/muninn_audio_fec_tests.cpp)
target_compile_features(muninn_audio_fec_tests PRIVATE cxx_std_20)
target_include_directories(muninn_audio_fec_tests PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR})
add_test(NAME muninn_audio_fec_tests COMMAND muninn_audio_fec_tests)

add_executable(muninn_udp_socket_tests
    tests/muninn_udp_socket_tests.cpp)
target_compile_features(muninn_udp_socket_tests PRIVATE cxx_std_20)
target_compile_definitions(muninn_udp_socket_tests PRIVATE
    WIN32_LEAN_AND_MEAN
    NOMINMAX
    _WIN32_WINNT=0x0A00)
target_include_directories(muninn_udp_socket_tests PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR})
target_link_libraries(muninn_udp_socket_tests PRIVATE ws2_32)
add_test(NAME muninn_udp_socket_tests COMMAND muninn_udp_socket_tests)
