#include "njulf_omm_bridge.h"

#include <omm.h>

#include <algorithm>
#include <cmath>
#include <cstring>
#include <limits>
#include <iterator>
#include <memory>
#include <new>
#include <string>
#include <vector>

namespace {

static_assert(sizeof(ommCpuOpacityMicromapDesc) == 8);
static_assert(sizeof(ommCpuOpacityMicromapUsageCount) == 8);
static_assert(sizeof(njulf_omm_usage) == 8);
#if INTPTR_MAX == INT64_MAX
static_assert(sizeof(njulf_omm_bridge_info) == 32);
static_assert(sizeof(njulf_omm_bake_request) == 120);
static_assert(sizeof(njulf_omm_result_view) == 312);
#endif

struct result_storage {
    std::vector<uint8_t> array_data;
    std::vector<uint8_t> descriptor_data;
    std::vector<uint8_t> index_data;
    std::vector<njulf_omm_usage> usage;
    ommDebugStats stats = ommDebugStatsDefault();
    std::string detail = "omm-cpu-bake-complete";
};

struct baker_guard {
    ommBaker value = nullptr;
    ~baker_guard() { if (value) (void)ommDestroyBaker(value); }
};

struct texture_guard {
    ommBaker baker = nullptr;
    ommCpuTexture value = nullptr;
    ~texture_guard() { if (value) (void)ommCpuDestroyTexture(baker, value); }
};

struct bake_guard {
    ommCpuBakeResult value = nullptr;
    ~bake_guard() { if (value) (void)ommCpuDestroyBakeResult(value); }
};

void message_callback(ommMessageSeverity, const char*, void*) {}

bool cancelled(const njulf_omm_bake_request& request) {
    return request.cancellation_flag && *request.cancellation_flag != 0u;
}

template <typename T>
bool checked_copy(std::vector<uint8_t>& destination, const T* source, uint64_t bytes) {
    if (!source || bytes == 0 || bytes > static_cast<uint64_t>(SIZE_MAX)) return false;
    destination.resize(static_cast<size_t>(bytes));
    std::memcpy(destination.data(), source, static_cast<size_t>(bytes));
    return true;
}

bool valid_request(const njulf_omm_bake_request& request) {
    if (request.struct_size != sizeof(njulf_omm_bake_request) ||
        request.bridge_abi != NJULF_OMM_BRIDGE_ABI ||
        !request.alpha_fp32 || !request.uv32 || !request.indices ||
        request.texture_width == 0 || request.texture_height == 0 ||
        request.vertex_count == 0 || request.primitive_count == 0 ||
        request.subdivision_level > 12 ||
        request.maximum_array_data_bytes == 0 ||
        request.maximum_total_output_bytes == 0 ||
        request.maximum_workload_size == 0 ||
        !std::isfinite(request.alpha_cutoff_inclusive) ||
        request.alpha_cutoff_inclusive < 0.0f ||
        request.alpha_cutoff_inclusive > 1.0f ||
        request.address_mode > 2 || request.filter > 1 ||
        request.texture_width > std::numeric_limits<uint32_t>::max() / sizeof(float))
        return false;

    const uint64_t pixels = static_cast<uint64_t>(request.texture_width) *
                            static_cast<uint64_t>(request.texture_height);
    const uint64_t uv_values = static_cast<uint64_t>(request.vertex_count) * 2u;
    const uint64_t indices = static_cast<uint64_t>(request.primitive_count) * 3u;
    return request.alpha_value_count == pixels &&
           request.uv_float_count == uv_values &&
           request.index_count == indices &&
           request.index_count <= std::numeric_limits<uint32_t>::max();
}

ommTextureAddressMode map_address(uint32_t value) {
    switch (value) {
        case 0: return ommTextureAddressMode_Wrap;
        case 1: return ommTextureAddressMode_Mirror;
        case 2: return ommTextureAddressMode_Clamp;
        default: return ommTextureAddressMode_MAX_NUM;
    }
}

ommTextureFilterMode map_filter(uint32_t value) {
    return value == 0 ? ommTextureFilterMode_Nearest : ommTextureFilterMode_Linear;
}

njulf_omm_status map_result(ommResult result) {
    switch (result) {
        case ommResult_SUCCESS: return NJULF_OMM_STATUS_SUCCESS;
        case ommResult_INVALID_ARGUMENT: return NJULF_OMM_STATUS_INVALID_ARGUMENT;
        case ommResult_WORKLOAD_TOO_BIG: return NJULF_OMM_STATUS_WORKLOAD_TOO_LARGE;
        default: return NJULF_OMM_STATUS_SDK_FAILURE;
    }
}

void set_detail(char (&target)[192], const std::string& value) {
    std::memset(target, 0, sizeof(target));
    const size_t count = std::min(value.size(), sizeof(target) - 1);
    std::memcpy(target, value.data(), count);
}

} // namespace

extern "C" NJULF_OMM_API njulf_omm_status njulf_omm_get_bridge_info(
    njulf_omm_bridge_info* info) {
    if (!info || info->struct_size != sizeof(njulf_omm_bridge_info))
        return NJULF_OMM_STATUS_INVALID_ARGUMENT;
    const ommLibraryDesc sdk = ommGetLibraryDesc();
    info->bridge_abi = NJULF_OMM_BRIDGE_ABI;
    info->sdk_version_major = sdk.versionMajor;
    info->sdk_version_minor = sdk.versionMinor;
    info->sdk_version_build = sdk.versionBuild;
    std::fill(std::begin(info->reserved), std::end(info->reserved), 0u);
    return NJULF_OMM_STATUS_SUCCESS;
}

extern "C" NJULF_OMM_API njulf_omm_status njulf_omm_bake(
    const njulf_omm_bake_request* request_ptr,
    njulf_omm_result_handle* result_ptr) {
    if (!request_ptr || !result_ptr || !valid_request(*request_ptr))
        return NJULF_OMM_STATUS_INVALID_ARGUMENT;
    *result_ptr = nullptr;
    const njulf_omm_bake_request& request = *request_ptr;
    if (cancelled(request)) return NJULF_OMM_STATUS_CANCELLED;

    try {
        baker_guard baker;
        ommBakerCreationDesc creation = ommBakerCreationDescDefault();
        creation.type = ommBakerType_CPU;
        creation.messageInterface.messageCallback = message_callback;
        ommResult sdk_result = ommCreateBaker(&creation, &baker.value);
        if (sdk_result != ommResult_SUCCESS) return map_result(sdk_result);

        const float sdk_cutoff = request.alpha_cutoff_inclusive == 0.0f
            ? 0.0f
            : std::nextafter(request.alpha_cutoff_inclusive,
                             -std::numeric_limits<float>::infinity());
        ommCpuTextureMipDesc mip = ommCpuTextureMipDescDefault();
        mip.width = request.texture_width;
        mip.height = request.texture_height;
        mip.rowPitch = request.texture_width * sizeof(float);
        mip.textureData = request.alpha_fp32;
        ommCpuTextureDesc texture_desc = ommCpuTextureDescDefault();
        texture_desc.format = ommCpuTextureFormat_FP32;
        texture_desc.mips = &mip;
        texture_desc.mipCount = 1;
        texture_desc.alphaCutoff = sdk_cutoff;
        texture_guard texture{baker.value};
        sdk_result = ommCpuCreateTexture(baker.value, &texture_desc, &texture.value);
        if (sdk_result != ommResult_SUCCESS) return map_result(sdk_result);

        ommCpuBakeInputDesc input = ommCpuBakeInputDescDefault();
        input.bakeFlags = static_cast<ommCpuBakeFlags>(
            static_cast<uint32_t>(ommCpuBakeFlags_EnableInternalThreads) |
            static_cast<uint32_t>(ommCpuBakeFlags_Force32BitIndices) |
            static_cast<uint32_t>(ommCpuBakeFlags_EnableValidation));
        input.texture = texture.value;
        input.runtimeSamplerDesc.addressingMode = map_address(request.address_mode);
        input.runtimeSamplerDesc.filter = map_filter(request.filter);
        input.runtimeSamplerDesc.borderAlpha = 0.0f;
        input.alphaMode = ommAlphaMode_Test;
        input.texCoordFormat = ommTexCoordFormat_UV32_FLOAT;
        input.texCoords = request.uv32;
        input.texCoordStrideInBytes = 2u * sizeof(float);
        input.indexFormat = ommIndexFormat_UINT_32;
        input.indexBuffer = request.indices;
        input.indexCount = static_cast<uint32_t>(request.index_count);
        input.dynamicSubdivisionScale = 0.0f;
        input.rejectionThreshold = 0.0f;
        input.alphaCutoff = sdk_cutoff;
        input.nearDuplicateDeduplicationFactor = 0.0f;
        input.alphaCutoffLessEqual = request.alpha_cutoff_inclusive == 0.0f
            ? ommOpacityState_Opaque
            : ommOpacityState_Transparent;
        input.alphaCutoffGreater = ommOpacityState_Opaque;
        input.format = ommFormat_OC1_4_State;
        input.formats = nullptr;
        input.unknownStatePromotion = ommUnknownStatePromotion_Nearest;
        input.unresolvedTriState = ommSpecialIndex_FullyUnknownOpaque;
        input.maxSubdivisionLevel = static_cast<uint8_t>(request.subdivision_level);
        input.maxArrayDataSize = request.maximum_array_data_bytes;
        input.subdivisionLevels = nullptr;
        input.maxWorkloadSize = request.maximum_workload_size;

        bake_guard bake;
        sdk_result = ommCpuBake(baker.value, &input, &bake.value);
        if (sdk_result != ommResult_SUCCESS) return map_result(sdk_result);
        if (cancelled(request)) return NJULF_OMM_STATUS_CANCELLED;

        const ommCpuBakeResultDesc* desc = nullptr;
        sdk_result = ommCpuGetBakeResultDesc(bake.value, &desc);
        if (sdk_result != ommResult_SUCCESS || !desc ||
            desc->indexFormat != ommIndexFormat_UINT_32 ||
            desc->indexCount != request.primitive_count ||
            desc->arrayDataSize == 0 ||
            desc->arrayDataSize > request.maximum_array_data_bytes ||
            desc->descArrayCount == 0 ||
            desc->descArrayHistogramCount == 0 || !desc->arrayData ||
            !desc->descArray || !desc->descArrayHistogram || !desc->indexBuffer)
            return NJULF_OMM_STATUS_OUTPUT_INVALID;

        const uint64_t descriptor_bytes =
            static_cast<uint64_t>(desc->descArrayCount) * sizeof(ommCpuOpacityMicromapDesc);
        const uint64_t index_bytes =
            static_cast<uint64_t>(desc->indexCount) * sizeof(uint32_t);
        const uint64_t usage_bytes =
            static_cast<uint64_t>(desc->descArrayHistogramCount) * sizeof(njulf_omm_usage);
        uint64_t total_output = desc->arrayDataSize;
        if (descriptor_bytes > std::numeric_limits<uint64_t>::max() - total_output)
            return NJULF_OMM_STATUS_OUTPUT_INVALID;
        total_output += descriptor_bytes;
        if (index_bytes > std::numeric_limits<uint64_t>::max() - total_output)
            return NJULF_OMM_STATUS_OUTPUT_INVALID;
        total_output += index_bytes;
        if (usage_bytes > std::numeric_limits<uint64_t>::max() - total_output)
            return NJULF_OMM_STATUS_OUTPUT_INVALID;
        total_output += usage_bytes;
        if (total_output > request.maximum_total_output_bytes)
            return NJULF_OMM_STATUS_OUTPUT_INVALID;

        auto result = std::make_unique<result_storage>();
        if (!checked_copy(result->array_data, static_cast<const uint8_t*>(desc->arrayData),
                          desc->arrayDataSize) ||
            !checked_copy(result->descriptor_data, desc->descArray,
                          descriptor_bytes) ||
            !checked_copy(result->index_data, static_cast<const uint8_t*>(desc->indexBuffer),
                          index_bytes))
            return NJULF_OMM_STATUS_OUTPUT_INVALID;

        result->usage.reserve(desc->descArrayHistogramCount);
        uint64_t descriptor_total = 0;
        for (uint32_t i = 0; i < desc->descArrayHistogramCount; ++i) {
            const auto& source = desc->descArrayHistogram[i];
            if (source.count == 0 || source.format != ommFormat_OC1_4_State)
                return NJULF_OMM_STATUS_OUTPUT_INVALID;
            if (source.count > std::numeric_limits<uint64_t>::max() - descriptor_total)
                return NJULF_OMM_STATUS_OUTPUT_INVALID;
            descriptor_total += source.count;
            result->usage.push_back({source.count, source.subdivisionLevel, source.format});
        }
        if (descriptor_total != desc->descArrayCount)
            return NJULF_OMM_STATUS_OUTPUT_INVALID;

        sdk_result = ommDebugGetStats2(baker.value, bake.value, &result->stats);
        if (sdk_result != ommResult_SUCCESS) return map_result(sdk_result);
        *result_ptr = result.release();
        return NJULF_OMM_STATUS_SUCCESS;
    } catch (const std::bad_alloc&) {
        return NJULF_OMM_STATUS_OUT_OF_MEMORY;
    } catch (...) {
        return NJULF_OMM_STATUS_SDK_FAILURE;
    }
}

extern "C" NJULF_OMM_API njulf_omm_status njulf_omm_get_result_view(
    njulf_omm_result_handle handle,
    njulf_omm_result_view* view) {
    if (!handle || !view || view->struct_size != sizeof(njulf_omm_result_view))
        return NJULF_OMM_STATUS_INVALID_ARGUMENT;
    const auto& result = *static_cast<const result_storage*>(handle);
    view->bridge_abi = NJULF_OMM_BRIDGE_ABI;
    view->array_data = result.array_data.data();
    view->array_data_bytes = result.array_data.size();
    view->descriptor_data = result.descriptor_data.data();
    view->descriptor_data_bytes = result.descriptor_data.size();
    view->descriptor_count = static_cast<uint32_t>(result.descriptor_data.size() /
                                                   sizeof(ommCpuOpacityMicromapDesc));
    view->index_data = result.index_data.data();
    view->index_data_bytes = result.index_data.size();
    view->index_count = static_cast<uint32_t>(result.index_data.size() / sizeof(uint32_t));
    view->descriptor_usage = result.usage.data();
    view->descriptor_usage_count = static_cast<uint32_t>(result.usage.size());
    view->opaque_count = result.stats.totalOpaque;
    view->transparent_count = result.stats.totalTransparent;
    view->unknown_opaque_count = result.stats.totalUnknownOpaque;
    view->unknown_transparent_count = result.stats.totalUnknownTransparent;
    set_detail(view->detail, result.detail);
    return NJULF_OMM_STATUS_SUCCESS;
}

extern "C" NJULF_OMM_API void njulf_omm_destroy_result(
    njulf_omm_result_handle handle) {
    delete static_cast<result_storage*>(handle);
}
