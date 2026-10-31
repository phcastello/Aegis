#include <jni.h>
#include "AegisMicroWakeWord.h"
extern "C" JNIEXPORT jlong JNICALL Java_com_aegis_node_MicroWakeWordNative_create(JNIEnv* env,jobject,jbyteArray model) {
    jsize length=env->GetArrayLength(model);
    auto* bytes=env->GetByteArrayElements(model,nullptr);
    auto* result=aegis_mww_create(reinterpret_cast<uint8_t*>(bytes),length);
    env->ReleaseByteArrayElements(model,bytes,JNI_ABORT);
    return reinterpret_cast<jlong>(result);
}
extern "C" JNIEXPORT void JNICALL Java_com_aegis_node_MicroWakeWordNative_destroy(JNIEnv*,jobject,jlong handle){aegis_mww_destroy(reinterpret_cast<void*>(handle));}
extern "C" JNIEXPORT jboolean JNICALL Java_com_aegis_node_MicroWakeWordNative_reset(JNIEnv*,jobject,jlong handle){return aegis_mww_reset(reinterpret_cast<void*>(handle));}
extern "C" JNIEXPORT jlong JNICALL Java_com_aegis_node_MicroWakeWordNative_process(JNIEnv* env,jobject,jlong handle,jshortArray audio,jint count) {
    if(count<0 || count>env->GetArrayLength(audio))return -2;
    auto* samples=env->GetShortArrayElements(audio,nullptr);
    auto result=aegis_mww_process(reinterpret_cast<void*>(handle),reinterpret_cast<int16_t*>(samples),count);
    env->ReleaseShortArrayElements(audio,samples,JNI_ABORT);
    return result;
}
