declare name "mimir_packet_razor_frontend";
declare version "0.1";
declare description "Faust-shaped native DSP front-end for the Mimir packet razor receiver: log-spaced band energy, smoothed positive flux, and no wide FFT/MFCC pass in the audio callback.";

import("stdfaust.lib");

bands = 16;
lowHz = 850.0;
highHz = 14000.0;
q = 18.0;
attackHz = 140.0;
releaseHz = 38.0;

mel(hz) = 2595.0 * log10(1.0 + hz / 700.0);
hz(m) = 700.0 * (pow(10.0, m / 2595.0) - 1.0);
bandHz(i) = hz(mel(lowHz) + (mel(highHz) - mel(lowHz)) * float(i) / float(max(1, bands - 1)));

// This mirrors the C# streaming receiver contract: native DSP owns cheap
// continuously updated evidence. The managed arena currently uses a direct
// packet-window scorer, but this file is the lowering target for the callback
// path where Faust should emit reusable band/flux control streams.
bandEnergy(i) = fi.resonbp(1.0, bandHz(i), q)
    : abs
    : si.smoo;

positiveFlux(i) = bandEnergy(i)
    <: _, mem
    : -
    : max(0.0)
    : si.smoo;

process = _ <: par(i, bands, bandEnergy(i)), par(i, bands, positiveFlux(i));
