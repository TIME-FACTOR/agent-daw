// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Egor Khindikaynen (Nota). See LICENSES/ for license terms.
//
// Uid — a stable 128-bit identity for editable objects (tracks, clips).
//
// Why: tracks and clips are otherwise addressed by position (track id is engine-local and
// reassigned on load; clip index shifts on every edit). Anything that must refer to "the same
// clip" across edits, saves, undo and different machines (version diffs, three-way merges,
// collaboration) needs an identity that travels with the object.
//
// Rules (enforced in Engine::settleUids, the one place ids are assigned):
//   * null (0,0) means "not assigned yet"; the engine mints one on the next publish.
//   * a copy (duplicate, paste, split's right half) is a new object: it gets a fresh id when it
//     lands in the same snapshot as an object with that id.
//   * a move, trim or edit keeps the id. Undo/redo restores the id with the object.
//
// Message thread only. Never minted or compared on the audio thread.

#pragma once

#include <cstdint>
#include <random>
#include <string>

namespace nota {

struct Uid {
    uint64_t hi = 0;
    uint64_t lo = 0;

    bool isNull() const { return hi == 0 && lo == 0; }
    bool operator==(const Uid& o) const { return hi == o.hi && lo == o.lo; }
    bool operator!=(const Uid& o) const { return !(*this == o); }

    // 32 lowercase hex digits, no separators.
    std::string toHex() const {
        static const char* d = "0123456789abcdef";
        std::string s(32, '0');
        for (int i = 0; i < 16; ++i) {
            s[static_cast<size_t>(i)] = d[(hi >> (60 - 4 * i)) & 0xF];
            s[static_cast<size_t>(16 + i)] = d[(lo >> (60 - 4 * i)) & 0xF];
        }
        return s;
    }

    // Parses exactly 32 hex digits (either case). Returns false and leaves *out untouched otherwise.
    static bool fromHex(const char* s, Uid* out) {
        if (!s || !out) return false;
        uint64_t parts[2] = {0, 0};
        for (int i = 0; i < 32; ++i) {
            char c = s[i];
            uint64_t v;
            if (c >= '0' && c <= '9') v = static_cast<uint64_t>(c - '0');
            else if (c >= 'a' && c <= 'f') v = static_cast<uint64_t>(c - 'a' + 10);
            else if (c >= 'A' && c <= 'F') v = static_cast<uint64_t>(c - 'A' + 10);
            else return false;
            parts[i / 16] = (parts[i / 16] << 4) | v;
        }
        if (s[32] != '\0') return false;
        out->hi = parts[0];
        out->lo = parts[1];
        return true;
    }

    struct Hash {
        size_t operator()(const Uid& u) const { return static_cast<size_t>(u.hi ^ (u.lo * 0x9E3779B97F4A7C15ull)); }
    };
};

// A fresh random id (RFC 4122 version-4 layout). Never null.
inline Uid mintUid() {
    thread_local std::mt19937_64 rng{std::random_device{}()};
    Uid u;
    do {
        u.hi = rng();
        u.lo = rng();
        u.hi = (u.hi & 0xFFFFFFFFFFFF0FFFull) | 0x0000000000004000ull; // version 4
        u.lo = (u.lo & 0x3FFFFFFFFFFFFFFFull) | 0x8000000000000000ull; // variant 10
    } while (u.isNull());
    return u;
}

} // namespace nota
