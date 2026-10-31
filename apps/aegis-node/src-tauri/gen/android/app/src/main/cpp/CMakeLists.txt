cmake_minimum_required(VERSION 3.22.1)
project("aegis_microwakeword")

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# Global optimization flags (applied to all targets including fetched dependencies)
add_compile_options(-O3 -ffunction-sections -fdata-sections)
add_link_options(-Wl,--gc-sections)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

include(FetchContent)

# Local endpointing only; the selected microWakeWord model/frontend/event rules do not change.
FetchContent_Declare(
    aegis_libfvad
    URL https://github.com/dpirch/libfvad/archive/532ab666c20d3cfda38bca63abbb0f152706c369.tar.gz
    URL_HASH SHA256=bf639e4a5bc37b1c90e047c42524e80a1d0cecadeb6e0a61a33580fca22a00ab
)

# Exact frontend + fixed-point KissFFT used by accepted pymicro-features 2.0.2.
# HA's newer KissFFT/C build had sparse raw-feature differences in direct replay.
FetchContent_Declare(
    aegis_frontend_source
    URL https://github.com/rhasspy/pymicro-features/archive/96bd69cfad79aa67697e176570d3dd87052c3def.tar.gz
    URL_HASH SHA256=92ea30a9351418a94f084e2af491140607dd8443dfc91b540c7ba0f48d94340d
)

# Fetch tflite-micro from GitHub (archive download, faster than git clone).
# When bumping tflite-micro, the dependencies below (flatbuffers, gemmlowp, ruy) must also
# be updated to match the versions pinned in tflite-micro's own third_party/ configuration.
# To update dependencies, check the tflite-micro repository at the pinned commit
# for the exact versions of flatbuffers, gemmlowp, and ruy.
FetchContent_Declare(
    tflite_micro
    URL https://github.com/tensorflow/tflite-micro/archive/2747abd5c82a95fb1624106a946fc671c31f16e8.tar.gz # January 22nd 2026
    URL_HASH SHA256=6ac0c8ef35f267cf3baf05dc094d4fd158864d4cc830d490b752994e58add8c8
)

# Header-only: flatbuffers for model parsing (version matching tflite-micro)
FetchContent_Declare(
    flatbuffers
    URL https://github.com/google/flatbuffers/archive/0100f6a5779831fa7a651e4b67ef389a8752bd9b.tar.gz # v23.5.26
    URL_HASH SHA256=85db3520acc4010b21984e2fb5ead3ec0d2c48df8009b614cb73562a82846554
)
set(FLATBUFFERS_BUILD_TESTS OFF CACHE BOOL "" FORCE)
set(FLATBUFFERS_INSTALL OFF CACHE BOOL "" FORCE)
set(FLATBUFFERS_BUILD_FLATC OFF CACHE BOOL "" FORCE)
set(FLATBUFFERS_BUILD_FLATHASH OFF CACHE BOOL "" FORCE)

# Header-only: gemmlowp for fixed-point math (commit matching tflite-micro)
FetchContent_Declare(
    gemmlowp
    URL https://github.com/google/gemmlowp/archive/fda83bdc38b118cc6b56753bd540caa49e570745.tar.gz
    URL_HASH SHA256=0f990732a0d541be514dfc5c1c45969626e9f349faa851baec01b9a9dcb4ae4b
)

# Header-only: ruy profiler instrumentation (commit matching tflite-micro)
FetchContent_Declare(
    ruy
    URL https://github.com/google/ruy/archive/54774a7a2cf85963777289193629d4bd42de4a59.tar.gz
    URL_HASH SHA256=91993e7eb2aa56e62e9f4abc1158b2b46333574aa81f1a7431b06df80a42b7fa
)

FetchContent_MakeAvailable(aegis_frontend_source tflite_micro flatbuffers gemmlowp ruy aegis_libfvad)
target_compile_options(fvad PRIVATE -fwrapv -fno-strict-aliasing)

# Only native DSP sources are used; no Python bridge/interpreter is included.
set(TFLITE_FRONTEND "${aegis_frontend_source_SOURCE_DIR}/tensorflow/lite/experimental/microfrontend/lib")

# Paths for TFLite Micro interpreter sources
set(TFLM "${tflite_micro_SOURCE_DIR}/tensorflow/lite/micro")
set(TFL "${tflite_micro_SOURCE_DIR}/tensorflow/lite")

# ------------------------------------------------------------------------------
# TFLite Micro Frontend static library (third-party code)
# ------------------------------------------------------------------------------
add_library(tflite_micro_frontend STATIC
    # Core FFT
    ${TFLITE_FRONTEND}/fft.cc
    ${TFLITE_FRONTEND}/fft_util.cc
    ${TFLITE_FRONTEND}/kiss_fft_int16.cc

    # Filterbank
    ${TFLITE_FRONTEND}/filterbank.cc
    ${TFLITE_FRONTEND}/filterbank_util.cc

    # Frontend
    ${TFLITE_FRONTEND}/frontend.cc
    ${TFLITE_FRONTEND}/frontend_util.cc

    # Log scale
    ${TFLITE_FRONTEND}/log_lut.cc
    ${TFLITE_FRONTEND}/log_scale.cc
    ${TFLITE_FRONTEND}/log_scale_util.cc

    # Noise reduction
    ${TFLITE_FRONTEND}/noise_reduction.cc
    ${TFLITE_FRONTEND}/noise_reduction_util.cc

    # PCAN gain control
    ${TFLITE_FRONTEND}/pcan_gain_control.cc
    ${TFLITE_FRONTEND}/pcan_gain_control_util.cc

    # Window
    ${TFLITE_FRONTEND}/window.cc
    ${TFLITE_FRONTEND}/window_util.cc
)

target_include_directories(tflite_micro_frontend PUBLIC
    # For tensorflow/lite/experimental/microfrontend/lib/*.h includes
    ${aegis_frontend_source_SOURCE_DIR}
    # For kiss_fft.h and tools/kiss_fftr.h includes
    ${aegis_frontend_source_SOURCE_DIR}/kissfft
)

# Fixed-point 16-bit arithmetic to match model training
target_compile_definitions(tflite_micro_frontend PUBLIC
    FIXED_POINT=16
)

# Suppress warnings from third-party code
target_compile_options(tflite_micro_frontend PRIVATE
    -fwrapv -fno-strict-aliasing # Conservative fixed-point overflow/alias semantics.
    -Wno-unused-parameter
    -Wno-sign-compare
    # Frontend PCAN/filter initialization still uses floating arithmetic.
    # Keep normal IEEE arithmetic for parity with pymicro-features.
)

# ------------------------------------------------------------------------------
# TFLite Micro Interpreter static library
# ------------------------------------------------------------------------------
add_library(tflite_micro_interpreter STATIC
    # Core
    ${TFLM}/micro_interpreter.cc
    ${TFLM}/micro_allocator.cc
    ${TFLM}/micro_context.cc
    ${TFLM}/micro_op_resolver.cc
    ${TFLM}/micro_utils.cc
    ${TFLM}/micro_log.cc
    ${TFLM}/flatbuffer_utils.cc
    ${TFLM}/memory_helpers.cc
    ${TFLM}/micro_allocation_info.cc
    ${TFLM}/micro_interpreter_context.cc
    ${TFLM}/micro_interpreter_graph.cc
    ${TFLM}/micro_resource_variable.cc
    ${TFLM}/micro_profiler.cc
    ${TFLM}/micro_time.cc
    ${TFLM}/system_setup.cc
    ${TFLM}/fake_micro_context.cc

    # Debug log (platform-specific)
    ${TFLM}/debug_log.cc

    # Arena allocators
    ${TFLM}/arena_allocator/single_arena_buffer_allocator.cc
    ${TFLM}/arena_allocator/non_persistent_arena_buffer_allocator.cc
    ${TFLM}/arena_allocator/persistent_arena_buffer_allocator.cc

    # Memory planner
    ${TFLM}/memory_planner/greedy_memory_planner.cc
    ${TFLM}/memory_planner/linear_memory_planner.cc
    ${TFLM}/memory_planner/non_persistent_buffer_planner_shim.cc

    # TFLite bridge
    ${TFLM}/tflite_bridge/micro_error_reporter.cc
    ${TFLM}/tflite_bridge/flatbuffer_conversions_bridge.cc

    # Error reporter base class (in compiler/mlir tree)
    ${tflite_micro_SOURCE_DIR}/tensorflow/compiler/mlir/lite/core/api/error_reporter.cc

    # TFLite core C
    ${TFL}/core/c/common.cc

    # TFLite core API
    ${TFL}/core/api/flatbuffer_conversions.cc
    ${TFL}/core/api/tensor_utils.cc

    # Kernel utilities
    ${TFL}/kernels/kernel_util.cc
    ${TFL}/kernels/internal/common.cc
    ${TFL}/kernels/internal/quantization_util.cc
    ${TFL}/kernels/internal/portable_tensor_utils.cc
    ${TFL}/kernels/internal/tensor_ctypes.cc
    ${TFL}/kernels/internal/runtime_shape.cc
    ${TFL}/kernels/internal/reference/portable_tensor_utils.cc

    # Schema (moved to compiler/mlir in recent tflite-micro)
    ${tflite_micro_SOURCE_DIR}/tensorflow/compiler/mlir/lite/schema/schema_utils.cc

    # Micro kernels (operators needed for wake word models)
    ${TFLM}/kernels/conv.cc
    ${TFLM}/kernels/conv_common.cc
    ${TFLM}/kernels/depthwise_conv.cc
    ${TFLM}/kernels/depthwise_conv_common.cc
    ${TFLM}/kernels/fully_connected.cc
    ${TFLM}/kernels/fully_connected_common.cc
    ${TFLM}/kernels/pooling.cc
    ${TFLM}/kernels/pooling_common.cc
    ${TFLM}/kernels/reshape.cc
    ${TFLM}/kernels/reshape_common.cc
    ${TFLM}/kernels/softmax.cc
    ${TFLM}/kernels/softmax_common.cc
    ${TFLM}/kernels/logistic.cc
    ${TFLM}/kernels/logistic_common.cc
    ${TFLM}/kernels/add.cc
    ${TFLM}/kernels/add_common.cc
    ${TFLM}/kernels/mul.cc
    ${TFLM}/kernels/mul_common.cc
    ${TFLM}/kernels/quantize.cc
    ${TFLM}/kernels/quantize_common.cc
    ${TFLM}/kernels/dequantize.cc
    ${TFLM}/kernels/dequantize_common.cc
    ${TFLM}/kernels/activations.cc
    ${TFLM}/kernels/activations_common.cc
    ${TFLM}/kernels/pad.cc
    ${TFLM}/kernels/pad_common.cc
    ${TFLM}/kernels/concatenation.cc
    ${TFLM}/kernels/strided_slice.cc
    ${TFLM}/kernels/strided_slice_common.cc
    ${TFLM}/kernels/pack.cc
    ${TFLM}/kernels/unpack.cc
    ${TFLM}/kernels/kernel_util.cc
    ${TFLM}/kernels/micro_tensor_utils.cc

    # Streaming model variable ops (needed for stateful wake word models)
    ${TFLM}/kernels/call_once.cc
    ${TFLM}/kernels/var_handle.cc
    ${TFLM}/kernels/read_variable.cc
    ${TFLM}/kernels/assign_variable.cc
    ${TFLM}/kernels/split_v.cc
    ${TFLM}/kernels/reduce.cc
    ${TFLM}/kernels/reduce_common.cc
)

target_include_directories(tflite_micro_interpreter PUBLIC
    ${tflite_micro_SOURCE_DIR}
    ${flatbuffers_SOURCE_DIR}/include
    ${gemmlowp_SOURCE_DIR}
    ${ruy_SOURCE_DIR}
)

target_compile_definitions(tflite_micro_interpreter PUBLIC
    TF_LITE_STATIC_MEMORY
    TF_LITE_DISABLE_X86_NEON
)

# Suppress warnings from third-party code
target_compile_options(tflite_micro_interpreter PRIVATE
    -fno-exceptions # TFLM static-memory placement allocators do not support unwinding
    -Wno-unused-parameter
    -Wno-sign-compare
    -Wno-missing-field-initializers
    -Wno-unused-variable
    -Wno-unused-function
    -ffast-math
)

# Aegis wrapper is also built on Linux for parity replay. No model is compiled in.
if(ANDROID)
  add_library(aegis_microwakeword SHARED AegisMicroWakeWord.cpp AegisMicroWakeWord_jni.cpp MicroFrontendWrapper.cpp AegisSpeechVad.cpp AegisSpeechVad_jni.cpp)
  target_link_libraries(aegis_microwakeword tflite_micro_frontend tflite_micro_interpreter fvad android log)
else()
  add_library(aegis_microwakeword SHARED AegisMicroWakeWord.cpp MicroFrontendWrapper.cpp AegisSpeechVad.cpp)
  target_link_libraries(aegis_microwakeword tflite_micro_frontend tflite_micro_interpreter fvad)
endif()

# Optional JVM/native endpointing verification on a host, never packaged in an APK.
option(AEGIS_BUILD_VAD_HOST_JNI "Expose VAD JNI in the host test library" OFF)
if(AEGIS_BUILD_VAD_HOST_JNI AND NOT ANDROID)
  find_path(AEGIS_JNI_INCLUDE jni.h HINTS "$ENV{JAVA_HOME}/include" REQUIRED)
  find_path(AEGIS_JNI_PLATFORM_INCLUDE jni_md.h HINTS "$ENV{JAVA_HOME}/include/linux" "$ENV{JAVA_HOME}/include/darwin" "$ENV{JAVA_HOME}/include/win32" REQUIRED)
  target_sources(aegis_microwakeword PRIVATE AegisSpeechVad_jni.cpp)
  target_include_directories(aegis_microwakeword PRIVATE "${AEGIS_JNI_INCLUDE}" "${AEGIS_JNI_PLATFORM_INCLUDE}")
endif()

# Explicit diagnostic build only; the normal APK contains neither harness nor data.
option(AEGIS_BUILD_REPLAY "Build private WAV replay command" OFF)
set(AEGIS_REPLAY_SOURCE "" CACHE FILEPATH "Path to scripts/hands-free/replay-native.cpp")
if(AEGIS_BUILD_REPLAY)
  add_executable(aegis_mww_replay "${AEGIS_REPLAY_SOURCE}")
  target_include_directories(aegis_mww_replay PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}")
  target_link_libraries(aegis_mww_replay aegis_microwakeword)
  get_filename_component(AEGIS_REPLAY_DIRECTORY "${AEGIS_REPLAY_SOURCE}" DIRECTORY)
  add_library(aegis_frontend_probe SHARED "${AEGIS_REPLAY_DIRECTORY}/frontend-probe.cpp" MicroFrontendWrapper.cpp)
  target_include_directories(aegis_frontend_probe PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}")
  target_link_libraries(aegis_frontend_probe tflite_micro_frontend)
  if(ANDROID)
    target_link_libraries(aegis_frontend_probe android log)
  endif()
endif()
