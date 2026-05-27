cmake_minimum_required(VERSION 3.20)

project(mimir_camera_capture LANGUAGES C CXX)

add_library(mimir_camera_capture SHARED
    mimir_ks_camera_capture.cpp)

target_compile_features(mimir_camera_capture PRIVATE cxx_std_20)
target_compile_definitions(mimir_camera_capture PRIVATE
    WIN32_LEAN_AND_MEAN
    NOMINMAX)
target_link_libraries(mimir_camera_capture PRIVATE
    setupapi
    ksuser)

set(PSMOVEAPI_ROOT "$ENV{TEMP}/mimir-driver-probes/psmoveapi" CACHE PATH "Root of a psmoveapi checkout with external/libusb-1.0 and external/PS3EYEDriver.")
set(LIBUSB_ROOT "${PSMOVEAPI_ROOT}/external/libusb-1.0" CACHE PATH "libusb source root.")
set(PS3EYE_ROOT "${PSMOVEAPI_ROOT}/external/PS3EYEDriver" CACHE PATH "PS3EYEDriver source root.")

add_library(mimir_libusb_static STATIC
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
target_include_directories(mimir_libusb_static PUBLIC
    ${LIBUSB_ROOT}/libusb
    ${LIBUSB_ROOT}/libusb/os
    ${LIBUSB_ROOT}/msvc)
target_compile_definitions(mimir_libusb_static PRIVATE
    _CRT_SECURE_NO_WARNINGS
    _WIN32_WINNT=0x0601)
target_link_libraries(mimir_libusb_static PRIVATE setupapi ole32 advapi32)

add_library(mimir_ps3eye_vendor STATIC
    ${PS3EYE_ROOT}/src/ps3eye.cpp
    ${PS3EYE_ROOT}/src/ps3eye_capi.cpp)
target_include_directories(mimir_ps3eye_vendor PUBLIC
    ${PS3EYE_ROOT}/src
    ${LIBUSB_ROOT}/libusb)
target_link_libraries(mimir_ps3eye_vendor PUBLIC mimir_libusb_static)

add_library(mimir_ps3eye_capture SHARED
    mimir_ps3eye_capture.cpp)
target_compile_features(mimir_ps3eye_capture PRIVATE cxx_std_20)
target_compile_definitions(mimir_ps3eye_capture PRIVATE
    WIN32_LEAN_AND_MEAN
    NOMINMAX)
target_include_directories(mimir_ps3eye_capture PRIVATE ${PS3EYE_ROOT}/src)
target_link_libraries(mimir_ps3eye_capture PRIVATE mimir_ps3eye_vendor)

add_library(mimir_mf_gpu_capture SHARED
    mimir_mf_gpu_capture.cpp)
target_compile_features(mimir_mf_gpu_capture PRIVATE cxx_std_20)
target_compile_definitions(mimir_mf_gpu_capture PRIVATE
    WIN32_LEAN_AND_MEAN
    NOMINMAX)
target_link_libraries(mimir_mf_gpu_capture PRIVATE
    d3d11
    dxgi
    mf
    mfplat
    mfreadwrite
    mfuuid)
