#pragma once
// Where the client keeps the session tokens the server hands out after a browser login (CYB-00008): one file per
// server, %LOCALAPPDATA%\Cyberverse\sessions\<host>_<port>.token. A token logs this PC in without the browser until
// it expires (30 days unused) or is revoked; deleting the file only means logging in in the browser again.
#include <windows.h>

#include <cctype>
#include <cstdint>
#include <filesystem>
#include <fstream>
#include <string>
#include <system_error>

namespace SessionStore
{
inline std::filesystem::path PathFor(const std::string& host, uint16_t port)
{
    wchar_t base[MAX_PATH];
    const auto length = GetEnvironmentVariableW(L"LOCALAPPDATA", base, MAX_PATH);
    const auto dir = length > 0 && length < MAX_PATH ? std::filesystem::path(base) : std::filesystem::temp_directory_path();

    auto name = host + "_" + std::to_string(port);
    for (auto& c : name)
    {
        if (!std::isalnum(static_cast<unsigned char>(c)) && c != '.' && c != '-')
        {
            c = '_';
        }
    }

    return dir / "Cyberverse" / "sessions" / (name + ".token");
}

/// The saved token, or "" when there is none.
inline std::string Load(const std::filesystem::path& path)
{
    std::ifstream file(path);
    std::string token;
    std::getline(file, token);
    return token;
}

inline bool Save(const std::filesystem::path& path, const std::string& token)
{
    std::error_code error;
    std::filesystem::create_directories(path.parent_path(), error);
    std::ofstream file(path, std::ios::trunc);
    file << token;
    return file.good();
}
} // namespace SessionStore
