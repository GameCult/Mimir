#include "muninn_rudp_url.h"

#include <cstdlib>
#include <iostream>
#include <string>

namespace {

void require(bool condition, const std::string &message)
{
    if (!condition) {
        std::cerr << message << '\n';
        std::exit(1);
    }
}

} // namespace

int main()
{
    const auto configured = muninn_rudp_url::parse(
        "rudp://192.168.1.66:5204/muninn.raven.av.rudp?channel=realtime&format=muninn-typed-media&connection=0x6d750001&audio_connection=0x6d750004&sender_resend_delay_ms=5&reliable_expire_after_ms=120&assembly_deadline_ms=120&gap_wait_ms=16&audio_reorder_ms=80");
    require(configured.has_value(), "configured RUDP URL did not parse");
    require(configured->port == 5204, "configured RUDP URL port was not parsed");
    require(configured->channel_id == "realtime", "configured RUDP realtime channel was not parsed");
    require(configured->video_local_port == 15204, "configured RUDP video local port was not derived");
    require(configured->audio_local_port == 15205, "configured RUDP audio local port was not derived");
    require(configured->connection_id == 0x6d750001, "configured RUDP connection id was not parsed");
    require(configured->audio_connection_id == 0x6d750004, "configured RUDP audio connection id was not parsed");
    require(configured->reliable_expire_after_ms == 120, "configured reliable expiry did not survive parse");
    require(configured->video_assembly_deadline_ms == 120, "configured assembly deadline did not survive parse");
    require(configured->gap_wait_ms == 16, "configured gap wait did not survive parse");
    require(configured->audio_reorder_ms == 80, "configured audio reorder wait did not survive parse");

    const auto clamped = muninn_rudp_url::parse(
        "rudp://192.168.1.66:5204/muninn.raven.av.rudp?reliable_expire_after_ms=9000&assembly_deadline_ms=9000");
    require(clamped.has_value(), "oversized latency RUDP URL did not parse");
    require(clamped->reliable_expire_after_ms == muninn_rudp_url::MaxLatencyBudgetMs,
            "oversized reliable expiry did not clamp to the max latency budget");
    require(clamped->video_assembly_deadline_ms == muninn_rudp_url::MaxLatencyBudgetMs,
            "oversized assembly deadline did not clamp to the max latency budget");

    const auto fallback = muninn_rudp_url::parse("rudp://192.168.1.66:5204/muninn.raven.av.rudp");
    require(fallback.has_value(), "minimal RUDP URL did not parse");
    require(fallback->channel_id == "media", "minimal RUDP URL did not use bounded reliable media delivery");
    require(fallback->connection_id == muninn_rudp_url::DefaultMediaRudpConnectionId,
            "minimal RUDP URL did not keep the default connection id");
    require(fallback->audio_connection_id == muninn_rudp_url::DefaultAudioRudpConnectionId,
            "minimal RUDP URL did not keep the default audio connection id");
    require(fallback->reliable_expire_after_ms == muninn_rudp_url::DefaultReliableExpireAfterMs,
            "minimal RUDP URL did not keep the default reliable expiry");
    require(fallback->video_assembly_deadline_ms == muninn_rudp_url::DefaultVideoAssemblyDeadlineMs,
            "minimal RUDP URL did not keep the default assembly deadline");
    require(fallback->audio_reorder_ms == muninn_rudp_url::DefaultAudioReorderMs,
            "minimal RUDP URL did not keep the default audio reorder wait");
    require(fallback->audio_reorder_ms == 120,
            "minimal RUDP URL did not use the bounded realtime audio reorder budget");

    return 0;
}
