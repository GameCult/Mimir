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

typedef struct LocalcastSampleHandle {
    uint64_t sensor_id_hash;
    uint64_t timestamp_ns;
    uint64_t arrival_ns;
    uint64_t sequence;
    uint64_t payload_handle;
} LocalcastSampleHandle;

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

#ifdef __cplusplus
}
#endif
