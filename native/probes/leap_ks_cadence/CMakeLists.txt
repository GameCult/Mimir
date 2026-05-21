cmake_minimum_required(VERSION 3.20)

project(leap_ks_cadence LANGUAGES CXX)

add_executable(leap_ks_cadence
    leap_ks_cadence.cpp)

target_compile_features(leap_ks_cadence PRIVATE cxx_std_20)
target_compile_definitions(leap_ks_cadence PRIVATE
    WIN32_LEAN_AND_MEAN
    NOMINMAX)
target_link_libraries(leap_ks_cadence PRIVATE
    setupapi
    ksuser)
