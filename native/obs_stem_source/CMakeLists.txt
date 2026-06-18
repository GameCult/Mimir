cmake_minimum_required(VERSION 3.20)

project(mimir_obs_stem_source LANGUAGES CXX)

find_package(libobs REQUIRED CONFIG)

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
