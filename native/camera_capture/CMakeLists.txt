cmake_minimum_required(VERSION 3.20)

project(mimir_camera_capture LANGUAGES CXX)

add_library(mimir_camera_capture SHARED
    mimir_ks_camera_capture.cpp)

target_compile_features(mimir_camera_capture PRIVATE cxx_std_20)
target_compile_definitions(mimir_camera_capture PRIVATE
    WIN32_LEAN_AND_MEAN
    NOMINMAX)
target_link_libraries(mimir_camera_capture PRIVATE
    setupapi
    ksuser)
