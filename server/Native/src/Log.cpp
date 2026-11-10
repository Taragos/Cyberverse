#include "Log.h"

#include <atomic>
#include <cstdarg>
#include <cstdio>

// GameNetworkingSockets may log from its own thread, so the callback is read atomically.
static std::atomic<LogCallback> g_logCallback{nullptr};

void SetLogCallback(const LogCallback callback)
{
    g_logCallback.store(callback);
}

void Log(const LogLevel level, const char* format, ...)
{
    char message[1024];
    va_list args;
    va_start(args, format);
    vsnprintf(message, sizeof(message), format, args);
    va_end(args);

    if (const auto callback = g_logCallback.load(); callback != nullptr)
    {
        callback(static_cast<int32_t>(level), message);
        return;
    }

    const auto stream = level >= LogLevel::Warn ? stderr : stdout;
    fprintf(stream, "%s\n", message);
    fflush(stream); // stdout is fully buffered when piped (Docker, CI)
}
