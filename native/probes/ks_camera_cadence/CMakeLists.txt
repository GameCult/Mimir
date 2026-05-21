cmake_minimum_required(VERSION 3.20)

project(ks_camera_cadence LANGUAGES CXX)

add_executable(ks_camera_cadence
    ks_camera_cadence.cpp)

target_compile_features(ks_camera_cadence PRIVATE cxx_std_20)
target_compile_definitions(ks_camera_cadence PRIVATE
    WIN32_LEAN_AND_MEAN
    NOMINMAX)
target_link_libraries(ks_camera_cadence PRIVATE
    setupapi
    ksuser)
