#pragma once

#include <cstdint>

// Native log lines go to the callback C# installs (server_set_log_callback), so they end up in the server's log file.
// Without a callback they go to stdout (Debug, Info) or stderr (Warn, Error).
enum class LogLevel : int32_t
{
    Debug = 1,
    Info = 2,
    Warn = 3,
    Error = 4,
};

using LogCallback = void (*)(int32_t level, const char* message);

void SetLogCallback(LogCallback callback);

#if defined(__GNUC__) || defined(__clang__)
__attribute__((format(printf, 2, 3)))
#endif
void Log(LogLevel level, const char* format, ...);
