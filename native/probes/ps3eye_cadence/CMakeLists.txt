cmake_minimum_required(VERSION 3.20)

project(ps3eye_cadence LANGUAGES C CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

set(PSMOVEAPI_ROOT "$ENV{TEMP}/mimir-driver-probes/psmoveapi" CACHE PATH "Root of a psmoveapi checkout with external/libusb-1.0 and external/PS3EYEDriver.")
set(LIBUSB_ROOT "${PSMOVEAPI_ROOT}/external/libusb-1.0" CACHE PATH "libusb source root.")
set(PS3EYE_ROOT "${PSMOVEAPI_ROOT}/external/PS3EYEDriver" CACHE PATH "PS3EYEDriver source root.")

add_library(libusb_static STATIC
    ${LIBUSB_ROOT}/libusb/core.c
    ${LIBUSB_ROOT}/libusb/descriptor.c
    ${LIBUSB_ROOT}/libusb/hotplug.c
    ${LIBUSB_ROOT}/libusb/io.c
    ${LIBUSB_ROOT}/libusb/strerror.c
    ${LIBUSB_ROOT}/libusb/sync.c
    ${LIBUSB_ROOT}/libusb/os/events_windows.c
    ${LIBUSB_ROOT}/libusb/os/threads_windows.c
    ${LIBUSB_ROOT}/libusb/os/windows_common.c
    ${LIBUSB_ROOT}/libusb/os/windows_usbdk.c
    ${LIBUSB_ROOT}/libusb/os/windows_winusb.c)
target_include_directories(libusb_static PUBLIC
    ${LIBUSB_ROOT}/libusb
    ${LIBUSB_ROOT}/libusb/os
    ${LIBUSB_ROOT}/msvc)
target_compile_definitions(libusb_static PRIVATE
    _CRT_SECURE_NO_WARNINGS
    _WIN32_WINNT=0x0601)
target_link_libraries(libusb_static PRIVATE setupapi ole32 advapi32)

add_library(ps3eye STATIC
    ${PS3EYE_ROOT}/src/ps3eye.cpp
    ${PS3EYE_ROOT}/src/ps3eye_capi.cpp)
target_include_directories(ps3eye PUBLIC
    ${PS3EYE_ROOT}/src
    ${LIBUSB_ROOT}/libusb)
target_link_libraries(ps3eye PUBLIC libusb_static)

add_executable(ps3eye_dual_cadence ps3eye_dual_cadence.cpp)
target_include_directories(ps3eye_dual_cadence PRIVATE ${PS3EYE_ROOT}/src)
target_link_libraries(ps3eye_dual_cadence PRIVATE ps3eye)
