#pragma once
#include <cstddef>
#include <cstdint>
extern "C" {
void* aegis_mww_create(const uint8_t* data,size_t length);
void aegis_mww_destroy(void* handle);
bool aegis_mww_reset(void* handle);
int64_t aegis_mww_process(void* handle,const int16_t* data,size_t length);
float aegis_mww_last_score(void* handle);
float aegis_mww_max_score(void* handle);
// Scores at the first event returned by the most recent process call, or NaN.
// Observations only: querying these does not alter inference or event decisions.
float aegis_mww_event_score(void* handle);
float aegis_mww_event_average(void* handle);
}
