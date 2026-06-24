// AnimeStudio 的轻量 ACL 解码入口。
// 这里只做一件事：把 ACL compressed_tracks 在某个时间点解成平铺 float。
// Unity/Endfield 的 binding、Avatar、glTF 写出仍由 C# 层按确定性关系处理。

#include <acl/core/compressed_tracks.h>
#include <acl/core/track_writer.h>
#include <acl/decompression/decompress.h>

#include <rtm/quatf.h>
#include <rtm/vector4f.h>

#include <cstdlib>
#include <cstdint>
#include <cstring>

#if defined(_WIN32)
#include <malloc.h>
#endif

#if defined(_WIN32)
#define AS_EXPORT extern "C" __declspec(dllexport)
#else
#define AS_EXPORT extern "C"
#endif

struct AnimeStudioAclInfo
{
    int32_t result;
    uint32_t size;
    uint32_t version;
    uint32_t trackType;
    uint32_t numTracks;
    uint32_t numSamples;
    float sampleRate;
    float duration;
    uint32_t outputFloatCount;
};

struct AnimeStudioScalarSettings final : public acl::decompression_settings
{
    static constexpr bool is_track_type_supported(acl::track_type8 type)
    {
        return type != acl::track_type8::qvvf;
    }
};

struct AnimeStudioTransformWriter final : public acl::track_writer
{
    float* output;

    explicit AnimeStudioTransformWriter(float* output_)
        : output(output_)
    {
    }

    RTM_FORCE_INLINE void RTM_SIMD_CALL write_rotation(uint32_t trackIndex, rtm::quatf_arg0 rotation)
    {
        float* dst = output + trackIndex * 12;
        rtm::quat_store(rotation, dst + 0);
    }

    RTM_FORCE_INLINE void RTM_SIMD_CALL write_translation(uint32_t trackIndex, rtm::vector4f_arg0 translation)
    {
        float* dst = output + trackIndex * 12;
        rtm::vector_store(translation, dst + 4);
    }

    RTM_FORCE_INLINE void RTM_SIMD_CALL write_scale(uint32_t trackIndex, rtm::vector4f_arg0 scale)
    {
        float* dst = output + trackIndex * 12;
        rtm::vector_store(scale, dst + 8);
    }
};

struct AnimeStudioScalarWriter final : public acl::track_writer
{
    float* output;

    explicit AnimeStudioScalarWriter(float* output_)
        : output(output_)
    {
    }

    RTM_FORCE_INLINE void RTM_SIMD_CALL write_float1(uint32_t trackIndex, rtm::scalarf_arg0 value)
    {
        output[trackIndex] = rtm::scalar_cast(value);
    }
};

struct AnimeStudioAlignedBuffer final
{
    uint8_t* data = nullptr;
    uint32_t size = 0;

    AnimeStudioAlignedBuffer(const uint8_t* source, uint32_t sourceSize)
        : size(sourceSize)
    {
        if (source == nullptr || sourceSize == 0)
        {
            return;
        }

#if defined(_WIN32)
        data = static_cast<uint8_t*>(_aligned_malloc(sourceSize, 16));
#else
        data = static_cast<uint8_t*>(std::aligned_alloc(16, (sourceSize + 15U) & ~15U));
#endif
        if (data != nullptr)
        {
            std::memcpy(data, source, sourceSize);
        }
    }

    ~AnimeStudioAlignedBuffer()
    {
#if defined(_WIN32)
        _aligned_free(data);
#else
        std::free(data);
#endif
    }

    AnimeStudioAlignedBuffer(const AnimeStudioAlignedBuffer&) = delete;
    AnimeStudioAlignedBuffer& operator=(const AnimeStudioAlignedBuffer&) = delete;
};

static const acl::compressed_tracks* ReadTracks(const uint8_t* buffer, uint32_t bufferSize, int32_t* result)
{
    if (result != nullptr)
    {
        *result = 0;
    }

    if (buffer == nullptr || bufferSize < 32)
    {
        if (result != nullptr)
        {
            *result = -1;
        }

        return nullptr;
    }

    auto tracks = reinterpret_cast<const acl::compressed_tracks*>(buffer);
    const auto validation = tracks->is_valid(false);
    if (validation.any())
    {
        if (result != nullptr)
        {
            *result = -2;
        }

        return nullptr;
    }

    if (bufferSize < tracks->get_size())
    {
        if (result != nullptr)
        {
            *result = -3;
        }

        return nullptr;
    }

    return tracks;
}

AS_EXPORT AnimeStudioAclInfo AnimeStudioAclGetInfo(const uint8_t* buffer, uint32_t bufferSize)
{
    AnimeStudioAclInfo info = {};
    AnimeStudioAlignedBuffer aligned(buffer, bufferSize);
    if (aligned.data == nullptr)
    {
        info.result = -1;
        return info;
    }

    int32_t result = 0;
    const auto tracks = ReadTracks(aligned.data, aligned.size, &result);
    info.result = result;
    if (tracks == nullptr)
    {
        return info;
    }

    const auto trackType = tracks->get_track_type();
    info.size = tracks->get_size();
    info.version = static_cast<uint32_t>(tracks->get_version());
    info.trackType = static_cast<uint32_t>(trackType);
    info.numTracks = tracks->get_num_tracks();
    info.numSamples = tracks->get_num_samples_per_track();
    info.sampleRate = tracks->get_sample_rate();
    info.duration = tracks->get_duration();
    info.outputFloatCount = trackType == acl::track_type8::qvvf
        ? info.numTracks * 12
        : info.numTracks;

    return info;
}

AS_EXPORT int32_t AnimeStudioAclDecompressSample(
    const uint8_t* buffer,
    uint32_t bufferSize,
    float sampleTime,
    int32_t roundingPolicy,
    float* output,
    uint32_t outputFloatCount)
{
    if (output == nullptr)
    {
        return -1;
    }

    AnimeStudioAlignedBuffer aligned(buffer, bufferSize);
    if (aligned.data == nullptr)
    {
        return -1;
    }

    int32_t result = 0;
    const auto tracks = ReadTracks(aligned.data, aligned.size, &result);
    if (tracks == nullptr)
    {
        return result;
    }

    const auto policy = static_cast<acl::sample_rounding_policy>(roundingPolicy);
    const auto trackType = tracks->get_track_type();
    if (trackType == acl::track_type8::qvvf)
    {
        const uint32_t requiredFloats = tracks->get_num_tracks() * 12;
        if (outputFloatCount < requiredFloats)
        {
            return -4;
        }

        acl::decompression_context<acl::debug_transform_decompression_settings> context;
        if (!context.initialize(*tracks))
        {
            return -5;
        }

        context.seek(sampleTime, policy);
        AnimeStudioTransformWriter writer(output);
        context.decompress_tracks(writer);
        return 0;
    }

    const uint32_t requiredFloats = tracks->get_num_tracks();
    if (outputFloatCount < requiredFloats)
    {
        return -4;
    }

    acl::decompression_context<AnimeStudioScalarSettings> context;
    if (!context.initialize(*tracks))
    {
        return -5;
    }

    context.seek(sampleTime, policy);
    AnimeStudioScalarWriter writer(output);
    context.decompress_tracks(writer);
    return 0;
}
