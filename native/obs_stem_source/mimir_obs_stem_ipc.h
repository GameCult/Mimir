#pragma once

#include <cstdint>

constexpr uint64_t MIMIR_OBS_STEM_MAGIC = 0x4D545352494D494Dull;
constexpr int MIMIR_OBS_STEM_VERSION = 1;
constexpr int MIMIR_OBS_STEM_MAX_STEMS = 8;
constexpr int MIMIR_OBS_STEM_MAX_FRAMES = 4096;
constexpr int MIMIR_OBS_STEM_ID_BYTES = 64;
constexpr int MIMIR_OBS_STEM_DISPLAY_BYTES = 64;
constexpr int MIMIR_OBS_STEM_SOURCE_BYTES = 64;
constexpr int MIMIR_OBS_STEM_HEADER_BYTES = 64;
constexpr int MIMIR_OBS_STEM_RECORD_BYTES = 224;
constexpr int MIMIR_OBS_STEM_MAP_BYTES =
    MIMIR_OBS_STEM_HEADER_BYTES +
    (MIMIR_OBS_STEM_MAX_STEMS * MIMIR_OBS_STEM_RECORD_BYTES) +
    (MIMIR_OBS_STEM_MAX_STEMS * MIMIR_OBS_STEM_MAX_FRAMES * static_cast<int>(sizeof(float)));

struct MimirObsStemHeader {
    uint64_t magic;
    int32_t version;
    uint32_t generation;
    int32_t stem_count;
    int32_t max_stems;
    int32_t max_frames;
    int32_t header_bytes;
    int32_t record_bytes;
    int32_t map_bytes;
};

struct MimirObsStemRecord {
    int32_t channel_index;
    int32_t configured;
    int32_t sample_rate;
    int32_t frame_count;
    int32_t sample_offset;
    int32_t reserved0;
    int32_t reserved1;
    int32_t reserved2;
    char stem_id[MIMIR_OBS_STEM_ID_BYTES];
    char display_name[MIMIR_OBS_STEM_DISPLAY_BYTES];
    char source_id[MIMIR_OBS_STEM_SOURCE_BYTES];
};
