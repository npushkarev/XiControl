// SPDX-License-Identifier: GPL-3.0-only
#pragma once

#include <stdint.h>
#include <wchar.h>

#if defined(XI_INTEL_POWER_EXPORTS)
#define XI_POWER_API __declspec(dllexport)
#else
#define XI_POWER_API __declspec(dllimport)
#endif
#ifdef __cplusplus
#define XI_POWER_NOEXCEPT noexcept
extern "C" {
#else
#define XI_POWER_NOEXCEPT
#endif

// Только x64: 0 = успех, 1 = ошибка, 2 = нужен буфер (*needed включает NUL).
// path: установленный подписанный ClientApiProxy.dll из Windows DriverStore.
XI_POWER_API int32_t __cdecl XiPowerOpen(const wchar_t* path, void** session) XI_POWER_NOEXCEPT;
XI_POWER_API int32_t __cdecl XiPowerRead(void* session, char* utf8, uint32_t capacity,
    uint32_t* needed) XI_POWER_NOEXCEPT;
XI_POWER_API int32_t __cdecl XiPowerSchema(void* session, char* utf8, uint32_t capacity,
    uint32_t* needed) XI_POWER_NOEXCEPT;
// Это транспортный предел, НЕ подтверждённый безопасный диапазон модели.
// Caller обязан проверить capabilities/locks/bounds/readback и иметь путь отката.
XI_POWER_API int32_t __cdecl XiPowerSetLimit(void* session, int32_t index,
    int64_t milliwatts) XI_POWER_NOEXCEPT;
// Close не должен пересекаться с другими вызовами; в C# использовать SafeHandle.
XI_POWER_API void __cdecl XiPowerClose(void* session) XI_POWER_NOEXCEPT;
// Строка принадлежит TLS; немедленно скопировать на вызывающей стороне.
XI_POWER_API const char* __cdecl XiPowerLastError(void) XI_POWER_NOEXCEPT;

#ifdef __cplusplus
}
#endif
