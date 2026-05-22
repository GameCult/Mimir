cmake_minimum_required(VERSION 3.20)

project(wasapi_audio_cadence LANGUAGES CXX)

add_executable(wasapi_audio_cadence
    wasapi_audio_cadence.cpp)

target_compile_features(wasapi_audio_cadence PRIVATE cxx_std_20)
target_compile_definitions(wasapi_audio_cadence PRIVATE
    WIN32_LEAN_AND_MEAN
    NOMINMAX
    _WIN32_WINNT=0x0A00)
target_link_libraries(wasapi_audio_cadence PRIVATE
    ole32
    avrt
    crypt32)
