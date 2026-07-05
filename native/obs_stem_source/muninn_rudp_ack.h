#pragma once

#include <cstdint>
#include <optional>
#include <set>
#include <utility>

namespace muninn_rudp_ack {

class AckTracker {
public:
    void reset()
    {
        received_sequences_.clear();
        highest_received_sequence_.reset();
    }

    void remember(uint32_t sequence)
    {
        received_sequences_.insert(sequence);
        if (!highest_received_sequence_ || sequence > *highest_received_sequence_) {
            highest_received_sequence_ = sequence;
        }
        if (!highest_received_sequence_) {
            return;
        }
        const uint32_t highest = *highest_received_sequence_;
        for (auto iterator = received_sequences_.begin(); iterator != received_sequences_.end();) {
            if (*iterator + 32 < highest) {
                iterator = received_sequences_.erase(iterator);
            } else {
                ++iterator;
            }
        }
    }

    std::pair<uint32_t, uint32_t> state() const
    {
        const uint32_t ack = highest_received_sequence_.value_or(0);
        uint32_t ack_mask = 0;
        for (uint32_t bit = 0; bit < 32; ++bit) {
            if (ack > bit && received_sequences_.find(ack - bit - 1) != received_sequences_.end()) {
                ack_mask |= 1u << bit;
            }
        }
        return {ack, ack_mask};
    }

private:
    std::set<uint32_t> received_sequences_;
    std::optional<uint32_t> highest_received_sequence_;
};

} // namespace muninn_rudp_ack
