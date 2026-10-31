// Apache-2.0. Interpreter setup adapted from Home Assistant Android (see NOTICE).
#include "AegisMicroWakeWord.h"
#include "MicroFrontendWrapper.h"
#include <algorithm>
#include <array>
#include <cmath>
#include <cstring>
#include <memory>
#include <vector>
#include "flatbuffers/flatbuffers.h"
#include "tensorflow/lite/micro/micro_interpreter.h"
#include "tensorflow/lite/micro/micro_mutable_op_resolver.h"
#include "tensorflow/lite/micro/micro_resource_variable.h"
#include "tensorflow/lite/schema/schema_generated.h"

struct AegisDetector {
    std::vector<uint8_t> model;
    alignas(16) std::array<uint8_t, 128 * 1024> arena{};
    alignas(16) std::array<uint8_t, 32 * 1024> variables{};
    tflite::MicroMutableOpResolver<20> ops;
    std::unique_ptr<tflite::MicroInterpreter> interpreter;
    std::unique_ptr<MicroFrontendWrapper> frontend;
    std::array<int16_t,160> pending{};
    size_t pendingSize=0;
    int frame=0, scoreCount=0, scoreIndex=0;
    std::array<float,5> scores{};
    int64_t samplesSeen=0, lastEvent=-16000;
    bool latched=false;
    float lastScore=0, maxScore=0;
    AegisDetector(const uint8_t* data,size_t length):model(data,data+length) {}
    bool init() {
        flatbuffers::Verifier verifier(model.data(),model.size());
        if(!tflite::VerifyModelBuffer(verifier))return false;
        auto* parsed=tflite::GetModel(model.data());
        if(parsed->version()!=TFLITE_SCHEMA_VERSION)return false;
        if(ops.AddCallOnce()!=kTfLiteOk || ops.AddVarHandle()!=kTfLiteOk || ops.AddReshape()!=kTfLiteOk ||
           ops.AddReadVariable()!=kTfLiteOk || ops.AddStridedSlice()!=kTfLiteOk || ops.AddConcatenation()!=kTfLiteOk ||
           ops.AddAssignVariable()!=kTfLiteOk || ops.AddConv2D()!=kTfLiteOk || ops.AddMul()!=kTfLiteOk ||
           ops.AddAdd()!=kTfLiteOk || ops.AddMean()!=kTfLiteOk || ops.AddFullyConnected()!=kTfLiteOk ||
           ops.AddLogistic()!=kTfLiteOk || ops.AddQuantize()!=kTfLiteOk || ops.AddDepthwiseConv2D()!=kTfLiteOk ||
           ops.AddAveragePool2D()!=kTfLiteOk || ops.AddMaxPool2D()!=kTfLiteOk || ops.AddPad()!=kTfLiteOk ||
           ops.AddPack()!=kTfLiteOk || ops.AddSplitV()!=kTfLiteOk)return false;
        return restart();
    }
    bool restart() {
        interpreter.reset();frontend.reset();arena.fill(0);variables.fill(0);
        auto* allocator=tflite::MicroAllocator::Create(variables.data(),variables.size());
        if(!allocator)return false;
        auto* resources=tflite::MicroResourceVariables::Create(allocator,20);
        if(!resources)return false;
        interpreter=std::make_unique<tflite::MicroInterpreter>(tflite::GetModel(model.data()),ops,arena.data(),arena.size(),resources);
        if(interpreter->AllocateTensors()!=kTfLiteOk)return false;
        auto* in=interpreter->input(0);auto* out=interpreter->output(0);
        if(!in || !out || in->type!=kTfLiteInt8 || out->type!=kTfLiteUInt8 ||
           in->dims->size!=3 || in->dims->data[0]!=1 || in->dims->data[1]!=3 || in->dims->data[2]!=40 ||
           out->dims->size!=2 || out->dims->data[0]!=1 || out->dims->data[1]!=1 ||
           std::abs(in->params.scale-0.10196078568696976f)>1e-8f || in->params.zero_point!=-128 ||
           out->params.scale!=0.00390625f || out->params.zero_point!=0)return false;
        frontend=std::make_unique<MicroFrontendWrapper>(16000,10);
        pendingSize=0;frame=0;scoreCount=0;scoreIndex=0;scores.fill(0);samplesSeen=0;lastEvent=-16000;latched=false;lastScore=0;maxScore=0;
        return frontend->isInitialized();
    }
    int64_t process(const int16_t* data,size_t length) {
        int64_t event=-1;
        while(length) {
            size_t n=std::min(length,160-pendingSize);
            std::memcpy(pending.data()+pendingSize,data,n*sizeof(int16_t));pendingSize+=n;data+=n;length-=n;
            if(pendingSize<160)continue;
            samplesSeen+=160;pendingSize=0;
            std::array<float,40> feature{};
            if(frontend->processBlock(pending.data(),feature.data())) {
                auto* input=interpreter->input(0);
                for(size_t i=0;i<40;i++) {
                    // np.round(float32) in the accepted PoC uses ties-to-even.
                    float v=std::nearbyintf(feature[i]/input->params.scale+static_cast<float>(input->params.zero_point));
                    input->data.int8[frame*40+i]=static_cast<int8_t>(std::max(-128.f,std::min(127.f,v)));
                }
                if(++frame<3)continue;
                frame=0;
                if(interpreter->Invoke()!=kTfLiteOk)return -2;
                auto* output=interpreter->output(0);
                lastScore=(static_cast<int>(output->data.uint8[0])-output->params.zero_point)*output->params.scale;
                scores[scoreIndex]=lastScore;scoreIndex=(scoreIndex+1)%5;scoreCount=std::min(5,scoreCount+1);
                if(scoreCount<5)continue;
                float average=0;for(float score:scores)average+=score;average/=5;
                maxScore=std::max(maxScore,average);
                if(average<0.5f*0.6f)latched=false;
                if(average>=0.5f && !latched && samplesSeen-lastEvent>=16000) {
                    latched=true;lastEvent=samplesSeen;if(event<0)event=samplesSeen;
                }
            }
        }
        return event;
    }
};
extern "C" void* aegis_mww_create(const uint8_t* data,size_t length) {
    if(!data || length!=39552)return nullptr;
    auto detector=std::make_unique<AegisDetector>(data,length);
    if(!detector->init())return nullptr;
    return detector.release();
}
extern "C" void aegis_mww_destroy(void* handle){delete static_cast<AegisDetector*>(handle);}
extern "C" bool aegis_mww_reset(void* handle){return handle && static_cast<AegisDetector*>(handle)->restart();}
extern "C" int64_t aegis_mww_process(void* handle,const int16_t* data,size_t length){return handle?static_cast<AegisDetector*>(handle)->process(data,length):-2;}
extern "C" float aegis_mww_last_score(void* handle){return static_cast<AegisDetector*>(handle)->lastScore;}
extern "C" float aegis_mww_max_score(void* handle){return static_cast<AegisDetector*>(handle)->maxScore;}
