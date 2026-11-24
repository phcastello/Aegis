cmake_minimum_required(VERSION 3.22.1)
project(aegis_aec3 LANGUAGES C CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)
set(CMAKE_C_VISIBILITY_PRESET hidden)
set(CMAKE_CXX_VISIBILITY_PRESET hidden)
set(CMAKE_VISIBILITY_INLINES_HIDDEN ON)

include(FetchContent)
# Upstream packaged WebRTC APM (including AEC3), and its pinned Abseil LTS.
# Hash-checked build-time source downloads; no system WebRTC install is needed.
FetchContent_Declare(
    aegis_webrtc_apm
    URL https://freedesktop.org/software/pulseaudio/webrtc-audio-processing/webrtc-audio-processing-2.1.tar.xz
    URL_HASH SHA256=ae9302824b2038d394f10213cab05312c564a038434269f11dbf68f511f9f9fe
)
FetchContent_Declare(
    aegis_abseil
    URL https://github.com/abseil/abseil-cpp/releases/download/20240722.0/abseil-cpp-20240722.0.tar.gz
    URL_HASH SHA256=f50e5ac311a81382da7fa75b97310e4b9006474f9560ac46f54a9967f07d4ae3
)
set(BUILD_SHARED_LIBS OFF)
set(ABSL_PROPAGATE_CXX_STD ON CACHE BOOL "" FORCE)
set(ABSL_BUILD_TESTING OFF CACHE BOOL "" FORCE)
set(ABSL_BUILD_TEST_HELPERS OFF CACHE BOOL "" FORCE)
set(ABSL_ENABLE_INSTALL OFF CACHE BOOL "" FORCE)
FetchContent_MakeAvailable(aegis_abseil aegis_webrtc_apm)

set(APM_DIR "${aegis_webrtc_apm_SOURCE_DIR}/webrtc")
include(WebRtcApmSources.cmake)

set(APM_DEFINES WEBRTC_POSIX WEBRTC_LINUX WEBRTC_LIBRARY_IMPL
    WEBRTC_APM_DEBUG_DUMP=0 NDEBUG)
set(APM_SYSTEM_LIBS)
if(ANDROID)
    list(APPEND APM_DEFINES WEBRTC_ANDROID)
    list(APPEND AEGIS_APM_COMMON_SOURCES rtc_base/system/warn_current_thread_is_deadlocked.cc)
    list(APPEND APM_SYSTEM_LIBS log)
else()
    find_package(Threads REQUIRED)
    list(APPEND APM_SYSTEM_LIBS Threads::Threads m)
endif()

# WebRTC's default x86 build dispatches SIMD code according to CPU features.
# Only the corresponding source files receive the optional ISA flags.
if(ANDROID_ABI MATCHES "^x86" OR (NOT ANDROID AND CMAKE_SYSTEM_PROCESSOR MATCHES "^(x86_64|i.86|AMD64)$"))
    list(APPEND APM_DEFINES WEBRTC_ENABLE_AVX2 WAP_DISABLE_INLINE_SSE)
    list(TRANSFORM AEGIS_APM_SSE_SOURCES PREPEND "${APM_DIR}/")
    list(TRANSFORM AEGIS_APM_AVX_SOURCES PREPEND "${APM_DIR}/")
    set_source_files_properties(${AEGIS_APM_SSE_SOURCES} PROPERTIES COMPILE_OPTIONS "-msse2")
    set_source_files_properties(${AEGIS_APM_AVX_SOURCES} PROPERTIES COMPILE_OPTIONS "-mavx2;-mfma")
    set_source_files_properties("${APM_DIR}/third_party/pffft/src/pffft.c" PROPERTIES COMPILE_DEFINITIONS "_GNU_SOURCE;PFFFT_SIMD_DISABLE")
    set(AEGIS_APM_ARCH_SOURCES ${AEGIS_APM_SSE_SOURCES} ${AEGIS_APM_AVX_SOURCES})
elseif(ANDROID_ABI STREQUAL "arm64-v8a" OR (NOT ANDROID AND CMAKE_SYSTEM_PROCESSOR MATCHES "^(aarch64|arm64)$"))
    # AArch64 has NEON available in the ABI. Use upstream accelerated kernels.
    list(APPEND APM_DEFINES WEBRTC_ARCH_ARM64 WEBRTC_HAS_NEON)
    list(APPEND AEGIS_APM_COMMON_SOURCES ${AEGIS_APM_NEON_SOURCES})
elseif(ANDROID_ABI STREQUAL "armeabi-v7a")
    # The scalar fixed-point path also works on older ARMv7 devices. Keep it
    # portable and avoid optional ISA assumptions in the fallback library.
    set_source_files_properties("${APM_DIR}/third_party/pffft/src/pffft.c" PROPERTIES COMPILE_DEFINITIONS "_GNU_SOURCE;PFFFT_SIMD_DISABLE")
else()
    message(FATAL_ERROR "Unsupported AEC3 build architecture: ${ANDROID_ABI}/${CMAKE_SYSTEM_PROCESSOR}")
endif()

list(TRANSFORM AEGIS_APM_COMMON_SOURCES PREPEND "${APM_DIR}/")
add_library(aegis_aec3 SHARED
    AegisEcho_jni.cpp
    ${AEGIS_APM_COMMON_SOURCES}
    ${AEGIS_APM_ARCH_SOURCES}
)
target_include_directories(aegis_aec3 PRIVATE "${APM_DIR}")
target_compile_definitions(aegis_aec3 PRIVATE ${APM_DEFINES})
# Keep the WebRTC fixed-point routines' signed overflow semantics conservative.
target_compile_options(aegis_aec3 PRIVATE -O2 -fwrapv -fno-strict-aliasing -ffunction-sections -fdata-sections)

# AEC3 and the separately linked libfvad both include WebRTC signal processing.
# Hide everything except the closed JNI contract, including static Abseil symbols.
target_link_options(aegis_aec3 PRIVATE
    -Wl,--gc-sections
    -Wl,--exclude-libs,ALL
    "-Wl,--version-script=${CMAKE_CURRENT_SOURCE_DIR}/exports.map"
)
target_link_libraries(aegis_aec3 PRIVATE
    absl::base absl::flags absl::strings absl::numeric absl::synchronization
    absl::bad_optional_access ${APM_SYSTEM_LIBS}
)

if(NOT ANDROID)
    find_path(AEGIS_JNI_INCLUDE jni.h HINTS "$ENV{JAVA_HOME}/include" REQUIRED)
    find_path(AEGIS_JNI_PLATFORM_INCLUDE jni_md.h HINTS "$ENV{JAVA_HOME}/include/linux" REQUIRED)
    target_include_directories(aegis_aec3 PRIVATE "${AEGIS_JNI_INCLUDE}" "${AEGIS_JNI_PLATFORM_INCLUDE}")
endif()
