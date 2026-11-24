#include <jni.h>

#include <algorithm>
#include <array>
#include <cmath>
#include <memory>
#include <new>
#include <optional>
#include <utility>

#include "api/audio/echo_canceller3_config.h"
#include "modules/audio_processing/aec3/echo_canceller3.h"
#include "modules/audio_processing/include/audio_processing.h"

namespace {

constexpr int kFrameSamples = 160;
constexpr int kMaxSamples = 320;
using Processor = rtc::scoped_refptr<webrtc::AudioProcessing>;

// The linear filter output retains speech even when the residual suppressor
// is still adapting. Export both signals from one AEC3 instance; the caller
// can independently decide which speech is safe to forward.
class EchoControllerFactory final : public webrtc::EchoControlFactory {
public:
    std::unique_ptr<webrtc::EchoControl> Create(
            int rate, int render_channels, int capture_channels) override {
        webrtc::EchoCanceller3Config config;
        config.filter.export_linear_aec_output = true;
        return std::make_unique<webrtc::EchoCanceller3>(
                config, std::nullopt, rate, render_channels, capture_channels);
    }
};

bool valid_size(jsize count) {
    return count == kFrameSamples || count == kMaxSamples;
}

}  // namespace

extern "C" JNIEXPORT jlong JNICALL
Java_com_aegis_node_HandsFreeEchoNative_open(JNIEnv *, jobject) {
    try {
        auto processor = webrtc::AudioProcessingBuilder()
                .SetEchoControlFactory(std::make_unique<EchoControllerFactory>())
                .Create();
        if (!processor) return 0;
        webrtc::AudioProcessing::Config config;
        config.echo_canceller.enabled = true;
        config.echo_canceller.mobile_mode = false;
        config.echo_canceller.export_linear_aec_output = true;
        // Leave independent noise suppression and automatic gain adjustment
        // disabled; this stage is exclusively an acoustic echo processor.
        processor->ApplyConfig(config);
        return reinterpret_cast<jlong>(new (std::nothrow) Processor(std::move(processor)));
    } catch (...) {
        return 0;
    }
}

extern "C" JNIEXPORT void JNICALL
Java_com_aegis_node_HandsFreeEchoNative_destroy(JNIEnv *, jobject, jlong handle) {
    delete reinterpret_cast<Processor *>(handle);
}

extern "C" JNIEXPORT jboolean JNICALL
Java_com_aegis_node_HandsFreeEchoNative_reset(JNIEnv *, jobject, jlong handle) {
    if (!handle) return JNI_FALSE;
    try {
        auto &processor = *reinterpret_cast<Processor *>(handle);
        return processor->Initialize() == webrtc::AudioProcessing::kNoError;
    } catch (...) {
        return JNI_FALSE;
    }
}

extern "C" JNIEXPORT jboolean JNICALL
Java_com_aegis_node_HandsFreeEchoNative_process(
        JNIEnv *env, jobject, jlong handle, jshortArray render_array,
        jshortArray capture_array, jshortArray output_array, jint delay_ms,
        jshortArray linear_array) {
    if (!handle || !render_array || !capture_array || !output_array ||
        delay_ms < 0 || delay_ms > 500) return JNI_FALSE;
    const auto count = env->GetArrayLength(capture_array);
    if (!valid_size(count) || env->GetArrayLength(render_array) != count ||
        env->GetArrayLength(output_array) != count ||
        (linear_array && env->GetArrayLength(linear_array) != count)) return JNI_FALSE;
    if (env->IsSameObject(render_array, output_array) ||
        env->IsSameObject(capture_array, output_array) ||
        (linear_array && (env->IsSameObject(linear_array, render_array) ||
                         env->IsSameObject(linear_array, capture_array) ||
                         env->IsSameObject(linear_array, output_array)))) return JNI_FALSE;

    try {
        auto &processor = *reinterpret_cast<Processor *>(handle);
        const webrtc::StreamConfig format(16000, 1);
        std::array<jshort, kMaxSamples> render{};
        std::array<jshort, kMaxSamples> capture{};
        std::array<jshort, kMaxSamples> output{};
        std::array<jshort, kMaxSamples> linear{};
        std::array<jshort, kFrameSamples> render_output{};
        env->GetShortArrayRegion(render_array, 0, count, render.data());
        env->GetShortArrayRegion(capture_array, 0, count, capture.data());
        if (env->ExceptionCheck()) return JNI_FALSE;
        for (jsize offset = 0; offset < count; offset += kFrameSamples) {
            if (processor->set_stream_delay_ms(delay_ms) != webrtc::AudioProcessing::kNoError ||
                processor->ProcessReverseStream(render.data() + offset, format, format,
                                                render_output.data()) != webrtc::AudioProcessing::kNoError ||
                processor->ProcessStream(capture.data() + offset, format, format,
                                         output.data() + offset) != webrtc::AudioProcessing::kNoError) {
                return JNI_FALSE;
            }
            if (linear_array) {
                std::array<float, kFrameSamples> block;
                if (!processor->GetLinearAecOutput(
                        rtc::ArrayView<std::array<float, kFrameSamples>>(&block, 1))) {
                    return JNI_FALSE;
                }
                for (int i = 0; i < kFrameSamples; ++i) {
                    linear[offset + i] = static_cast<jshort>(
                            std::clamp(std::lround(block[i] * 32768.0f), -32768l, 32767l));
                }
            }
        }
        env->SetShortArrayRegion(output_array, 0, count, output.data());
        if (linear_array && !env->ExceptionCheck()) {
            env->SetShortArrayRegion(linear_array, 0, count, linear.data());
        }
        return env->ExceptionCheck() ? JNI_FALSE : JNI_TRUE;
    } catch (...) {
        return JNI_FALSE;
    }
}
