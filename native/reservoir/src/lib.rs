use std::collections::VecDeque;

pub const DEFAULT_RESERVOIR_NS: u64 = 5_000_000_000;
pub const LOCALCAST_SAMPLE_FLAG_DIAGNOSTIC: u32 = 1 << 0;

#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub enum SampleKind {
    CameraFrame,
    CameraFeature,
    SceneRay,
    SurfaceClaim,
    MaterialClaim,
    AudioBlock,
    PhaseClaim,
    EventClaim,
    RenderPacket,
}

impl SampleKind {
    fn from_u32(value: u32) -> Option<Self> {
        match value {
            0 => Some(Self::CameraFrame),
            1 => Some(Self::CameraFeature),
            2 => Some(Self::SceneRay),
            3 => Some(Self::SurfaceClaim),
            4 => Some(Self::MaterialClaim),
            5 => Some(Self::AudioBlock),
            6 => Some(Self::PhaseClaim),
            7 => Some(Self::EventClaim),
            8 => Some(Self::RenderPacket),
            _ => None,
        }
    }

    fn id(self) -> u32 {
        match self {
            Self::CameraFrame => 0,
            Self::CameraFeature => 1,
            Self::SceneRay => 2,
            Self::SurfaceClaim => 3,
            Self::MaterialClaim => 4,
            Self::AudioBlock => 5,
            Self::PhaseClaim => 6,
            Self::EventClaim => 7,
            Self::RenderPacket => 8,
        }
    }
}

#[derive(Debug, Clone, PartialEq)]
pub struct ReservoirSample<T> {
    pub kind: SampleKind,
    pub sensor_id: String,
    pub timestamp_ns: u64,
    pub arrival_ns: u64,
    pub sequence: u64,
    pub payload: T,
}

impl<T> ReservoirSample<T> {
    pub fn new(
        kind: SampleKind,
        sensor_id: impl Into<String>,
        timestamp_ns: u64,
        arrival_ns: u64,
        sequence: u64,
        payload: T,
    ) -> Self {
        Self {
            kind,
            sensor_id: sensor_id.into(),
            timestamp_ns,
            arrival_ns,
            sequence,
            payload,
        }
    }
}

#[derive(Debug, Clone)]
pub struct RollingReservoir<T> {
    duration_ns: u64,
    edge_ns: u64,
    samples: VecDeque<ReservoirSample<T>>,
}

impl<T> RollingReservoir<T> {
    pub fn new(duration_ns: u64) -> Self {
        Self {
            duration_ns: if duration_ns == 0 {
                DEFAULT_RESERVOIR_NS
            } else {
                duration_ns
            },
            edge_ns: 0,
            samples: VecDeque::new(),
        }
    }

    pub fn push(&mut self, sample: ReservoirSample<T>) {
        self.edge_ns = self.edge_ns.max(sample.timestamp_ns);
        self.samples.push_back(sample);
        self.evict_expired();
    }

    pub fn set_edge(&mut self, edge_ns: u64) {
        self.edge_ns = self.edge_ns.max(edge_ns);
        self.evict_expired();
    }

    pub fn edge_ns(&self) -> u64 {
        self.edge_ns
    }

    pub fn window_start_ns(&self) -> u64 {
        self.edge_ns.saturating_sub(self.duration_ns)
    }

    pub fn len(&self) -> usize {
        self.samples.len()
    }

    pub fn is_empty(&self) -> bool {
        self.samples.is_empty()
    }

    pub fn iter(&self) -> impl Iterator<Item = &ReservoirSample<T>> {
        self.samples.iter()
    }

    pub fn sample_at(&self, index: usize) -> Option<&ReservoirSample<T>> {
        self.samples.get(index)
    }

    pub fn view(&self, kind: SampleKind) -> impl Iterator<Item = &ReservoirSample<T>> {
        self.samples
            .iter()
            .filter(move |sample| sample.kind == kind)
    }

    pub fn view_len(&self, kind: SampleKind) -> usize {
        self.view(kind).count()
    }

    pub fn view_sample_at(&self, kind: SampleKind, index: usize) -> Option<&ReservoirSample<T>> {
        self.view(kind).nth(index)
    }

    pub fn latest_for_sensor(
        &self,
        kind: SampleKind,
        sensor_id: &str,
    ) -> Option<&ReservoirSample<T>> {
        self.samples
            .iter()
            .rev()
            .find(|sample| sample.kind == kind && sample.sensor_id == sensor_id)
    }

    fn evict_expired(&mut self) {
        let start_ns = self.window_start_ns();
        self.samples.retain(|sample| {
            sample.timestamp_ns >= start_ns && sample.timestamp_ns <= self.edge_ns
        });
    }
}

impl<T> Default for RollingReservoir<T> {
    fn default() -> Self {
        Self::new(DEFAULT_RESERVOIR_NS)
    }
}

#[repr(C)]
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct LocalcastSampleHandle {
    pub sensor_id_hash: u64,
    pub timestamp_ns: u64,
    pub arrival_ns: u64,
    pub sequence: u64,
    pub payload_handle: u64,
    pub flags: u32,
    pub reserved: u32,
}

pub struct LocalcastReservoir {
    inner: RollingReservoir<LocalcastSampleHandle>,
}

pub struct LocalcastRuntime {
    reservoir: RollingReservoir<LocalcastSampleHandle>,
}

#[repr(C)]
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct LocalcastRuntimeStatus {
    pub edge_ns: u64,
    pub window_start_ns: u64,
    pub total_sample_count: usize,
    pub camera_frame_count: usize,
    pub camera_feature_count: usize,
    pub scene_ray_count: usize,
    pub surface_claim_count: usize,
    pub material_claim_count: usize,
    pub audio_block_count: usize,
    pub phase_claim_count: usize,
    pub event_claim_count: usize,
    pub render_packet_count: usize,
}

impl Default for LocalcastRuntimeStatus {
    fn default() -> Self {
        Self {
            edge_ns: 0,
            window_start_ns: 0,
            total_sample_count: 0,
            camera_frame_count: 0,
            camera_feature_count: 0,
            scene_ray_count: 0,
            surface_claim_count: 0,
            material_claim_count: 0,
            audio_block_count: 0,
            phase_claim_count: 0,
            event_claim_count: 0,
            render_packet_count: 0,
        }
    }
}

impl LocalcastRuntime {
    fn new(duration_ns: u64) -> Self {
        Self {
            reservoir: RollingReservoir::new(duration_ns),
        }
    }

    fn push(&mut self, kind: SampleKind, sample: LocalcastSampleHandle) {
        self.reservoir.push(sample_from_handle(kind, sample));
    }

    fn view_len(&self, kind: SampleKind) -> usize {
        self.reservoir.view_len(kind)
    }

    fn status(&self) -> LocalcastRuntimeStatus {
        LocalcastRuntimeStatus {
            edge_ns: self.reservoir.edge_ns(),
            window_start_ns: self.reservoir.window_start_ns(),
            total_sample_count: self.reservoir.len(),
            camera_frame_count: self.view_len(SampleKind::CameraFrame),
            camera_feature_count: self.view_len(SampleKind::CameraFeature),
            scene_ray_count: self.view_len(SampleKind::SceneRay),
            surface_claim_count: self.view_len(SampleKind::SurfaceClaim),
            material_claim_count: self.view_len(SampleKind::MaterialClaim),
            audio_block_count: self.view_len(SampleKind::AudioBlock),
            phase_claim_count: self.view_len(SampleKind::PhaseClaim),
            event_claim_count: self.view_len(SampleKind::EventClaim),
            render_packet_count: self.view_len(SampleKind::RenderPacket),
        }
    }
}

fn sample_from_handle(
    kind: SampleKind,
    sample: LocalcastSampleHandle,
) -> ReservoirSample<LocalcastSampleHandle> {
    ReservoirSample::new(
        kind,
        sample.sensor_id_hash.to_string(),
        sample.timestamp_ns,
        sample.arrival_ns,
        sample.sequence,
        sample,
    )
}

fn is_live_sample(sample: LocalcastSampleHandle) -> bool {
    sample.flags == 0
}

#[unsafe(no_mangle)]
pub extern "C" fn localcast_reservoir_create(duration_ns: u64) -> *mut LocalcastReservoir {
    Box::into_raw(Box::new(LocalcastReservoir {
        inner: RollingReservoir::new(duration_ns),
    }))
}

#[unsafe(no_mangle)]
pub extern "C" fn localcast_reservoir_destroy(ptr: *mut LocalcastReservoir) {
    if ptr.is_null() {
        return;
    }
    unsafe {
        drop(Box::from_raw(ptr));
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn localcast_reservoir_push(
    ptr: *mut LocalcastReservoir,
    sample_kind: u32,
    sample: LocalcastSampleHandle,
) -> bool {
    let Some(reservoir) = (unsafe { ptr.as_mut() }) else {
        return false;
    };
    let Some(kind) = SampleKind::from_u32(sample_kind) else {
        return false;
    };
    if !is_live_sample(sample) {
        return false;
    }
    reservoir.inner.push(sample_from_handle(kind, sample));
    true
}

#[unsafe(no_mangle)]
pub extern "C" fn localcast_reservoir_set_edge(ptr: *mut LocalcastReservoir, edge_ns: u64) -> bool {
    let Some(reservoir) = (unsafe { ptr.as_mut() }) else {
        return false;
    };
    reservoir.inner.set_edge(edge_ns);
    true
}

#[unsafe(no_mangle)]
pub extern "C" fn localcast_reservoir_edge_ns(ptr: *const LocalcastReservoir) -> u64 {
    let Some(reservoir) = (unsafe { ptr.as_ref() }) else {
        return 0;
    };
    reservoir.inner.edge_ns()
}

#[unsafe(no_mangle)]
pub extern "C" fn localcast_reservoir_window_start_ns(ptr: *const LocalcastReservoir) -> u64 {
    let Some(reservoir) = (unsafe { ptr.as_ref() }) else {
        return 0;
    };
    reservoir.inner.window_start_ns()
}

#[unsafe(no_mangle)]
pub extern "C" fn localcast_reservoir_len(ptr: *const LocalcastReservoir) -> usize {
    let Some(reservoir) = (unsafe { ptr.as_ref() }) else {
        return 0;
    };
    reservoir.inner.len()
}

#[unsafe(no_mangle)]
pub extern "C" fn localcast_reservoir_view_len(
    ptr: *const LocalcastReservoir,
    sample_kind: u32,
) -> usize {
    let Some(reservoir) = (unsafe { ptr.as_ref() }) else {
        return 0;
    };
    let Some(kind) = SampleKind::from_u32(sample_kind) else {
        return 0;
    };
    reservoir.inner.view_len(kind)
}

#[unsafe(no_mangle)]
pub extern "C" fn localcast_reservoir_sample_at(
    ptr: *const LocalcastReservoir,
    index: usize,
    out_sample_kind: *mut u32,
    out_sample: *mut LocalcastSampleHandle,
) -> bool {
    let Some(reservoir) = (unsafe { ptr.as_ref() }) else {
        return false;
    };
    let Some(out_kind) = (unsafe { out_sample_kind.as_mut() }) else {
        return false;
    };
    let Some(out) = (unsafe { out_sample.as_mut() }) else {
        return false;
    };
    let Some(sample) = reservoir.inner.sample_at(index) else {
        return false;
    };
    *out_kind = sample.kind as u32;
    *out = sample.payload;
    true
}

#[unsafe(no_mangle)]
pub extern "C" fn localcast_reservoir_view_sample_at(
    ptr: *const LocalcastReservoir,
    sample_kind: u32,
    index: usize,
    out_sample: *mut LocalcastSampleHandle,
) -> bool {
    let Some(reservoir) = (unsafe { ptr.as_ref() }) else {
        return false;
    };
    let Some(kind) = SampleKind::from_u32(sample_kind) else {
        return false;
    };
    let Some(out) = (unsafe { out_sample.as_mut() }) else {
        return false;
    };
    let Some(sample) = reservoir.inner.view_sample_at(kind, index) else {
        return false;
    };
    *out = sample.payload;
    true
}

#[unsafe(no_mangle)]
pub extern "C" fn localcast_reservoir_latest_for_sensor(
    ptr: *const LocalcastReservoir,
    sample_kind: u32,
    sensor_id_hash: u64,
    out_sample: *mut LocalcastSampleHandle,
) -> bool {
    let Some(reservoir) = (unsafe { ptr.as_ref() }) else {
        return false;
    };
    let Some(kind) = SampleKind::from_u32(sample_kind) else {
        return false;
    };
    let Some(out) = (unsafe { out_sample.as_mut() }) else {
        return false;
    };
    let key = sensor_id_hash.to_string();
    let Some(sample) = reservoir.inner.latest_for_sensor(kind, &key) else {
        return false;
    };
    *out = sample.payload;
    true
}

#[unsafe(no_mangle)]
pub extern "C" fn localcast_runtime_create(duration_ns: u64) -> *mut LocalcastRuntime {
    Box::into_raw(Box::new(LocalcastRuntime::new(duration_ns)))
}

#[unsafe(no_mangle)]
pub extern "C" fn localcast_runtime_destroy(ptr: *mut LocalcastRuntime) {
    if ptr.is_null() {
        return;
    }
    unsafe {
        drop(Box::from_raw(ptr));
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn localcast_runtime_push_camera_frame(
    ptr: *mut LocalcastRuntime,
    sample: LocalcastSampleHandle,
) -> bool {
    localcast_runtime_push_typed(ptr, SampleKind::CameraFrame, sample)
}

#[unsafe(no_mangle)]
pub extern "C" fn localcast_runtime_push_camera_feature(
    ptr: *mut LocalcastRuntime,
    sample: LocalcastSampleHandle,
) -> bool {
    localcast_runtime_push_typed(ptr, SampleKind::CameraFeature, sample)
}

#[unsafe(no_mangle)]
pub extern "C" fn localcast_runtime_push_scene_ray(
    ptr: *mut LocalcastRuntime,
    sample: LocalcastSampleHandle,
) -> bool {
    localcast_runtime_push_typed(ptr, SampleKind::SceneRay, sample)
}

#[unsafe(no_mangle)]
pub extern "C" fn localcast_runtime_push_surface_claim(
    ptr: *mut LocalcastRuntime,
    sample: LocalcastSampleHandle,
) -> bool {
    localcast_runtime_push_typed(ptr, SampleKind::SurfaceClaim, sample)
}

#[unsafe(no_mangle)]
pub extern "C" fn localcast_runtime_push_material_claim(
    ptr: *mut LocalcastRuntime,
    sample: LocalcastSampleHandle,
) -> bool {
    localcast_runtime_push_typed(ptr, SampleKind::MaterialClaim, sample)
}

#[unsafe(no_mangle)]
pub extern "C" fn localcast_runtime_push_audio_block(
    ptr: *mut LocalcastRuntime,
    sample: LocalcastSampleHandle,
) -> bool {
    localcast_runtime_push_typed(ptr, SampleKind::AudioBlock, sample)
}

#[unsafe(no_mangle)]
pub extern "C" fn localcast_runtime_push_phase_claim(
    ptr: *mut LocalcastRuntime,
    sample: LocalcastSampleHandle,
) -> bool {
    localcast_runtime_push_typed(ptr, SampleKind::PhaseClaim, sample)
}

#[unsafe(no_mangle)]
pub extern "C" fn localcast_runtime_push_event_claim(
    ptr: *mut LocalcastRuntime,
    sample: LocalcastSampleHandle,
) -> bool {
    localcast_runtime_push_typed(ptr, SampleKind::EventClaim, sample)
}

#[unsafe(no_mangle)]
pub extern "C" fn localcast_runtime_push_render_packet(
    ptr: *mut LocalcastRuntime,
    sample: LocalcastSampleHandle,
) -> bool {
    localcast_runtime_push_typed(ptr, SampleKind::RenderPacket, sample)
}

#[unsafe(no_mangle)]
pub extern "C" fn localcast_runtime_status(
    ptr: *const LocalcastRuntime,
    out_status: *mut LocalcastRuntimeStatus,
) -> bool {
    let Some(runtime) = (unsafe { ptr.as_ref() }) else {
        return false;
    };
    let Some(out) = (unsafe { out_status.as_mut() }) else {
        return false;
    };
    *out = runtime.status();
    true
}

#[unsafe(no_mangle)]
pub extern "C" fn localcast_runtime_len(ptr: *const LocalcastRuntime) -> usize {
    let Some(runtime) = (unsafe { ptr.as_ref() }) else {
        return 0;
    };
    runtime.reservoir.len()
}

#[unsafe(no_mangle)]
pub extern "C" fn localcast_runtime_view_len(
    ptr: *const LocalcastRuntime,
    sample_kind: u32,
) -> usize {
    let Some(runtime) = (unsafe { ptr.as_ref() }) else {
        return 0;
    };
    let Some(kind) = SampleKind::from_u32(sample_kind) else {
        return 0;
    };
    runtime.reservoir.view_len(kind)
}

#[unsafe(no_mangle)]
pub extern "C" fn localcast_runtime_sample_at(
    ptr: *const LocalcastRuntime,
    index: usize,
    out_sample_kind: *mut u32,
    out_sample: *mut LocalcastSampleHandle,
) -> bool {
    let Some(runtime) = (unsafe { ptr.as_ref() }) else {
        return false;
    };
    let Some(kind_out) = (unsafe { out_sample_kind.as_mut() }) else {
        return false;
    };
    let Some(sample_out) = (unsafe { out_sample.as_mut() }) else {
        return false;
    };
    let Some(sample) = runtime.reservoir.sample_at(index) else {
        return false;
    };
    *kind_out = sample.kind.id();
    *sample_out = sample.payload;
    true
}

#[unsafe(no_mangle)]
pub extern "C" fn localcast_runtime_view_sample_at(
    ptr: *const LocalcastRuntime,
    sample_kind: u32,
    index: usize,
    out_sample: *mut LocalcastSampleHandle,
) -> bool {
    let Some(runtime) = (unsafe { ptr.as_ref() }) else {
        return false;
    };
    let Some(kind) = SampleKind::from_u32(sample_kind) else {
        return false;
    };
    let Some(sample_out) = (unsafe { out_sample.as_mut() }) else {
        return false;
    };
    let Some(sample) = runtime.reservoir.view_sample_at(kind, index) else {
        return false;
    };
    *sample_out = sample.payload;
    true
}

#[unsafe(no_mangle)]
pub extern "C" fn localcast_runtime_latest_for_sensor(
    ptr: *const LocalcastRuntime,
    sample_kind: u32,
    sensor_id_hash: u64,
    out_sample: *mut LocalcastSampleHandle,
) -> bool {
    let Some(runtime) = (unsafe { ptr.as_ref() }) else {
        return false;
    };
    let Some(kind) = SampleKind::from_u32(sample_kind) else {
        return false;
    };
    let Some(sample_out) = (unsafe { out_sample.as_mut() }) else {
        return false;
    };
    let sensor_id = sensor_id_hash.to_string();
    let Some(sample) = runtime.reservoir.latest_for_sensor(kind, &sensor_id) else {
        return false;
    };
    *sample_out = sample.payload;
    true
}

fn localcast_runtime_push_typed(
    ptr: *mut LocalcastRuntime,
    kind: SampleKind,
    sample: LocalcastSampleHandle,
) -> bool {
    let Some(runtime) = (unsafe { ptr.as_mut() }) else {
        return false;
    };
    if !is_live_sample(sample) {
        return false;
    }
    runtime.push(kind, sample);
    true
}

#[cfg(test)]
mod tests {
    use super::*;

    fn handle(
        sensor_id_hash: u64,
        timestamp_ns: u64,
        arrival_ns: u64,
        sequence: u64,
        payload_handle: u64,
    ) -> LocalcastSampleHandle {
        LocalcastSampleHandle {
            sensor_id_hash,
            timestamp_ns,
            arrival_ns,
            sequence,
            payload_handle,
            flags: 0,
            reserved: 0,
        }
    }

    fn diagnostic_handle(
        sensor_id_hash: u64,
        timestamp_ns: u64,
        arrival_ns: u64,
        sequence: u64,
        payload_handle: u64,
    ) -> LocalcastSampleHandle {
        LocalcastSampleHandle {
            flags: LOCALCAST_SAMPLE_FLAG_DIAGNOSTIC,
            ..handle(
                sensor_id_hash,
                timestamp_ns,
                arrival_ns,
                sequence,
                payload_handle,
            )
        }
    }

    #[test]
    fn rolling_reservoir_expires_every_kind_from_one_edge() {
        let mut reservoir = RollingReservoir::new(DEFAULT_RESERVOIR_NS);
        reservoir.push(ReservoirSample::new(
            SampleKind::CameraFrame,
            "cam-a",
            1_000_000_000,
            1_000_000_010,
            0,
            "old-camera",
        ));
        reservoir.push(ReservoirSample::new(
            SampleKind::AudioBlock,
            "audio",
            7_000_000_001,
            7_000_000_020,
            0,
            "audio-edge",
        ));

        assert_eq!(1, reservoir.len());
        assert_eq!(0, reservoir.view_len(SampleKind::CameraFrame));
        assert_eq!(1, reservoir.view_len(SampleKind::AudioBlock));
        assert_eq!(2_000_000_001, reservoir.window_start_ns());
    }

    #[test]
    fn typed_views_share_storage_without_owning_retention() {
        let mut reservoir = RollingReservoir::new(DEFAULT_RESERVOIR_NS);
        reservoir.push(ReservoirSample::new(
            SampleKind::CameraFrame,
            "cam-a",
            10,
            11,
            0,
            "a0",
        ));
        reservoir.push(ReservoirSample::new(
            SampleKind::AudioBlock,
            "mic",
            12,
            13,
            0,
            "m0",
        ));
        reservoir.push(ReservoirSample::new(
            SampleKind::CameraFrame,
            "cam-b",
            14,
            15,
            0,
            "b0",
        ));

        let all = reservoir
            .iter()
            .map(|sample| sample.payload)
            .collect::<Vec<_>>();
        let camera = reservoir
            .view(SampleKind::CameraFrame)
            .map(|sample| sample.payload)
            .collect::<Vec<_>>();

        assert_eq!(vec!["a0", "m0", "b0"], all);
        assert_eq!(vec!["a0", "b0"], camera);
        assert_eq!(3, reservoir.len());
    }

    #[test]
    fn indexed_reads_expose_total_buffer_and_typed_views() {
        let mut reservoir = RollingReservoir::new(DEFAULT_RESERVOIR_NS);
        reservoir.push(ReservoirSample::new(
            SampleKind::CameraFrame,
            "cam-a",
            10,
            11,
            0,
            "frame-a",
        ));
        reservoir.push(ReservoirSample::new(
            SampleKind::AudioBlock,
            "mic",
            12,
            13,
            0,
            "audio",
        ));
        reservoir.push(ReservoirSample::new(
            SampleKind::CameraFrame,
            "cam-b",
            14,
            15,
            0,
            "frame-b",
        ));

        assert_eq!("audio", reservoir.sample_at(1).unwrap().payload);
        assert_eq!(
            "frame-b",
            reservoir
                .view_sample_at(SampleKind::CameraFrame, 1)
                .unwrap()
                .payload
        );
        assert!(
            reservoir
                .view_sample_at(SampleKind::PhaseClaim, 0)
                .is_none()
        );
    }

    #[test]
    fn latest_for_sensor_is_scoped_to_kind_and_sensor() {
        let mut reservoir = RollingReservoir::new(DEFAULT_RESERVOIR_NS);
        reservoir.push(ReservoirSample::new(
            SampleKind::CameraFrame,
            "cam-a",
            10,
            11,
            0,
            "frame-a0",
        ));
        reservoir.push(ReservoirSample::new(
            SampleKind::AudioBlock,
            "cam-a",
            12,
            13,
            0,
            "audio-a0",
        ));
        reservoir.push(ReservoirSample::new(
            SampleKind::CameraFrame,
            "cam-a",
            14,
            15,
            1,
            "frame-a1",
        ));

        let latest = reservoir
            .latest_for_sensor(SampleKind::CameraFrame, "cam-a")
            .unwrap();

        assert_eq!("frame-a1", latest.payload);
        assert_eq!(1, latest.sequence);
    }

    #[test]
    fn late_samples_outside_the_window_cannot_be_observed() {
        let mut reservoir = RollingReservoir::new(DEFAULT_RESERVOIR_NS);
        reservoir.push(ReservoirSample::new(
            SampleKind::AudioBlock,
            "audio",
            8_000_000_000,
            8_000_000_010,
            0,
            "edge",
        ));
        reservoir.push(ReservoirSample::new(
            SampleKind::CameraFrame,
            "fallback-camera",
            1_000_000_000,
            8_000_000_020,
            0,
            "too-late",
        ));

        assert_eq!(1, reservoir.len());
        assert_eq!(0, reservoir.view_len(SampleKind::CameraFrame));
        assert!(
            reservoir
                .latest_for_sensor(SampleKind::CameraFrame, "fallback-camera")
                .is_none()
        );
    }

    #[test]
    fn c_abi_pushes_and_queries_latest_sample_from_view() {
        let reservoir = localcast_reservoir_create(DEFAULT_RESERVOIR_NS);
        let pushed = localcast_reservoir_push(reservoir, 0, handle(42, 10, 11, 7, 99));
        let mut out = handle(0, 0, 0, 0, 0);
        let found = localcast_reservoir_latest_for_sensor(reservoir, 0, 42, &mut out);

        assert!(pushed);
        assert!(found);
        assert_eq!(1, localcast_reservoir_len(reservoir));
        assert_eq!(1, localcast_reservoir_view_len(reservoir, 0));
        assert_eq!(99, out.payload_handle);
        localcast_reservoir_destroy(reservoir);
    }

    #[test]
    fn c_abi_reads_total_buffer_and_view_samples_by_index() {
        let reservoir = localcast_reservoir_create(DEFAULT_RESERVOIR_NS);
        assert!(localcast_reservoir_push(
            reservoir,
            0,
            handle(10, 10, 11, 0, 100),
        ));
        assert!(localcast_reservoir_push(
            reservoir,
            5,
            handle(20, 12, 13, 0, 200),
        ));
        assert!(localcast_reservoir_push(
            reservoir,
            0,
            handle(30, 14, 15, 0, 300),
        ));

        let mut kind = 99;
        let mut sample = handle(0, 0, 0, 0, 0);

        assert!(localcast_reservoir_sample_at(
            reservoir,
            1,
            &mut kind,
            &mut sample,
        ));
        assert_eq!(5, kind);
        assert_eq!(200, sample.payload_handle);
        assert!(localcast_reservoir_view_sample_at(
            reservoir,
            0,
            1,
            &mut sample,
        ));
        assert_eq!(300, sample.payload_handle);
        assert!(!localcast_reservoir_sample_at(
            reservoir,
            99,
            &mut kind,
            &mut sample,
        ));
        assert!(!localcast_reservoir_view_sample_at(
            reservoir,
            99,
            0,
            &mut sample,
        ));
        localcast_reservoir_destroy(reservoir);
    }

    #[test]
    fn c_abi_rejects_diagnostic_samples_from_live_reservoir() {
        let reservoir = localcast_reservoir_create(DEFAULT_RESERVOIR_NS);

        assert!(!localcast_reservoir_push(
            reservoir,
            0,
            diagnostic_handle(66, 10, 11, 0, 666),
        ));
        assert_eq!(0, localcast_reservoir_len(reservoir));
        assert_eq!(0, localcast_reservoir_view_len(reservoir, 0));

        localcast_reservoir_destroy(reservoir);
    }

    #[test]
    fn c_abi_rejects_unknown_flagged_samples_from_live_reservoir() {
        let reservoir = localcast_reservoir_create(DEFAULT_RESERVOIR_NS);
        let mut sample = handle(66, 10, 11, 0, 666);
        sample.flags = 1 << 30;

        assert!(!localcast_reservoir_push(reservoir, 0, sample));
        assert_eq!(0, localcast_reservoir_len(reservoir));

        localcast_reservoir_destroy(reservoir);
    }

    #[test]
    fn c_abi_rejects_nulls_and_unknown_sample_kinds() {
        let sample = handle(1, 2, 3, 4, 5);
        let mut out = sample;

        assert!(!localcast_reservoir_push(std::ptr::null_mut(), 0, sample));
        assert!(!localcast_reservoir_set_edge(std::ptr::null_mut(), 10));
        assert_eq!(0, localcast_reservoir_edge_ns(std::ptr::null()));
        assert_eq!(0, localcast_reservoir_window_start_ns(std::ptr::null()));
        assert_eq!(0, localcast_reservoir_len(std::ptr::null()));
        assert_eq!(0, localcast_reservoir_view_len(std::ptr::null(), 0));
        assert!(!localcast_reservoir_latest_for_sensor(
            std::ptr::null(),
            0,
            1,
            &mut out,
        ));

        let reservoir = localcast_reservoir_create(0);
        assert!(!localcast_reservoir_push(reservoir, 99, sample));
        assert_eq!(0, localcast_reservoir_view_len(reservoir, 99));
        assert!(!localcast_reservoir_latest_for_sensor(
            reservoir,
            0,
            1,
            std::ptr::null_mut(),
        ));
        localcast_reservoir_destroy(reservoir);
    }

    #[test]
    fn runtime_spine_routes_typed_producers_into_one_rolling_buffer() {
        let runtime = localcast_runtime_create(DEFAULT_RESERVOIR_NS);
        let camera = handle(10, 1_000_000_000, 1_000_000_001, 1, 100);
        let audio = handle(20, 7_000_000_001, 7_000_000_010, 2, 200);
        let phase = handle(30, 7_000_000_010, 7_000_000_020, 3, 300);

        assert!(localcast_runtime_push_camera_frame(runtime, camera));
        assert!(localcast_runtime_push_audio_block(runtime, audio));
        assert!(localcast_runtime_push_phase_claim(runtime, phase));

        let mut status = LocalcastRuntimeStatus::default();
        assert!(localcast_runtime_status(runtime, &mut status));
        assert_eq!(7_000_000_010, status.edge_ns);
        assert_eq!(2_000_000_010, status.window_start_ns);
        assert_eq!(2, status.total_sample_count);
        assert_eq!(0, status.camera_frame_count);
        assert_eq!(1, status.audio_block_count);
        assert_eq!(1, status.phase_claim_count);

        localcast_runtime_destroy(runtime);
    }

    #[test]
    fn runtime_spine_exposes_total_and_typed_window_reads() {
        let runtime = localcast_runtime_create(DEFAULT_RESERVOIR_NS);
        assert!(localcast_runtime_push_camera_frame(
            runtime,
            handle(10, 10, 11, 1, 100),
        ));
        assert!(localcast_runtime_push_audio_block(
            runtime,
            handle(20, 12, 13, 2, 200),
        ));
        assert!(localcast_runtime_push_camera_frame(
            runtime,
            handle(10, 14, 15, 3, 300),
        ));

        assert_eq!(3, localcast_runtime_len(runtime));
        assert_eq!(2, localcast_runtime_view_len(runtime, 0));
        assert_eq!(1, localcast_runtime_view_len(runtime, 5));

        let mut kind = 99;
        let mut sample = handle(0, 0, 0, 0, 0);
        assert!(localcast_runtime_sample_at(
            runtime,
            1,
            &mut kind,
            &mut sample,
        ));
        assert_eq!(5, kind);
        assert_eq!(200, sample.payload_handle);

        assert!(localcast_runtime_view_sample_at(runtime, 0, 1, &mut sample,));
        assert_eq!(300, sample.payload_handle);

        assert!(localcast_runtime_latest_for_sensor(
            runtime,
            0,
            10,
            &mut sample,
        ));
        assert_eq!(300, sample.payload_handle);

        localcast_runtime_destroy(runtime);
    }

    #[test]
    fn runtime_spine_rejects_nulls() {
        let sample = handle(1, 2, 3, 4, 5);
        let mut status = LocalcastRuntimeStatus::default();

        assert!(!localcast_runtime_push_camera_frame(
            std::ptr::null_mut(),
            sample,
        ));
        assert!(!localcast_runtime_status(std::ptr::null(), &mut status));
        assert_eq!(0, localcast_runtime_len(std::ptr::null()));
        assert_eq!(0, localcast_runtime_view_len(std::ptr::null(), 0));
        let runtime = localcast_runtime_create(0);
        assert!(!localcast_runtime_status(runtime, std::ptr::null_mut()));
        let mut kind = 0;
        let mut out = sample;
        assert!(!localcast_runtime_sample_at(
            runtime,
            0,
            std::ptr::null_mut(),
            &mut out,
        ));
        assert!(!localcast_runtime_sample_at(
            runtime,
            0,
            &mut kind,
            std::ptr::null_mut(),
        ));
        assert!(!localcast_runtime_view_sample_at(runtime, 99, 0, &mut out,));
        assert!(!localcast_runtime_latest_for_sensor(
            runtime, 99, 1, &mut out,
        ));
        localcast_runtime_destroy(runtime);
    }

    #[test]
    fn runtime_spine_rejects_diagnostic_samples() {
        let runtime = localcast_runtime_create(DEFAULT_RESERVOIR_NS);

        assert!(!localcast_runtime_push_camera_frame(
            runtime,
            diagnostic_handle(1, 2, 3, 4, 5),
        ));

        let mut status = LocalcastRuntimeStatus::default();
        assert!(localcast_runtime_status(runtime, &mut status));
        assert_eq!(0, status.total_sample_count);
        assert_eq!(0, status.camera_frame_count);

        localcast_runtime_destroy(runtime);
    }

    #[test]
    fn runtime_spine_rejects_unknown_flagged_samples() {
        let runtime = localcast_runtime_create(DEFAULT_RESERVOIR_NS);
        let mut sample = handle(1, 2, 3, 4, 5);
        sample.flags = 1 << 30;

        assert!(!localcast_runtime_push_camera_frame(runtime, sample));

        let mut status = LocalcastRuntimeStatus::default();
        assert!(localcast_runtime_status(runtime, &mut status));
        assert_eq!(0, status.total_sample_count);

        localcast_runtime_destroy(runtime);
    }
}
