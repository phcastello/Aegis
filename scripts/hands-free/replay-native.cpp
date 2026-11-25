// Private replay harness for the exact C API used by Android JNI. No model embedded.
// Output CSV contains scores and sample offsets, never audio or transcripts.
#include "AegisMicroWakeWord.h"
#include <algorithm>
#include <array>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <vector>

static uint32_t u32(const uint8_t* p) { return uint32_t(p[0]) | uint32_t(p[1])<<8 | uint32_t(p[2])<<16 | uint32_t(p[3])<<24; }
static uint16_t u16(const uint8_t* p) { return uint16_t(p[0]) | uint16_t(p[1])<<8; }

int main(int argc,char** argv) {
    if(argc!=4) { std::fprintf(stderr,"Usage: aegis_mww_replay MODEL.tflite INPUT.wav OUTPUT.csv\n");return 2; }
    FILE* modelFile=std::fopen(argv[1],"rb");if(!modelFile)return 3;
    std::vector<uint8_t> model(39552);
    bool modelOk=std::fread(model.data(),1,model.size(),modelFile)==model.size() && std::fgetc(modelFile)==EOF;
    std::fclose(modelFile);if(!modelOk)return 4;
    void* detector=aegis_mww_create(model.data(),model.size());if(!detector)return 5;
    FILE* wav=std::fopen(argv[2],"rb");if(!wav){aegis_mww_destroy(detector);return 6;}
    std::array<uint8_t,12> riff{};
    bool valid=std::fread(riff.data(),1,12,wav)==12 && !std::memcmp(riff.data(),"RIFF",4) && !std::memcmp(riff.data()+8,"WAVE",4);
    bool formatOk=false;uint32_t dataSize=0;
    while(valid) {
        std::array<uint8_t,8> chunk{};
        if(std::fread(chunk.data(),1,8,wav)!=8){valid=false;break;}
        uint32_t size=u32(chunk.data()+4);
        if(!std::memcmp(chunk.data(),"fmt ",4)) {
            if(size<16 || size>4096){valid=false;break;}
            std::vector<uint8_t> fmt(size);
            if(std::fread(fmt.data(),1,size,wav)!=size){valid=false;break;}
            formatOk=u16(fmt.data())==1 && u16(fmt.data()+2)==1 && u32(fmt.data()+4)==16000 && u16(fmt.data()+14)==16;
            if(size&1)std::fseek(wav,1,SEEK_CUR);
        } else if(!std::memcmp(chunk.data(),"data",4)) {
            dataSize=size;break;
        } else {
            if(size>128*1024*1024 || std::fseek(wav,size+(size&1),SEEK_CUR)){valid=false;break;}
        }
    }
    if(!valid || !formatOk || !dataSize || dataSize>128*1024*1024 || (dataSize&1)){std::fclose(wav);aegis_mww_destroy(detector);return 7;}
    FILE* out=std::fopen(argv[3],"wx");if(!out){std::fclose(wav);aegis_mww_destroy(detector);return 8;}
    std::fprintf(out,"fed_samples,last_score,max_score,event_sample\n");
    std::array<int16_t,160> pcm{};uint64_t total=0;
    // EOF matches accepted runner: pad final partial block and add 0.5s silence.
    uint64_t inputSamples=dataSize/2, paddedSamples=((inputSamples+8000+159)/160)*160;
    int result=0;
    while(total<paddedSamples) {
        pcm.fill(0);
        size_t take=total<inputSamples?static_cast<size_t>(std::min<uint64_t>(160,inputSamples-total)):0;
        if(take && std::fread(pcm.data(),2,take,wav)!=take){result=9;break;}
        auto event=aegis_mww_process(detector,pcm.data(),pcm.size());
        if(event==-2){result=10;break;}
        total+=160;
        std::fprintf(out,"%llu,%.9g,%.9g,%lld\n",static_cast<unsigned long long>(total),aegis_mww_last_score(detector),aegis_mww_max_score(detector),static_cast<long long>(event));
    }
    if(std::fclose(out))result=11;
    std::fclose(wav);aegis_mww_destroy(detector);return result;
}
