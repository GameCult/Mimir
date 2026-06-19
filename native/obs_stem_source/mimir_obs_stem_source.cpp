#include <obs-module.h>
#include <util/platform.h>

#include <atomic>
#include <chrono>
#include <cstring>
#include <string>
#include <thread>
#include <vector>

#include <windows.h>

#include "mimir_obs_stem_ipc.h"

OBS_DECLARE_MODULE()
OBS_MODULE_USE_DEFAULT_LOCALE("mimir-obs-stem-source", "en-US")

void mimir_register_program_texture_source();
void mimir_register_muninn_source();

struct MimirStemSource {
    obs_source_t *source = nullptr;
    std::string map_name = "Local\\MimirObsStemBus";
    std::string stem_id = "aligned_source_0";
    HANDLE mapping = nullptr;
    const uint8_t *view = nullptr;
    std::thread worker;
    std::atomic_bool running = false;
    uint32_t last_generation = 0;
};

static std::string obs_string(obs_data_t *settings, const char *key, const char *fallback)
{
    const char *value = obs_data_get_string(settings, key);
    return value && *value ? std::string(value) : std::string(fallback);
}

static void close_mapping(MimirStemSource *ctx)
{
    if (ctx->view) {
        UnmapViewOfFile(ctx->view);
        ctx->view = nullptr;
    }

    if (ctx->mapping) {
        CloseHandle(ctx->mapping);
        ctx->mapping = nullptr;
    }
}

static bool open_mapping(MimirStemSource *ctx)
{
    if (ctx->mapping) {
        return true;
    }

    ctx->mapping = OpenFileMappingA(FILE_MAP_READ, FALSE, ctx->map_name.c_str());
    if (!ctx->mapping) {
        return false;
    }

    ctx->view = static_cast<const uint8_t *>(MapViewOfFile(ctx->mapping, FILE_MAP_READ, 0, 0, MIMIR_OBS_STEM_MAP_BYTES));
    if (!ctx->view) {
        close_mapping(ctx);
        return false;
    }

    return true;
}

static const MimirObsStemRecord *find_record(const uint8_t *view, const std::string &stem_id, int stem_count)
{
    const auto count = std::min(stem_count, MIMIR_OBS_STEM_MAX_STEMS);
    for (int index = 0; index < count; ++index) {
        const auto *record = reinterpret_cast<const MimirObsStemRecord *>(view + MIMIR_OBS_STEM_HEADER_BYTES + (index * MIMIR_OBS_STEM_RECORD_BYTES));
        if (record->frame_count <= 0 || record->sample_rate <= 0 || record->sample_offset <= 0) {
            continue;
        }

        if (stem_id == record->stem_id) {
            return record;
        }
    }

    return nullptr;
}

static void publish_if_available(MimirStemSource *ctx)
{
    if (!open_mapping(ctx)) {
        return;
    }

    const auto *header = reinterpret_cast<const MimirObsStemHeader *>(ctx->view);
    const auto generation_a = header->generation;
    if ((generation_a & 1u) != 0 || generation_a == ctx->last_generation) {
        return;
    }

    if (header->magic != MIMIR_OBS_STEM_MAGIC ||
        header->version != MIMIR_OBS_STEM_VERSION ||
        header->map_bytes != MIMIR_OBS_STEM_MAP_BYTES) {
        return;
    }

    const auto *record = find_record(ctx->view, ctx->stem_id, header->stem_count);
    if (!record) {
        return;
    }

    const auto frame_count = std::min(record->frame_count, MIMIR_OBS_STEM_MAX_FRAMES);
    const auto sample_rate = record->sample_rate;
    const auto byte_count = static_cast<size_t>(frame_count) * sizeof(float);
    if (record->sample_offset < 0 || record->sample_offset + static_cast<int>(byte_count) > MIMIR_OBS_STEM_MAP_BYTES) {
        return;
    }

    std::vector<float> samples(frame_count);
    std::memcpy(samples.data(), ctx->view + record->sample_offset, byte_count);

    const auto generation_b = header->generation;
    if (generation_a != generation_b || (generation_b & 1u) != 0) {
        return;
    }

    ctx->last_generation = generation_b;
    uint8_t *planes[MAX_AV_PLANES] = {};
    planes[0] = reinterpret_cast<uint8_t *>(samples.data());
    obs_source_audio audio = {};
    audio.data[0] = planes[0];
    audio.frames = static_cast<uint32_t>(frame_count);
    audio.speakers = SPEAKERS_MONO;
    audio.samples_per_sec = static_cast<uint32_t>(sample_rate);
    audio.format = AUDIO_FORMAT_FLOAT_PLANAR;
    audio.timestamp = os_gettime_ns();
    obs_source_output_audio(ctx->source, &audio);
}

static void worker_loop(MimirStemSource *ctx)
{
    while (ctx->running.load()) {
        publish_if_available(ctx);
        std::this_thread::sleep_for(std::chrono::milliseconds(5));
    }
}

static const char *mimir_stem_get_name(void *)
{
    return obs_module_text("MimirAudioStem");
}

static void mimir_stem_update(void *data, obs_data_t *settings)
{
    auto *ctx = static_cast<MimirStemSource *>(data);
    ctx->map_name = obs_string(settings, "map_name", "Local\\MimirObsStemBus");
    ctx->stem_id = obs_string(settings, "stem_id", "aligned_source_0");
    ctx->last_generation = 0;
    close_mapping(ctx);
}

static void *mimir_stem_create(obs_data_t *settings, obs_source_t *source)
{
    auto *ctx = new MimirStemSource();
    ctx->source = source;
    mimir_stem_update(ctx, settings);
    ctx->running = true;
    ctx->worker = std::thread(worker_loop, ctx);
    return ctx;
}

static void mimir_stem_destroy(void *data)
{
    auto *ctx = static_cast<MimirStemSource *>(data);
    ctx->running = false;
    if (ctx->worker.joinable()) {
        ctx->worker.join();
    }

    close_mapping(ctx);
    delete ctx;
}

static void mimir_stem_defaults(obs_data_t *settings)
{
    obs_data_set_default_string(settings, "map_name", "Local\\MimirObsStemBus");
    obs_data_set_default_string(settings, "stem_id", "aligned_source_0");
}

static obs_properties_t *mimir_stem_properties(void *)
{
    obs_properties_t *props = obs_properties_create();
    obs_properties_add_text(props, "map_name", obs_module_text("MimirAudioStem.MapName"), OBS_TEXT_DEFAULT);
    obs_properties_add_text(props, "stem_id", obs_module_text("MimirAudioStem.StemId"), OBS_TEXT_DEFAULT);
    return props;
}

static obs_source_info mimir_stem_source_info = {
    .id = "mimir_audio_stem_source",
    .type = OBS_SOURCE_TYPE_INPUT,
    .output_flags = OBS_SOURCE_AUDIO,
    .get_name = mimir_stem_get_name,
    .create = mimir_stem_create,
    .destroy = mimir_stem_destroy,
    .get_defaults = mimir_stem_defaults,
    .get_properties = mimir_stem_properties,
    .update = mimir_stem_update,
};

bool obs_module_load(void)
{
    obs_register_source(&mimir_stem_source_info);
    mimir_register_program_texture_source();
    mimir_register_muninn_source();
    blog(
        LOG_INFO,
        "Mimir OBS sources loaded: mimir_audio_stem_source, mimir_program_texture_source, "
        "muninn_stream_source, muninn_video_source, muninn_audio_source");
    return true;
}
