use std::collections::{BTreeMap, VecDeque};

pub const DEFAULT_RESERVOIR_NS: u64 = 5_000_000_000;

#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub enum RingKind {
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

impl RingKind {
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
}

#[derive(Debug, Clone, PartialEq)]
pub struct Sample<T> {
    pub sensor_id: String,
    pub timestamp_ns: u64,
    pub arrival_ns: u64,
    pub sequence: u64,
    pub payload: T,
}

impl<T> Sample<T> {
    pub fn new(
        sensor_id: impl Into<String>,
        timestamp_ns: u64,
        arrival_ns: u64,
        sequence: u64,
        payload: T,
    ) -> Self {
        Self {
            sensor_id: sensor_id.into(),
            timestamp_ns,
            arrival_ns,
            sequence,
            payload,
        }
    }
}

#[derive(Debug, Clone)]
pub struct ReservoirRing<T> {
    duration_ns: u64,
    edge_ns: u64,
    samples: VecDeque<Sample<T>>,
}

impl<T> ReservoirRing<T> {
    pub fn new(duration_ns: u64) -> Self {
        Self {
            duration_ns,
            edge_ns: 0,
            samples: VecDeque::new(),
        }
    }

    pub fn push(&mut self, sample: Sample<T>) {
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

    pub fn iter(&self) -> impl Iterator<Item = &Sample<T>> {
        self.samples.iter()
    }

    pub fn latest_for_sensor(&self, sensor_id: &str) -> Option<&Sample<T>> {
        self.samples
            .iter()
            .rev()
            .find(|sample| sample.sensor_id == sensor_id)
    }

    fn evict_expired(&mut self) {
        let start_ns = self.window_start_ns();
        self.samples.retain(|sample| {
            sample.timestamp_ns >= start_ns && sample.timestamp_ns <= self.edge_ns
        });
    }
}

#[derive(Debug, Clone)]
pub struct Reservoir<T> {
    duration_ns: u64,
    edge_ns: u64,
    rings: BTreeMap<RingKind, ReservoirRing<T>>,
}

impl<T> Reservoir<T> {
    pub fn new(duration_ns: u64) -> Self {
        Self {
            duration_ns,
            edge_ns: 0,
            rings: BTreeMap::new(),
        }
    }

    pub fn push(&mut self, kind: RingKind, sample: Sample<T>) {
        self.edge_ns = self.edge_ns.max(sample.timestamp_ns);
        for ring in self.rings.values_mut() {
            ring.set_edge(self.edge_ns);
        }
        let ring = self
            .rings
            .entry(kind)
            .or_insert_with(|| ReservoirRing::new(self.duration_ns));
        ring.push(sample);
        ring.set_edge(self.edge_ns);
    }

    pub fn set_edge(&mut self, edge_ns: u64) {
        self.edge_ns = self.edge_ns.max(edge_ns);
        for ring in self.rings.values_mut() {
            ring.set_edge(self.edge_ns);
        }
    }

    pub fn ring(&self, kind: RingKind) -> Option<&ReservoirRing<T>> {
        self.rings.get(&kind)
    }

    pub fn edge_ns(&self) -> u64 {
        self.edge_ns
    }

    pub fn window_start_ns(&self) -> u64 {
        self.edge_ns.saturating_sub(self.duration_ns)
    }
}

impl<T> Default for Reservoir<T> {
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
}

pub struct LocalcastReservoir {
    inner: Reservoir<LocalcastSampleHandle>,
}

pub struct LocalcastRuntime {
    reservoir: Reservoir<LocalcastSampleHandle>,
}

#[repr(C)]
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct LocalcastRuntimeStatus {
    pub edge_ns: u64,
    pub window_start_ns: u64,
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
        let duration = if duration_ns == 0 {
            DEFAULT_RESERVOIR_NS
        } else {
            duration_ns
        };
        Self {
            reservoir: Reservoir::new(duration),
        }
    }

    fn push(&mut self, kind: RingKind, sample: LocalcastSampleHandle) {
        self.reservoir.push(
            kind,
            Sample::new(
                sample.sensor_id_hash.to_string(),
                sample.timestamp_ns,
                sample.arrival_ns,
                sample.sequence,
                sample,
            ),
        );
    }

    fn ring_len(&self, kind: RingKind) -> usize {
        self.reservoir.ring(kind).map_or(0, ReservoirRing::len)
    }

    fn status(&self) -> LocalcastRuntimeStatus {
        LocalcastRuntimeStatus {
            edge_ns: self.reservoir.edge_ns(),
            window_start_ns: self.reservoir.window_start_ns(),
            camera_frame_count: self.ring_len(RingKind::CameraFrame),
            camera_feature_count: self.ring_len(RingKind::CameraFeature),
            scene_ray_count: self.ring_len(RingKind::SceneRay),
            surface_claim_count: self.ring_len(RingKind::SurfaceClaim),
            material_claim_count: self.ring_len(RingKind::MaterialClaim),
            audio_block_count: self.ring_len(RingKind::AudioBlock),
            phase_claim_count: self.ring_len(RingKind::PhaseClaim),
            event_claim_count: self.ring_len(RingKind::EventClaim),
            render_packet_count: self.ring_len(RingKind::RenderPacket),
        }
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn localcast_reservoir_create(duration_ns: u64) -> *mut LocalcastReservoir {
    let duration = if duration_ns == 0 {
        DEFAULT_RESERVOIR_NS
    } else {
        duration_ns
    };
    Box::into_raw(Box::new(LocalcastReservoir {
        inner: Reservoir::new(duration),
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
    ring_kind: u32,
    sample: LocalcastSampleHandle,
) -> bool {
    let Some(reservoir) = (unsafe { ptr.as_mut() }) else {
        return false;
    };
    let Some(kind) = RingKind::from_u32(ring_kind) else {
        return false;
    };
    reservoir.inner.push(
        kind,
        Sample::new(
            sample.sensor_id_hash.to_string(),
            sample.timestamp_ns,
            sample.arrival_ns,
            sample.sequence,
            sample,
        ),
    );
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
pub extern "C" fn localcast_reservoir_ring_len(
    ptr: *const LocalcastReservoir,
    ring_kind: u32,
) -> usize {
    let Some(reservoir) = (unsafe { ptr.as_ref() }) else {
        return 0;
    };
    let Some(kind) = RingKind::from_u32(ring_kind) else {
        return 0;
    };
    reservoir.inner.ring(kind).map_or(0, ReservoirRing::len)
}

#[unsafe(no_mangle)]
pub extern "C" fn localcast_reservoir_latest_for_sensor(
    ptr: *const LocalcastReservoir,
    ring_kind: u32,
    sensor_id_hash: u64,
    out_sample: *mut LocalcastSampleHandle,
) -> bool {
    let Some(reservoir) = (unsafe { ptr.as_ref() }) else {
        return false;
    };
    let Some(kind) = RingKind::from_u32(ring_kind) else {
        return false;
    };
    let Some(out) = (unsafe { out_sample.as_mut() }) else {
        return false;
    };
    let Some(ring) = reservoir.inner.ring(kind) else {
        return false;
    };
    let key = sensor_id_hash.to_string();
    let Some(sample) = ring.latest_for_sensor(&key) else {
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
    localcast_runtime_push_typed(ptr, RingKind::CameraFrame, sample)
}

#[unsafe(no_mangle)]
pub extern "C" fn localcast_runtime_push_camera_feature(
    ptr: *mut LocalcastRuntime,
    sample: LocalcastSampleHandle,
) -> bool {
    localcast_runtime_push_typed(ptr, RingKind::CameraFeature, sample)
}

#[unsafe(no_mangle)]
pub extern "C" fn localcast_runtime_push_scene_ray(
    ptr: *mut LocalcastRuntime,
    sample: LocalcastSampleHandle,
) -> bool {
    localcast_runtime_push_typed(ptr, RingKind::SceneRay, sample)
}

#[unsafe(no_mangle)]
pub extern "C" fn localcast_runtime_push_surface_claim(
    ptr: *mut LocalcastRuntime,
    sample: LocalcastSampleHandle,
) -> bool {
    localcast_runtime_push_typed(ptr, RingKind::SurfaceClaim, sample)
}

#[unsafe(no_mangle)]
pub extern "C" fn localcast_runtime_push_material_claim(
    ptr: *mut LocalcastRuntime,
    sample: LocalcastSampleHandle,
) -> bool {
    localcast_runtime_push_typed(ptr, RingKind::MaterialClaim, sample)
}

#[unsafe(no_mangle)]
pub extern "C" fn localcast_runtime_push_audio_block(
    ptr: *mut LocalcastRuntime,
    sample: LocalcastSampleHandle,
) -> bool {
    localcast_runtime_push_typed(ptr, RingKind::AudioBlock, sample)
}

#[unsafe(no_mangle)]
pub extern "C" fn localcast_runtime_push_phase_claim(
    ptr: *mut LocalcastRuntime,
    sample: LocalcastSampleHandle,
) -> bool {
    localcast_runtime_push_typed(ptr, RingKind::PhaseClaim, sample)
}

#[unsafe(no_mangle)]
pub extern "C" fn localcast_runtime_push_event_claim(
    ptr: *mut LocalcastRuntime,
    sample: LocalcastSampleHandle,
) -> bool {
    localcast_runtime_push_typed(ptr, RingKind::EventClaim, sample)
}

#[unsafe(no_mangle)]
pub extern "C" fn localcast_runtime_push_render_packet(
    ptr: *mut LocalcastRuntime,
    sample: LocalcastSampleHandle,
) -> bool {
    localcast_runtime_push_typed(ptr, RingKind::RenderPacket, sample)
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

fn localcast_runtime_push_typed(
    ptr: *mut LocalcastRuntime,
    kind: RingKind,
    sample: LocalcastSampleHandle,
) -> bool {
    let Some(runtime) = (unsafe { ptr.as_mut() }) else {
        return false;
    };
    runtime.push(kind, sample);
    true
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn ring_expires_from_newest_shared_edge() {
        let mut ring = ReservoirRing::new(DEFAULT_RESERVOIR_NS);
        ring.push(Sample::new("cam-a", 1_000_000_000, 1_000_000_010, 0, 1));
        ring.push(Sample::new("cam-b", 4_000_000_000, 4_000_000_010, 0, 2));
        ring.push(Sample::new("cam-a", 7_100_000_000, 7_100_000_010, 1, 3));

        let timestamps = ring
            .iter()
            .map(|sample| sample.timestamp_ns)
            .collect::<Vec<_>>();

        assert_eq!(vec![4_000_000_000, 7_100_000_000], timestamps);
        assert_eq!(2_100_000_000, ring.window_start_ns());
    }

    #[test]
    fn reservoir_edge_expires_all_rings() {
        let mut reservoir = Reservoir::default();
        reservoir.push(
            RingKind::CameraFrame,
            Sample::new("cam-a", 1_000_000_000, 1_000_000_010, 0, "old-camera"),
        );
        reservoir.push(
            RingKind::AudioBlock,
            Sample::new("audio", 7_000_000_001, 7_000_000_020, 0, "audio-edge"),
        );

        let camera_ring = reservoir.ring(RingKind::CameraFrame).unwrap();

        assert!(camera_ring.is_empty());
        assert_eq!(2_000_000_001, reservoir.window_start_ns());
    }

    #[test]
    fn latest_for_sensor_ignores_other_sources() {
        let mut ring = ReservoirRing::new(DEFAULT_RESERVOIR_NS);
        ring.push(Sample::new("cam-a", 10, 11, 0, "a0"));
        ring.push(Sample::new("cam-b", 12, 13, 0, "b0"));
        ring.push(Sample::new("cam-a", 14, 15, 1, "a1"));

        let latest = ring.latest_for_sensor("cam-a").unwrap();

        assert_eq!("a1", latest.payload);
        assert_eq!(1, latest.sequence);
    }

    #[test]
    fn c_abi_pushes_and_queries_latest_sample() {
        let reservoir = localcast_reservoir_create(DEFAULT_RESERVOIR_NS);
        let pushed = localcast_reservoir_push(
            reservoir,
            0,
            LocalcastSampleHandle {
                sensor_id_hash: 42,
                timestamp_ns: 10,
                arrival_ns: 11,
                sequence: 7,
                payload_handle: 99,
            },
        );
        let mut out = LocalcastSampleHandle {
            sensor_id_hash: 0,
            timestamp_ns: 0,
            arrival_ns: 0,
            sequence: 0,
            payload_handle: 0,
        };
        let found = localcast_reservoir_latest_for_sensor(reservoir, 0, 42, &mut out);

        assert!(pushed);
        assert!(found);
        assert_eq!(1, localcast_reservoir_ring_len(reservoir, 0));
        assert_eq!(99, out.payload_handle);
        localcast_reservoir_destroy(reservoir);
    }

    #[test]
    fn c_abi_rejects_nulls_and_unknown_ring_kinds() {
        let sample = LocalcastSampleHandle {
            sensor_id_hash: 1,
            timestamp_ns: 2,
            arrival_ns: 3,
            sequence: 4,
            payload_handle: 5,
        };
        let mut out = sample;

        assert!(!localcast_reservoir_push(std::ptr::null_mut(), 0, sample));
        assert!(!localcast_reservoir_set_edge(std::ptr::null_mut(), 10));
        assert_eq!(0, localcast_reservoir_edge_ns(std::ptr::null()));
        assert_eq!(0, localcast_reservoir_window_start_ns(std::ptr::null()));
        assert_eq!(0, localcast_reservoir_ring_len(std::ptr::null(), 0));
        assert!(!localcast_reservoir_latest_for_sensor(
            std::ptr::null(),
            0,
            1,
            &mut out,
        ));

        let reservoir = localcast_reservoir_create(0);
        assert!(!localcast_reservoir_push(reservoir, 99, sample));
        assert_eq!(0, localcast_reservoir_ring_len(reservoir, 99));
        assert!(!localcast_reservoir_latest_for_sensor(
            reservoir,
            0,
            1,
            std::ptr::null_mut(),
        ));
        localcast_reservoir_destroy(reservoir);
    }

    #[test]
    fn runtime_spine_routes_typed_producers_into_one_reservoir() {
        let runtime = localcast_runtime_create(DEFAULT_RESERVOIR_NS);
        let camera = LocalcastSampleHandle {
            sensor_id_hash: 10,
            timestamp_ns: 1_000_000_000,
            arrival_ns: 1_000_000_001,
            sequence: 1,
            payload_handle: 100,
        };
        let audio = LocalcastSampleHandle {
            sensor_id_hash: 20,
            timestamp_ns: 7_000_000_001,
            arrival_ns: 7_000_000_010,
            sequence: 2,
            payload_handle: 200,
        };
        let phase = LocalcastSampleHandle {
            sensor_id_hash: 30,
            timestamp_ns: 7_000_000_010,
            arrival_ns: 7_000_000_020,
            sequence: 3,
            payload_handle: 300,
        };

        assert!(localcast_runtime_push_camera_frame(runtime, camera));
        assert!(localcast_runtime_push_audio_block(runtime, audio));
        assert!(localcast_runtime_push_phase_claim(runtime, phase));

        let mut status = LocalcastRuntimeStatus::default();
        assert!(localcast_runtime_status(runtime, &mut status));
        assert_eq!(7_000_000_010, status.edge_ns);
        assert_eq!(2_000_000_010, status.window_start_ns);
        assert_eq!(0, status.camera_frame_count);
        assert_eq!(1, status.audio_block_count);
        assert_eq!(1, status.phase_claim_count);

        localcast_runtime_destroy(runtime);
    }

    #[test]
    fn runtime_spine_rejects_nulls() {
        let sample = LocalcastSampleHandle {
            sensor_id_hash: 1,
            timestamp_ns: 2,
            arrival_ns: 3,
            sequence: 4,
            payload_handle: 5,
        };
        let mut status = LocalcastRuntimeStatus::default();

        assert!(!localcast_runtime_push_camera_frame(
            std::ptr::null_mut(),
            sample,
        ));
        assert!(!localcast_runtime_status(std::ptr::null(), &mut status));
        let runtime = localcast_runtime_create(0);
        assert!(!localcast_runtime_status(runtime, std::ptr::null_mut()));
        localcast_runtime_destroy(runtime);
    }
}
