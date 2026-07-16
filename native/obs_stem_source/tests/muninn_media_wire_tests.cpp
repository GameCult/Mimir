#include "muninn_media_wire.h"

#include <cstdlib>
#include <iomanip>
#include <iostream>

namespace {

void require(bool condition, const char *message)
{
    if (!condition) {
        std::cerr << message << '\n';
        std::exit(1);
    }
}

} // namespace

int main(int argc, char **argv)
{
    const auto early = muninn_media_wire::receiver_feedback_late_frame_ids(42, false);
    require(early.empty(), "early repair feedback marked a live frame late");

    const auto expired = muninn_media_wire::receiver_feedback_late_frame_ids(42, true);
    require(expired.size() == 1 && expired.front() == 42,
            "expired feedback did not name the late frame");

    const auto early_wire = muninn_media_wire::encode_receiver_feedback_payload(
        "muninn.raven.av.rudp",
        "raven:session:video",
        42,
        {"42:3"},
        false,
        false,
        "unix-ms:1000");
    const auto expired_wire = muninn_media_wire::encode_receiver_feedback_payload(
        "muninn.raven.av.rudp",
        "raven:session:video",
        42,
        {"42:3"},
        true,
        true,
        "unix-ms:1000");
    require(!early_wire.empty() && !expired_wire.empty(), "feedback wire encoding was empty");
    require(early_wire != expired_wire, "early and expired feedback encoded identically");
    if (argc == 2 && std::string(argv[1]) == "--emit-early-hex") {
        for (const auto byte : early_wire) {
            std::cout << std::hex << std::setw(2) << std::setfill('0')
                      << static_cast<unsigned int>(byte);
        }
        std::cout << '\n';
    }
    return 0;
}
