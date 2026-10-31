// BSD-3-Clause WebRTC VAD adapter. Source/licensing details are in NOTICE and assets.
#include <cstddef>
#include <cstdint>
#include "fvad.h"

extern "C" void* aegis_vad_create() {
    Fvad* vad=fvad_new();
    if(vad && (fvad_set_sample_rate(vad,16000)!=0 || fvad_set_mode(vad,2)!=0)) {fvad_free(vad);return nullptr;}
    return vad;
}
extern "C" void aegis_vad_destroy(void* handle) {if(handle)fvad_free(static_cast<Fvad*>(handle));}
extern "C" bool aegis_vad_reset(void* handle) {
    if(!handle)return false;
    auto* vad=static_cast<Fvad*>(handle);fvad_reset(vad);
    return fvad_set_sample_rate(vad,16000)==0 && fvad_set_mode(vad,2)==0;
}
extern "C" int aegis_vad_process(void* handle,const int16_t* pcm,size_t count) {
    if(!handle || !pcm || count!=320)return -1;
    return fvad_process(static_cast<Fvad*>(handle),pcm,count);
}
