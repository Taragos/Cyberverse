#pragma once

#include <cstdint>

/// A received message as the network delivered it: the 4 byte frame header and the packet. The native layer doesn't
/// look inside; C# reads it (server/Managed/Protocol). data stays valid until message_release(handle).
/// An empty poll result has handle == nullptr.
struct Message {
    const uint8_t* data = nullptr;
    void* handle = nullptr;
    uint32_t size = 0;
    uint32_t connectionId = 0;
};
