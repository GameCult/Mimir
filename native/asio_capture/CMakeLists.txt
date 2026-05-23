cmake_minimum_required(VERSION 3.20)

project(mimir_asio_capture LANGUAGES CXX)

add_library(mimir_asio_capture SHARED
    mimir_asio_capture.cpp)

target_compile_features(mimir_asio_capture PRIVATE cxx_std_20)
target_compile_definitions(mimir_asio_capture PRIVATE
    WIN32_LEAN_AND_MEAN
    NOMINMAX
    _WIN32_WINNT=0x0A00)
target_link_libraries(mimir_asio_capture PRIVATE
    ole32
    oleaut32)
