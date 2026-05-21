declare name "Mimir Voice Separation";
declare version "1.0";
declare author "Mimir";
declare license "MIT";

// Six aligned microphone inputs:
// 0 host Focusrite cardioid
// 1 co-streamer Focusrite shotgun
// 2-3 Kiyo room/context witnesses
// 4-5 PS Eye spatial witnesses
//
// Outputs:
// 0 host voice
// 1 co-streamer voice
// 2 ambient bed
// 3 transient/witness bed
// 4 local loopback placeholder
// 5 co-streamer loopback placeholder

hostGain = hslider("host/gain", 1.0, 0.0, 4.0, 0.01);
hostReject = hslider("host/witness_reject", 0.35, 0.0, 1.0, 0.01);
coGain = hslider("co_streamer/gain", 1.0, 0.0, 4.0, 0.01);
coReject = hslider("co_streamer/witness_reject", 0.35, 0.0, 1.0, 0.01);
ambientGain = hslider("ambient/gain", 0.6, 0.0, 2.0, 0.01);
transientGain = hslider("transients/gain", 0.7, 0.0, 2.0, 0.01);

voice_separation(m0, m1, m2, m3, m4, m5) = host, costreamer, ambient, transients, localLoopback, coLoopback
with {
    room = (m2 + m3 + m4 + m5) * 0.25;
    ambient = ambientGain * room;
    transients = transientGain * ((m4 + m5) * 0.5 - (m2 + m3) * 0.25);
    host = hostGain * (m0 - hostReject * room);
    costreamer = coGain * (m1 - coReject * room);
    localLoopback = 0.0 * m0;
    coLoopback = 0.0 * m1;
};

process = voice_separation;
