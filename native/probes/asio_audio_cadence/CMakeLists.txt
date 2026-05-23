cmake_minimum_required(VERSION 3.20)

project(asio_audio_cadence LANGUAGES CXX)

add_executable(asio_audio_cadence
    asio_audio_cadence.cpp)

target_compile_features(asio_audio_cadence PRIVATE cxx_std_20)
target_compile_definitions(asio_audio_cadence PRIVATE
    WIN32_LEAN_AND_MEAN
    NOMINMAX
    _WIN32_WINNT=0x0A00)
target_link_libraries(asio_audio_cadence PRIVATE
    ole32
    oleaut32)
