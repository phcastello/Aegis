// Optional private parity helper. Calls the unchanged production frontend.
#include "MicroFrontendWrapper.h"
extern "C" void* aegis_frontend_probe_create() {
    auto* f=new MicroFrontendWrapper(16000,10);
    if(!f->isInitialized()){delete f;return nullptr;}
    return f;
}
extern "C" int aegis_frontend_probe_process(void* h,const int16_t* pcm,float* features) {
    return static_cast<MicroFrontendWrapper*>(h)->processBlock(pcm,features)?1:0;
}
extern "C" void aegis_frontend_probe_destroy(void* h) { delete static_cast<MicroFrontendWrapper*>(h); }
