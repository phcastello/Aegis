#include <jni.h>
#include <cstdint>
#include <cstddef>
extern "C" void* aegis_vad_create();
extern "C" void aegis_vad_destroy(void*);
extern "C" bool aegis_vad_reset(void*);
extern "C" int aegis_vad_process(void*,const int16_t*,size_t);
extern "C" JNIEXPORT jlong JNICALL Java_com_aegis_node_HandsFreeSpeechVadNative_create(JNIEnv*,jobject){return reinterpret_cast<jlong>(aegis_vad_create());}
extern "C" JNIEXPORT void JNICALL Java_com_aegis_node_HandsFreeSpeechVadNative_destroy(JNIEnv*,jobject,jlong handle){aegis_vad_destroy(reinterpret_cast<void*>(handle));}
extern "C" JNIEXPORT jboolean JNICALL Java_com_aegis_node_HandsFreeSpeechVadNative_reset(JNIEnv*,jobject,jlong handle){return aegis_vad_reset(reinterpret_cast<void*>(handle));}
extern "C" JNIEXPORT jint JNICALL Java_com_aegis_node_HandsFreeSpeechVadNative_process(JNIEnv* env,jobject,jlong handle,jshortArray frame){
    if(!frame || env->GetArrayLength(frame)!=320)return -1;
    auto* samples=env->GetShortArrayElements(frame,nullptr);if(!samples)return -1;
    const int result=aegis_vad_process(reinterpret_cast<void*>(handle),reinterpret_cast<int16_t*>(samples),320);
    env->ReleaseShortArrayElements(frame,samples,JNI_ABORT);return result;
}
