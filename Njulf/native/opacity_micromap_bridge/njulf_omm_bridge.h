#pragma once

#include <stddef.h>
#include <stdint.h>

#if defined(_WIN32)
#  if defined(NJULF_OMM_BRIDGE_BUILD)
#    define NJULF_OMM_API __declspec(dllexport)
#  else
#    define NJULF_OMM_API __declspec(dllimport)
#  endif
#else
#  define NJULF_OMM_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

enum { NJULF_OMM_BRIDGE_ABI = 1u };

typedef enum njulf_omm_status {
    NJULF_OMM_STATUS_SUCCESS = 0,
    NJULF_OMM_STATUS_INVALID_ARGUMENT = 1,
    NJULF_OMM_STATUS_CANCELLED = 2,
    NJULF_OMM_STATUS_WORKLOAD_TOO_LARGE = 3,
    NJULF_OMM_STATUS_SDK_FAILURE = 4,
    NJULF_OMM_STATUS_OUTPUT_INVALID = 5,
    NJULF_OMM_STATUS_OUT_OF_MEMORY = 6
} njulf_omm_status;

typedef struct njulf_omm_bridge_info {
    uint32_t struct_size;
    uint32_t bridge_abi;
    uint32_t sdk_version_major;
    uint32_t sdk_version_minor;
    uint32_t sdk_version_build;
    uint32_t reserved[3];
} njulf_omm_bridge_info;

typedef struct njulf_omm_bake_request {
    uint32_t struct_size;
    uint32_t bridge_abi;
    const float* alpha_fp32;
    uint64_t alpha_value_count;
    uint32_t texture_width;
    uint32_t texture_height;
    const float* uv32;
    uint64_t uv_float_count;
    uint32_t vertex_count;
    const uint32_t* indices;
    uint64_t index_count;
    uint32_t primitive_count;
    uint32_t subdivision_level;
    uint32_t address_mode; /* 0 wrap, 1 mirror, 2 clamp */
    uint32_t filter;       /* 0 nearest, 1 linear */
    float alpha_cutoff_inclusive;
    uint32_t maximum_array_data_bytes;
    uint64_t maximum_total_output_bytes;
    uint64_t maximum_workload_size;
    const volatile uint32_t* cancellation_flag;
} njulf_omm_bake_request;

typedef struct njulf_omm_usage {
    uint32_t count;
    uint16_t subdivision_level;
    uint16_t format;
} njulf_omm_usage;

typedef struct njulf_omm_result_view {
    uint32_t struct_size;
    uint32_t bridge_abi;
    const uint8_t* array_data;
    uint64_t array_data_bytes;
    const uint8_t* descriptor_data;
    uint64_t descriptor_data_bytes;
    uint32_t descriptor_count;
    const uint8_t* index_data;
    uint64_t index_data_bytes;
    uint32_t index_count;
    const njulf_omm_usage* descriptor_usage;
    uint32_t descriptor_usage_count;
    uint64_t opaque_count;
    uint64_t transparent_count;
    uint64_t unknown_opaque_count;
    uint64_t unknown_transparent_count;
    char detail[192];
} njulf_omm_result_view;

typedef void* njulf_omm_result_handle;

NJULF_OMM_API njulf_omm_status njulf_omm_get_bridge_info(
    njulf_omm_bridge_info* info);

NJULF_OMM_API njulf_omm_status njulf_omm_bake(
    const njulf_omm_bake_request* request,
    njulf_omm_result_handle* result);

NJULF_OMM_API njulf_omm_status njulf_omm_get_result_view(
    njulf_omm_result_handle result,
    njulf_omm_result_view* view);

NJULF_OMM_API void njulf_omm_destroy_result(njulf_omm_result_handle result);

#ifdef __cplusplus
}
#endif
