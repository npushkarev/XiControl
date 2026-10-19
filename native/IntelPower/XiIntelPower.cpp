// SPDX-License-Identifier: GPL-3.0-only
// Узкий user-mode мост к уже установленному Intel IPF. Не kernel-драйвер.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include "XiIntelPower.h"
#include "vendor/intel-ipf/ClientApiProxy.h"
#include "vendor/intel-ipf/DllProxy.h"

#include <cstring>
#include <filesystem>
#include <limits>
#include <memory>
#include <mutex>
#include <stdexcept>
#include <string>

#if !defined(_WIN64) || !defined(_MSC_VER)
#error XiIntelPower requires the x64 MSVC ABI.
#endif
#if !defined(NDEBUG) || defined(_DEBUG) || !defined(_DLL) || _ITERATOR_DEBUG_LEVEL != 0
#error Release /MD and iterator debug level 0 are required; signature verification must stay enabled.
#endif
#ifdef IPF_BUILD_DEBUG
#error IPF debug loading and signature bypass are not allowed.
#endif
static_assert(sizeof(void*) == 8, "x64 only");

namespace {
constexpr int32_t Success = 0;
constexpr int32_t Error = 1;
constexpr int32_t BufferRequired = 2;
constexpr char PowerPath[] = "Platform.SOC.Power";
thread_local char lastError[2048] = {};

void SetError(const char* text) noexcept
{
    strncpy_s(lastError, sizeof(lastError), text ? text : "Unknown native error", _TRUNCATE);
}

void Win32Error(const char* operation)
{
    const DWORD error = GetLastError();
    throw std::runtime_error(std::string(operation) + " failed (Win32 " + std::to_string(error) + ")");
}

// Дескриптор удерживается между проверкой подписи и LoadLibrary: путь нельзя
// подменить записью/переименованием. В installed DriverStore пишут только админы.
struct FileGuard {
    HANDLE handle = INVALID_HANDLE_VALUE;
    ~FileGuard() noexcept { if (handle != INVALID_HANDLE_VALUE) CloseHandle(handle); }
    FileGuard() = default;
    FileGuard(const FileGuard&) = delete;
    FileGuard& operator=(const FileGuard&) = delete;
};

std::filesystem::path ValidateInstalledPath(const wchar_t* input)
{
    if (!input || !*input) throw std::invalid_argument("Intel proxy path is empty");
    const std::filesystem::path supplied(input);
    const auto root = supplied.root_name().wstring();
    if (!supplied.is_absolute() || root.size() != 2 || root[1] != L':')
        throw std::invalid_argument("Intel proxy path must be an absolute local drive path");
    const auto resolved = std::filesystem::canonical(supplied);
    if (_wcsicmp(resolved.filename().c_str(), L"ClientApiProxy.dll") != 0)
        throw std::invalid_argument("Only the installed ClientApiProxy.dll may be loaded");

    wchar_t windows[MAX_PATH] = {};
    const UINT count = GetWindowsDirectoryW(windows, MAX_PATH);
    if (!count || count >= MAX_PATH) Win32Error("GetWindowsDirectoryW");
    const auto driverStore = std::filesystem::canonical(
        std::filesystem::path(windows) / L"System32" / L"DriverStore" / L"FileRepository");
    const auto prefix = driverStore.wstring() + L"\\";
    const auto full = resolved.wstring();
    if (full.size() <= prefix.size() || _wcsnicmp(full.c_str(), prefix.c_str(), prefix.size()) != 0)
        throw std::invalid_argument("Intel proxy must reside in the installed Windows DriverStore");
    return resolved;
}

struct Session {
    // Последним уничтожается loader: исключение и Intel object не переживают DLL.
    DllProxy<Ipf::Proxy::ClientApiR5> library{"ClientApiR5"};
    Ipf::Proxy::ClientApiR5* client = nullptr;
    std::mutex mutex;

    void Destroy() noexcept
    {
        if (!client) return;
        auto* owned = client;
        client = nullptr;
        try { library.DestroyInstance(owned); }
        catch (const std::exception& ex) { SetError(ex.what()); }
        catch (...) { SetError("Unknown exception while closing Intel IPF session"); }
    }
    ~Session() noexcept { Destroy(); }
};

int32_t CopyUtf8(const std::string& value, char* buffer, uint32_t capacity, uint32_t* needed)
{
    if (value.size() >= std::numeric_limits<uint32_t>::max())
        throw std::length_error("Intel JSON response exceeds the C ABI size limit");
    *needed = static_cast<uint32_t>(value.size() + 1);
    if (!buffer || capacity < *needed) return BufferRequired;
    memcpy(buffer, value.c_str(), *needed);
    return Success;
}

template <typename Getter>
int32_t GetJson(void* handle, char* buffer, uint32_t capacity, uint32_t* needed, Getter getter) noexcept
{
    SetError("");
    if (needed) *needed = 0;
    if (!handle || !needed || (!buffer && capacity)) {
        SetError("Invalid session or output buffer");
        return Error;
    }
    auto* state = static_cast<Session*>(handle);
    try {
        std::lock_guard<std::mutex> lock(state->mutex);
        if (!state->client) throw std::runtime_error("Intel IPF session is closed");
        return CopyUtf8(getter(*state->client), buffer, capacity, needed);
    }
    catch (const std::exception& ex) { SetError(ex.what()); }
    catch (...) { SetError("Unknown exception while reading Intel IPF"); }
    return Error;
}

bool IsAdministrator()
{
    BYTE sid[SECURITY_MAX_SID_SIZE] = {};
    DWORD size = sizeof(sid);
    if (!CreateWellKnownSid(WinBuiltinAdministratorsSid, nullptr, sid, &size))
        Win32Error("CreateWellKnownSid");
    BOOL member = FALSE;
    // У medium UAC-токена Administrators — deny-only; это НЕ проходит проверку.
    if (!CheckTokenMembership(nullptr, sid, &member)) Win32Error("CheckTokenMembership");
    return member != FALSE;
}
} // namespace

int32_t __cdecl XiPowerOpen(const wchar_t* path, void** session) noexcept
{
    SetError("");
    if (!session) { SetError("Session output is null"); return Error; }
    *session = nullptr;
    // Владелец находится ВНЕ try: vendor exception уничтожится до выгрузки DLL.
    std::unique_ptr<Session> pending;
    try {
        const auto installed = ValidateInstalledPath(path);
        FileGuard file;
        file.handle = CreateFileW(installed.c_str(), GENERIC_READ, FILE_SHARE_READ,
            nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file.handle == INVALID_HANDLE_VALUE) Win32Error("Open installed Intel proxy");
        pending = std::make_unique<Session>();
        const int error = pending->library.Open(installed);
        if (error) throw std::runtime_error("Intel ClientApiR5 load failed (errno " + std::to_string(error) + ")");
        if (pending->library.GetRevision() != 5)
            throw std::runtime_error("Intel ClientApiR5 revision is not 5");
        pending->client = pending->library.CreateInstance(std::string("{}"));
        if (!pending->client) throw std::runtime_error("Intel ClientApiR5Create returned null");
        *session = pending.release();
        return Success;
    }
    catch (const std::exception& ex) { SetError(ex.what()); }
    catch (...) { SetError("Unknown exception while opening Intel IPF"); }
    return Error;
}

int32_t __cdecl XiPowerRead(void* session, char* utf8, uint32_t capacity, uint32_t* needed) noexcept
{
    return GetJson(session, utf8, capacity, needed,
        [](auto& api) { return api.GetNode(PowerPath); });
}

int32_t __cdecl XiPowerSchema(void* session, char* utf8, uint32_t capacity, uint32_t* needed) noexcept
{
    return GetJson(session, utf8, capacity, needed,
        [](auto& api) { return api.GetSchema(PowerPath, Ipf::SchemaType::DATA); });
}

int32_t __cdecl XiPowerSetLimit(void* session, int32_t index, int64_t milliwatts) noexcept
{
    SetError("");
    if (!session || (index != 1 && index != 2) || milliwatts < 1000 || milliwatts > 250000) {
        SetError("Invalid PL index or transport range (1000..250000 mW)");
        return Error;
    }
    auto* state = static_cast<Session*>(session);
    try {
        if (!IsAdministrator()) throw std::runtime_error("An elevated administrator token is required for SET");
        std::lock_guard<std::mutex> lock(state->mutex);
        if (!state->client) throw std::runtime_error("Intel IPF session is closed");
        // Не даём вызывающему коду произвольный namespace, primitive или команду.
        state->client->SetValue(index == 1 ? "Platform.SOC.Power.PL1.Limit" : "Platform.SOC.Power.PL2.Limit",
            milliwatts);
        return Success;
    }
    catch (const std::exception& ex) { SetError(ex.what()); }
    catch (...) { SetError("Unknown exception while setting Intel IPF power limit"); }
    return Error;
}

void __cdecl XiPowerClose(void* session) noexcept
{
    SetError("");
    if (!session) return;
    auto* state = static_cast<Session*>(session);
    try {
        { std::lock_guard<std::mutex> lock(state->mutex); state->Destroy(); }
        delete state;
    }
    catch (const std::exception& ex) { SetError(ex.what()); }
    catch (...) { SetError("Unknown exception while closing Intel IPF session"); }
}

const char* __cdecl XiPowerLastError(void) noexcept { return lastError; }
