// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Egor Khindikaynen (Nota). See LICENSES/ for license terms.
//
// OutputTap — the rendered master mix, handed to a host program (AGENT DAW Listen Together, spike S2).
//
// One producer (the audio thread, at the end of the live render) and one consumer (any single non-real-time
// thread). The producer is wait-free: no allocation, no lock, no syscall (AR-6). If the consumer falls behind and
// the ring fills, the producer drops the part of the block that does not fit, counts it, and carries on; it never
// waits for the consumer. The ring is allocated once, up front, so enabling or disabling the tap never frees
// memory the audio thread may be using.
//
// Stereo, interleaved float32, at the engine's sample rate (nota_engine_sample_rate).

#pragma once

#include <algorithm>
#include <atomic>
#include <cstdint>
#include <cstring>
#include <memory>

namespace nota {

class OutputTap {
  public:
    static constexpr uint32_t kCapacityFrames = 1u << 17; // about 2.7 s at 48 kHz
    static constexpr uint32_t kMask = kCapacityFrames - 1;

    OutputTap() : buf_(new float[static_cast<size_t>(kCapacityFrames) * 2]()) {}

    // ---- producer: audio thread ----------------------------------------------------------------
    void write(const float* interleavedStereo, int32_t frames) noexcept {
        if (frames <= 0 || !enabled_.load(std::memory_order_relaxed)) return;
        const uint64_t head = head_.load(std::memory_order_relaxed);
        const uint64_t tail = tail_.load(std::memory_order_acquire);
        const uint64_t freeFrames = kCapacityFrames - (head - tail);
        const uint32_t n = static_cast<uint32_t>(std::min<uint64_t>(static_cast<uint64_t>(frames), freeFrames));
        if (n < static_cast<uint32_t>(frames)) dropped_.fetch_add(static_cast<uint64_t>(frames) - n, std::memory_order_relaxed);
        if (n == 0) return;
        const uint32_t at = static_cast<uint32_t>(head) & kMask;
        const uint32_t first = std::min(n, kCapacityFrames - at);
        std::memcpy(buf_.get() + static_cast<size_t>(at) * 2, interleavedStereo, static_cast<size_t>(first) * 2 * sizeof(float));
        if (n > first) std::memcpy(buf_.get(), interleavedStereo + static_cast<size_t>(first) * 2, static_cast<size_t>(n - first) * 2 * sizeof(float));
        head_.store(head + n, std::memory_order_release);
    }

    // ---- consumer: one non-real-time thread ------------------------------------------------------
    // Copies up to maxFrames frames into `out` and returns how many.
    uint32_t read(float* out, uint32_t maxFrames) noexcept {
        const uint64_t tail = tail_.load(std::memory_order_relaxed);
        const uint64_t head = head_.load(std::memory_order_acquire);
        const uint32_t n = static_cast<uint32_t>(std::min<uint64_t>(head - tail, maxFrames));
        if (n == 0) return 0;
        const uint32_t at = static_cast<uint32_t>(tail) & kMask;
        const uint32_t first = std::min(n, kCapacityFrames - at);
        std::memcpy(out, buf_.get() + static_cast<size_t>(at) * 2, static_cast<size_t>(first) * 2 * sizeof(float));
        if (n > first) std::memcpy(out + static_cast<size_t>(first) * 2, buf_.get(), static_cast<size_t>(n - first) * 2 * sizeof(float));
        tail_.store(tail + n, std::memory_order_release);
        return n;
    }

    // Turning the tap on discards anything buffered and zeroes the drop counter, so a stream never starts with stale audio.
    // Consumer thread only.
    void setEnabled(bool on) noexcept {
        if (on) {
            tail_.store(head_.load(std::memory_order_acquire), std::memory_order_release);
            dropped_.store(0, std::memory_order_relaxed);
        }
        enabled_.store(on, std::memory_order_release);
    }
    bool enabled() const noexcept { return enabled_.load(std::memory_order_acquire); }
    // Frames the audio thread had to discard because the consumer was behind. An increase means a gap in the stream.
    uint64_t dropped() const noexcept { return dropped_.load(std::memory_order_relaxed); }
    uint32_t buffered() const noexcept {
        return static_cast<uint32_t>(head_.load(std::memory_order_acquire) - tail_.load(std::memory_order_acquire));
    }

  private:
    std::unique_ptr<float[]> buf_;
    alignas(64) std::atomic<uint64_t> head_{0};
    alignas(64) std::atomic<uint64_t> tail_{0};
    alignas(64) std::atomic<uint64_t> dropped_{0};
    std::atomic<bool> enabled_{false};
};

} // namespace nota
