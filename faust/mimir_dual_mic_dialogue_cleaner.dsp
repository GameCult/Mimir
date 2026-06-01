declare name "Mimir Dual Mic Dialogue Cleaner";
declare version "0.1";
declare author "Mimir";
declare license "MIT";
declare description "Faust-owned smooth dual-mic dialogue cleanup target: aligned shotgun/cardioid inputs become a conservative dialogue stem and a rejected residual witness.";

import("stdfaust.lib");

// Inputs are already delay-aligned upstream by mimir_alignment_actuator.
// This graph owns smooth audio-rate cleanup, not calibration, timing, or UI.
//
// input 0: aligned shotgun / co-streamer witness
// input 1: aligned cardioid / host witness
// output 0: dialogue composite stem
// output 1: rejected residual / noise witness

drive = hslider("dialogue/drive", 1.00, 0.00, 2.00, 0.001);
shotgunWeight = hslider("dialogue/shotgun_weight", 0.45, 0.00, 1.00, 0.001);
cardioidWeight = hslider("dialogue/cardioid_weight", 0.55, 0.00, 1.00, 0.001);
witnessReject = hslider("dialogue/witness_reject", 0.20, 0.00, 1.00, 0.001);
lowShelf = hslider("dialogue/low_shelf", 0.80, 0.00, 2.00, 0.001);
presenceShelf = hslider("dialogue/presence_shelf", 1.08, 0.00, 2.00, 0.001);
airShelf = hslider("dialogue/air_shelf", 0.90, 0.00, 2.00, 0.001);

// Smooth broadband downward expansion. This is intentionally slow enough to
// avoid the choppy gate artifact from the C# audition bridge.
floorLevel = hslider("denoise/noise_floor", 0.018, 0.0001, 0.25, 0.0001);
expansionDepth = hslider("denoise/expansion_depth", 0.32, 0.00, 0.95, 0.001);
expansionCurve = hslider("denoise/expansion_curve", 1.35, 0.25, 4.00, 0.001);
envelopeHz = hslider("denoise/envelope_hz", 18.0, 1.0, 80.0, 0.1);

band(f, q, g, x) = x : fi.resonbp(1.0, f, q) : *(g);

voiceShape(x) =
    band(120.0, 1.2, lowShelf, x) +
    band(220.0, 1.6, 0.95, x) +
    band(420.0, 2.0, 0.98, x) +
    band(800.0, 2.4, 1.00, x) +
    band(1500.0, 2.8, presenceShelf, x) +
    band(2800.0, 3.2, presenceShelf, x) +
    band(5200.0, 3.2, airShelf, x) +
    band(8500.0, 2.6, airShelf, x);

smoothAbs(x) = abs(x) : si.smooth(ba.tau2pole(1.0 / max(1.0, envelopeHz)));

softExpand(x) = x * gain
with {
    env = smoothAbs(x);
    ratio = min(1.0, env / max(0.0001, floorLevel));
    openness = pow(ratio, expansionCurve);
    gain = 1.0 - expansionDepth * (1.0 - openness);
};

softClip(x) = x / (1.0 + abs(x));

clean(shotgun, cardioid) = dialogue, residual
with {
    weighted = shotgun * shotgunWeight + cardioid * cardioidWeight;
    witness = (shotgun - cardioid) * 0.5;
    shaped = voiceShape(weighted - witnessReject * witness);
    dialogue = softExpand(shaped * drive) : softClip;
    residual = witness + (weighted - dialogue) * 0.25;
};

process = clean;
