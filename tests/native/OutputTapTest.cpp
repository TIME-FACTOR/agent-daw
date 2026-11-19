// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Egor Khindikaynen (Nota). See LICENSES/ for license terms.
//
// OutputTap unit test (AGENT DAW spike S2). Header-only, so it builds without the engine:
//   tests/native/run-output-tap-test.sh
// Checks ordering and wrap-around, drop counting without blocking, a two-thread stress run with an integrity
// checksum, and that write() allocates nothing (operator new is replaced to count).
#include <atomic>
#include <chrono>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <new>
#include <thread>
#include <vector>

#include "../../src/native/nota.engine/src/engine/OutputTap.h"

static std::atomic<long> g_allocs{0};
static std::atomic<bool> g_count{false};
void* operator new(std::size_t n) { if (g_count.load(std::memory_order_relaxed)) g_allocs.fetch_add(1); void* p = std::malloc(n ? n : 1); if (!p) throw std::bad_alloc(); return p; }
void operator delete(void* p) noexcept { std::free(p); }
void operator delete(void* p, std::size_t) noexcept { std::free(p); }

static int failures = 0;
#define CHECK(cond, msg) do { if (!(cond)) { std::printf("  [FAIL] %s\n", msg); ++failures; } else std::printf("  [PASS] %s\n", msg); } while (0)

using nota::OutputTap;

// Frame i of a test signal: left = i, right = -i (exactly representable up to 2^24).
static void fill(std::vector<float>& v, uint32_t first, uint32_t n) { v.resize(static_cast<size_t>(n) * 2); for (uint32_t i = 0; i < n; ++i) { v[2 * i] = static_cast<float>(first + i); v[2 * i + 1] = -static_cast<float>(first + i); } }

int main() {
    std::printf("-- output tap --\n");
    {
        auto tap = std::make_unique<OutputTap>();
        std::vector<float> in, out(2048 * 2);
        fill(in, 0, 1000);
        tap->write(in.data(), 1000);
        CHECK(tap->buffered() == 0 && tap->read(out.data(), 2048) == 0, "a disabled tap buffers nothing");
        tap->setEnabled(true);
        tap->write(in.data(), 1000);
        CHECK(tap->buffered() == 1000 && tap->read(out.data(), 400) == 400 && out[2 * 399] == 399.f && out[2 * 399 + 1] == -399.f, "frames come out in order, exactly as written");
        CHECK(tap->read(out.data(), 2048) == 600 && out[0] == 400.f, "the rest follows, then nothing");
        CHECK(tap->read(out.data(), 2048) == 0, "an empty tap returns 0");
    }
    {
        auto tap = std::make_unique<OutputTap>();
        tap->setEnabled(true);
        std::vector<float> in, out(4096 * 2);
        uint32_t next = 0, expect = 0; bool ordered = true;
        for (int round = 0; round < 200; ++round) {              // many blocks that straddle the ring's end
            fill(in, next, 700); tap->write(in.data(), 700); next += 700;
            uint32_t got = tap->read(out.data(), 650);
            for (uint32_t i = 0; i < got; ++i, ++expect) ordered = ordered && out[2 * i] == static_cast<float>(expect) && out[2 * i + 1] == -static_cast<float>(expect);
            (void)got;
        }
        for (uint32_t got; (got = tap->read(out.data(), 4096)) > 0;)   // drain the rest: the part that wrapped is read here
            for (uint32_t i = 0; i < got; ++i, ++expect) ordered = ordered && out[2 * i] == static_cast<float>(expect) && out[2 * i + 1] == -static_cast<float>(expect);
        ordered = ordered && expect == next;
        CHECK(ordered, "wrap-around keeps every sample in order over 140000 frames through a 131072-frame ring (consumer a little slower than producer)");
        CHECK(tap->dropped() == 0, "no frames were dropped while the consumer kept (nearly) up");
    }
    {
        auto tap = std::make_unique<OutputTap>();
        tap->setEnabled(true);
        std::vector<float> in, out(OutputTap::kCapacityFrames * 2);
        fill(in, 0, OutputTap::kCapacityFrames - 10);
        tap->write(in.data(), OutputTap::kCapacityFrames - 10);
        std::vector<float> more; fill(more, 777000, 100);
        auto t0 = std::chrono::steady_clock::now();
        tap->write(more.data(), 100);                              // 90 do not fit
        double us = std::chrono::duration<double, std::micro>(std::chrono::steady_clock::now() - t0).count();
        CHECK(tap->dropped() == 90 && tap->buffered() == OutputTap::kCapacityFrames, "overflow drops exactly what does not fit and counts it");
        CHECK(us < 1000.0, "a full ring never blocks the audio thread");
        uint32_t got = tap->read(out.data(), OutputTap::kCapacityFrames);
        CHECK(got == OutputTap::kCapacityFrames && out[2 * (OutputTap::kCapacityFrames - 10)] == 777000.f, "what fit is intact, up to the cut");
        tap->setEnabled(true);
        CHECK(tap->dropped() == 0 && tap->buffered() == 0, "enabling again clears the backlog and the counter");
    }
    {
        auto tap = std::make_unique<OutputTap>();
        tap->setEnabled(true);
        std::vector<float> in; fill(in, 0, 512);
        g_allocs = 0; g_count = true;
        for (int i = 0; i < 100000; ++i) tap->write(in.data(), 512);   // mostly overflowing: exercises both paths
        g_count = false;
        CHECK(g_allocs.load() == 0, "write() performs no allocation (100000 calls, both the fit and the overflow path)");
    }
    {   // threaded: a real producer at "audio" speed against a real consumer; nothing lost without drops, nothing duplicated or reordered ever
        auto tap = std::make_unique<OutputTap>();
        tap->setEnabled(true);
        constexpr uint32_t kBlocks = 4000, kBlock = 480;
        std::atomic<bool> done{false};
        std::thread producer([&] {
            std::vector<float> in; uint32_t next = 0;
            for (uint32_t b = 0; b < kBlocks; ++b) { fill(in, next, kBlock); tap->write(in.data(), kBlock); next += kBlock; if (b % 8 == 0) std::this_thread::sleep_for(std::chrono::microseconds(50)); }
            done = true;
        });
        std::vector<float> out(1024 * 2);
        uint64_t total = 0; double prev = -1; bool monotonic = true, intact = true;
        for (;;) {
            uint32_t n = tap->read(out.data(), 1024);
            for (uint32_t i = 0; i < n; ++i) { double l = out[2 * i]; monotonic = monotonic && l > prev; intact = intact && out[2 * i + 1] == -static_cast<float>(l); prev = l; }
            total += n;
            if (n == 0 && done.load()) { if (tap->buffered() == 0) break; }
            if (n == 0) std::this_thread::yield();
        }
        producer.join();
        CHECK(monotonic && intact, "two threads: never reordered, duplicated or torn");
        CHECK(total + tap->dropped() == static_cast<uint64_t>(kBlocks) * kBlock, "two threads: every frame is either delivered or counted as dropped");
        std::printf("  (threaded run: delivered %llu, dropped %llu)\n", static_cast<unsigned long long>(total), static_cast<unsigned long long>(tap->dropped()));
    }
    std::printf(failures == 0 ? "OUTPUT TAP PASSED\n" : "OUTPUT TAP FAILED (%d)\n", failures);
    return failures == 0 ? 0 : 1;
}
