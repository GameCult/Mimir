#pragma once

#include <stdbool.h>
#include <stdint.h>
#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

enum LocalcastSampleKind {
    LOCALCAST_SAMPLE_CAMERA_FRAME = 0,
    LOCALCAST_SAMPLE_CAMERA_FEATURE = 1,
    LOCALCAST_SAMPLE_SCENE_RAY = 2,
    LOCALCAST_SAMPLE_SURFACE_CLAIM = 3,
    LOCALCAST_SAMPLE_MATERIAL_CLAIM = 4,
    LOCALCAST_SAMPLE_AUDIO_BLOCK = 5,
    LOCALCAST_SAMPLE_PHASE_CLAIM = 6,
    LOCALCAST_SAMPLE_EVENT_CLAIM = 7,
    LOCALCAST_SAMPLE_RENDER_PACKET = 8
};

typedef struct LocalcastReservoir LocalcastReservoir;
typedef struct LocalcastRuntime LocalcastRuntime;
typedef struct LocalcastProducer LocalcastProducer;

typedef struct LocalcastSampleHandle {
    uint64_t sensor_id_hash;
    uint64_t timestamp_ns;
    uint64_t arrival_ns;
    uint64_t sequence;
    uint64_t payload_handle;
    uint32_t flags;
    uint32_t reserved;
} LocalcastSampleHandle;

enum LocalcastSampleFlags {
    LOCALCAST_SAMPLE_FLAG_DIAGNOSTIC = 1u << 0
};

enum LocalcastAudioSampleFormat {
    LOCALCAST_AUDIO_SAMPLE_FORMAT_F32_INTERLEAVED = 1
};

typedef struct LocalcastAudioBlockDescriptor {
    uint64_t data_handle;
    uint32_t frame_count;
    uint32_t channel_count;
    uint32_t sample_rate_hz;
    uint32_t sample_format;
    uint64_t start_sample;
    uint64_t channel_layout_hash;
} LocalcastAudioBlockDescriptor;

typedef struct LocalcastRenderPacketDescriptor {
    uint64_t point_buffer_handle;
    uint32_t point_count;
    uint32_t point_stride_bytes;
    uint32_t target_width;
    uint32_t target_height;
    uint64_t source_time_min_ns;
    uint64_t source_time_max_ns;
    uint64_t present_time_ns;
    uint64_t audio_alignment_time_ns;
    uint64_t metadata_handle;
} LocalcastRenderPacketDescriptor;

typedef struct LocalcastRenderPoint {
    uint64_t stable_key_hash;
    uint64_t source_timestamp_ns;
    float x;
    float y;
    float z;
    float radius_m;
    float red;
    float green;
    float blue;
    float alpha;
    float confidence;
} LocalcastRenderPoint;

typedef struct LocalcastRuntimeStatus {
    uint64_t edge_ns;
    uint64_t window_start_ns;
    size_t total_sample_count;
    size_t camera_frame_count;
    size_t camera_feature_count;
    size_t scene_ray_count;
    size_t surface_claim_count;
    size_t material_claim_count;
    size_t audio_block_count;
    size_t phase_claim_count;
    size_t event_claim_count;
    size_t render_packet_count;
} LocalcastRuntimeStatus;

LocalcastReservoir *localcast_reservoir_create(uint64_t duration_ns);
void localcast_reservoir_destroy(LocalcastReservoir *reservoir);

bool localcast_reservoir_push(
    LocalcastReservoir *reservoir,
    uint32_t sample_kind,
    LocalcastSampleHandle sample
);

bool localcast_reservoir_set_edge(LocalcastReservoir *reservoir, uint64_t edge_ns);
uint64_t localcast_reservoir_edge_ns(const LocalcastReservoir *reservoir);
uint64_t localcast_reservoir_window_start_ns(const LocalcastReservoir *reservoir);
size_t localcast_reservoir_len(const LocalcastReservoir *reservoir);
size_t localcast_reservoir_view_len(const LocalcastReservoir *reservoir, uint32_t sample_kind);

bool localcast_reservoir_sample_at(
    const LocalcastReservoir *reservoir,
    size_t index,
    uint32_t *out_sample_kind,
    LocalcastSampleHandle *out_sample
);

bool localcast_reservoir_view_sample_at(
    const LocalcastReservoir *reservoir,
    uint32_t sample_kind,
    size_t index,
    LocalcastSampleHandle *out_sample
);

bool localcast_reservoir_latest_for_sensor(
    const LocalcastReservoir *reservoir,
    uint32_t sample_kind,
    uint64_t sensor_id_hash,
    LocalcastSampleHandle *out_sample
);

LocalcastRuntime *localcast_runtime_create(uint64_t duration_ns);
void localcast_runtime_destroy(LocalcastRuntime *runtime);

bool localcast_runtime_push_camera_frame(LocalcastRuntime *runtime, LocalcastSampleHandle sample);
bool localcast_runtime_push_camera_feature(LocalcastRuntime *runtime, LocalcastSampleHandle sample);
bool localcast_runtime_push_scene_ray(LocalcastRuntime *runtime, LocalcastSampleHandle sample);
bool localcast_runtime_push_surface_claim(LocalcastRuntime *runtime, LocalcastSampleHandle sample);
bool localcast_runtime_push_material_claim(LocalcastRuntime *runtime, LocalcastSampleHandle sample);
bool localcast_runtime_push_audio_block(LocalcastRuntime *runtime, LocalcastSampleHandle sample);
bool localcast_runtime_push_phase_claim(LocalcastRuntime *runtime, LocalcastSampleHandle sample);
bool localcast_runtime_push_event_claim(LocalcastRuntime *runtime, LocalcastSampleHandle sample);
bool localcast_runtime_push_render_packet(LocalcastRuntime *runtime, LocalcastSampleHandle sample);
bool localcast_runtime_status(const LocalcastRuntime *runtime, LocalcastRuntimeStatus *out_status);
size_t localcast_runtime_len(const LocalcastRuntime *runtime);
size_t localcast_runtime_view_len(const LocalcastRuntime *runtime, uint32_t sample_kind);

bool localcast_runtime_sample_at(
    const LocalcastRuntime *runtime,
    size_t index,
    uint32_t *out_sample_kind,
    LocalcastSampleHandle *out_sample
);

bool localcast_runtime_view_sample_at(
    const LocalcastRuntime *runtime,
    uint32_t sample_kind,
    size_t index,
    LocalcastSampleHandle *out_sample
);

bool localcast_runtime_latest_for_sensor(
    const LocalcastRuntime *runtime,
    uint32_t sample_kind,
    uint64_t sensor_id_hash,
    LocalcastSampleHandle *out_sample
);

LocalcastProducer *localcast_producer_create(
    uint32_t sample_kind,
    uint64_t sensor_id_hash,
    uint64_t initial_sequence
);
void localcast_producer_destroy(LocalcastProducer *producer);
uint64_t localcast_producer_next_sequence(const LocalcastProducer *producer);

bool localcast_producer_push(
    LocalcastProducer *producer,
    LocalcastRuntime *runtime,
    uint64_t timestamp_ns,
    uint64_t arrival_ns,
    uint64_t payload_handle,
    LocalcastSampleHandle *out_sample
);

bool localcast_producer_push_audio_block(
    LocalcastProducer *producer,
    LocalcastRuntime *runtime,
    uint64_t timestamp_ns,
    uint64_t arrival_ns,
    const LocalcastAudioBlockDescriptor *descriptor,
    LocalcastSampleHandle *out_sample
);

bool localcast_producer_push_render_packet(
    LocalcastProducer *producer,
    LocalcastRuntime *runtime,
    uint64_t timestamp_ns,
    uint64_t arrival_ns,
    const LocalcastRenderPacketDescriptor *descriptor,
    LocalcastSampleHandle *out_sample
);

#ifdef __cplusplus
}
#endif
