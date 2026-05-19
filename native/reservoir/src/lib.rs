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
        self.samples
            .retain(|sample| sample.timestamp_ns >= start_ns && sample.timestamp_ns <= self.edge_ns);
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
}
