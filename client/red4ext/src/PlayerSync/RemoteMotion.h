#pragma once
// Other players' characters, shown about 100 ms behind their owner (CYB-00010). Their movement arrives 20 times a
// second, unreliably (EntityMovement): late or repeated updates are dropped by their sequence, and the rest are kept
// with the owner's send time, so the character is shown at an even pace between two updates however unevenly they
// arrive. Plain C++ without game types, so it can be tested on its own.
#include <algorithm>
#include <cmath>
#include <cstdint>
#include <deque>
#include <map>
#include <optional>

namespace Cyberverse::PlayerSync
{
struct MotionSample
{
    float x = 0.0f;
    float y = 0.0f;
    float z = 0.0f;
    float yaw = 0.0f;   // degrees
    float speed = 0.0f; // horizontal, m/s
    uint8_t state = 0;  // EMovementState
    float vx = 0.0f;    // horizontal velocity between the two updates shown, m/s; 0 while holding one
    float vy = 0.0f;
};

template <class Key>
class RemoteMotion
{
public:
    /// How far behind the owner a character is shown: two updates at 20 per second, so one lost update still leaves
    /// one to move towards.
    static constexpr double Delay = 0.1;
    /// How fast the estimate of the owner's clock may drift later, in seconds per second. Earlier always counts at
    /// once: the fastest update is the best estimate of the travel time.
    static constexpr double ClockRelax = 0.02;
    /// Kept per character at most, so a character nobody samples doesn't grow without end.
    static constexpr size_t MaxSnapshots = 32;

    /// A movement update; `now` is the local clock in seconds. False when it is late or repeated (dropped).
    bool Push(const Key& key, const uint32_t sequence, const uint32_t senderMs, const double now,
              const MotionSample& sample)
    {
        auto [it, isNew] = m_tracks.try_emplace(key);
        auto& track = it->second;
        if (isNew)
        {
            track.senderTime = senderMs / 1000.0;
            track.offset = now - track.senderTime;
        }
        else
        {
            // Unsigned differences, so the counters may wrap.
            if (static_cast<int32_t>(sequence - track.lastSequence) <= 0)
            {
                return false;
            }
            track.senderTime += static_cast<int32_t>(senderMs - track.lastSenderMs) / 1000.0;
            track.offset = std::min(now - track.senderTime, track.offset + ClockRelax * (now - track.lastPushAt));
        }
        track.lastSequence = sequence;
        track.lastSenderMs = senderMs;
        track.lastPushAt = now;

        // Usually the newest; a later sequence with an earlier time (the owner's clock jumped) goes in order.
        auto at = track.snapshots.end();
        while (at != track.snapshots.begin() && std::prev(at)->time > track.senderTime)
        {
            --at;
        }
        track.snapshots.insert(at, {track.senderTime, sample});
        while (track.snapshots.size() > MaxSnapshots)
        {
            track.snapshots.pop_front();
        }
        return true;
    }

    /// Where the character is to be shown now; nothing before its first update.
    std::optional<MotionSample> Sample(const Key& key, const double now)
    {
        const auto it = m_tracks.find(key);
        if (it == m_tracks.end() || it->second.snapshots.empty())
        {
            return std::nullopt;
        }

        auto& snapshots = it->second.snapshots;
        const auto t = now - it->second.offset - Delay;
        while (snapshots.size() >= 2 && snapshots[1].time <= t)
        {
            snapshots.pop_front();
        }

        const auto& from = snapshots.front();
        if (snapshots.size() == 1 || t <= from.time)
        {
            // Nothing newer yet (or the first update): stay there rather than guess ahead.
            return from.sample;
        }

        const auto& to = snapshots[1];
        const auto f = static_cast<float>((t - from.time) / std::max(1e-6, to.time - from.time));
        MotionSample result = f < 0.5f ? from.sample : to.sample;
        result.x = from.sample.x + (to.sample.x - from.sample.x) * f;
        result.y = from.sample.y + (to.sample.y - from.sample.y) * f;
        result.z = from.sample.z + (to.sample.z - from.sample.z) * f;
        result.yaw = from.sample.yaw + ShortestTurn(from.sample.yaw, to.sample.yaw) * f;
        result.speed = from.sample.speed + (to.sample.speed - from.sample.speed) * f;
        const auto dt = static_cast<float>(std::max(1e-3, to.time - from.time));
        result.vx = (to.sample.x - from.sample.x) / dt;
        result.vy = (to.sample.y - from.sample.y) / dt;
        return result;
    }

    void Forget(const Key& key) { m_tracks.erase(key); }

    template <class F>
    void ForEachKey(F&& f) const
    {
        for (const auto& [key, track] : m_tracks)
        {
            f(key);
        }
    }

    /// The signed turn in degrees from one yaw to another, the short way round.
    static float ShortestTurn(const float from, const float to)
    {
        auto turn = std::fmod(to - from, 360.0f);
        if (turn > 180.0f)
        {
            turn -= 360.0f;
        }
        else if (turn < -180.0f)
        {
            turn += 360.0f;
        }
        return turn;
    }

private:
    struct Snapshot
    {
        double time; // the owner's clock, seconds
        MotionSample sample;
    };

    struct Track
    {
        uint32_t lastSequence = 0;
        uint32_t lastSenderMs = 0;
        double senderTime = 0.0; // the owner's clock at the last update, seconds, unwrapped
        double offset = 0.0;     // local clock minus the owner's clock, as small as seen
        double lastPushAt = 0.0;
        std::deque<Snapshot> snapshots;
    };

    std::map<Key, Track> m_tracks;
};
} // namespace Cyberverse::PlayerSync
