declare name "Mimir Alignment Actuator";
declare version "0.1";
declare author "Mimir";
declare license "MIT";

import("stdfaust.lib");

// Faust/native DSP owns sample movement. Mimir.Runtime estimates delay/SRO and
// writes control values; this graph applies bounded fractional delay per source.
// Positive delay controls hold a source back. Upstream runtime policy should
// choose which sources are reference and which need correction.

maxDelay = 4096;

delay0 = hslider("source0/delay_samples", 0.0, 0.0, maxDelay, 0.001);
delay1 = hslider("source1/delay_samples", 0.0, 0.0, maxDelay, 0.001);
delay2 = hslider("source2/delay_samples", 0.0, 0.0, maxDelay, 0.001);
delay3 = hslider("source3/delay_samples", 0.0, 0.0, maxDelay, 0.001);
delay4 = hslider("source4/delay_samples", 0.0, 0.0, maxDelay, 0.001);
delay5 = hslider("source5/delay_samples", 0.0, 0.0, maxDelay, 0.001);

gain0 = hslider("source0/gain", 1.0, 0.0, 2.0, 0.001);
gain1 = hslider("source1/gain", 1.0, 0.0, 2.0, 0.001);
gain2 = hslider("source2/gain", 1.0, 0.0, 2.0, 0.001);
gain3 = hslider("source3/gain", 1.0, 0.0, 2.0, 0.001);
gain4 = hslider("source4/gain", 1.0, 0.0, 2.0, 0.001);
gain5 = hslider("source5/gain", 1.0, 0.0, 2.0, 0.001);

align(delay, gain, input) = input : de.fdelay(maxDelay, delay) : *(gain);

process =
    align(delay0, gain0),
    align(delay1, gain1),
    align(delay2, gain2),
    align(delay3, gain3),
    align(delay4, gain4),
    align(delay5, gain5);
