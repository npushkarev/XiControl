# XiIntelPower — узкий мост к установленному Intel IPF

x64 C ABI для C#; это **user-mode DLL, не kernel-драйвер**. Intel/OEM binaries не входят
в поставку. Сам bridge не получает подпись Microsoft от того, что вызывает подписанный Intel API.

## Сборка (без загрузки DLL и без обращения к железу)

Нужны лицензированный Visual Studio C++ toolchain (VS2022 или новее), x64 MSVC и Windows SDK.
WDK и установка драйвера **не нужны**. MinGW несовместим с MSVC `std::string`/exception ABI vendor DLL.
Официальный Intel SDK поддерживает MSVC; clang-cl отдельно не проверен.

Из workspace root, передав отдельный каталог артефактов:

```powershell
powershell -NoProfile -File source/native/IntelPower/build.ps1 -OutputDirectory .tools/native-build
```

Скрипт находит `vswhere`/`vcvars64`, собирает Release `/MD /DNDEBUG /O2 /EHsc /std:c++17`,
`_ITERATOR_DEBUG_LEVEL=0`, stack checks, CFG, NX, ASLR, CET. Выход — `XiIntelPower.dll`,
import library и object в выбранном каталоге **вне source**. Он ничего не устанавливает,
не подписывает, не загружает и не запускает Intel binaries.

## C ABI

Объявления находятся в `XiIntelPower.h`; x64 Windows calling convention, `int32_t` status:
`0` — успех; `1` — ошибка; `2` — нужен буфер. `needed` включает завершающий NUL.
JSON — UTF-8, его структуру и nullable-поля проверяет C#.

- `XiPowerOpen(const wchar_t* path, void** session)` — абсолютный установленный
  `ClientApiProxy.dll` из Windows DriverStore; путь получает managed caller через штатный
  HKLM/PnP install interface. C++ проверяет canonical local path/filename/DriverStore,
  держит read handle от подмены файла во время проверки и загрузки, проверяет WinVerifyTrust,
  release/dynamic CRT build flags и точную ClientApiR5 revision `5`. Factory config — `{}`.
- `XiPowerRead(session, buffer, capacity, needed)` — только `GetNode("Platform.SOC.Power")`.
- `XiPowerSchema(session, buffer, capacity, needed)` — `GetSchema` того же node, `DATA`.
- `XiPowerSetLimit(session, index, milliwatts)` — **только** `PL1.Limit`/`PL2.Limit`,
  index `1`/`2`, целые милливатты `1000..250000`, только elevated Administrators token.
  Это транспортный guard, **не безопасные per-model границы**. Никаких `Enable`,
  `TimeWindow`, `PL4`, lock, arbitrary primitive/path/command.
- `XiPowerClose(session)` — уничтожает объект через matching vendor Destroy, затем выгружает DLL.
- `XiPowerLastError()` — TLS `const char*`; managed caller немедленно копирует UTF-8 до следующего
  вызова на том же потоке. Bridge копирует `exception.what()` пока Intel DLL ещё загружена;
  исключения не пересекают C ABI.

Чтение/запись сериализованы mutex внутри session. Caller владеет валидным opaque session,
не передаёт произвольные/stale pointers, делает Close ровно один раз после завершения всех calls
(например, SafeHandle). Sizing-GET выполняется повторно при вызове с реальным буфером; размер
может измениться, поэтому caller должен ограниченно повторять при status `2`.

## Границы безопасности и интеграции

- В исследовательском этапе **не вызывать setter**. Сам факт успешного GET/revision/Lock=0 не
  доказывает безопасную запись. Managed policy обязана проверять реальный node/schema/capabilities,
  firmware locks, модель, AC/DC bounds, PL1/PL2 relation, не повышать выбранный baseline, проверять
  readback, предусматривать восстановление. Не отключать HVCI/Defender/signature verification.
- Native проверка диапазона только ограничивает неверные значения; она не обещает, что Intel
  разрешит SET и что прошивка/DTT не переустановит лимит.
- Bridge загружать по абсолютному доверенному appdir path, не из CWD/PATH/%TEMP%; не вводить
  глобальный поиск в user-writable каталоге. Vendor загружается абсолютным Unicode path
  `LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32`. Не копировать vendor DLL в appdir.
- На установленном 2.3.20201.4882 есть R5, но нет R6. Поэтому здесь использованы официальные
  `ClientApiR5` types/factory; текущий upstream public thin loader запрашивает R6 и не является fallback.
- `/MD` требует совместимого VC++ x64 release Redistributable, не старее toolset bridge.
  При сборке VS2026 это может быть более новая версия, чем у приложения/OEM. Не копировать
  произвольные DLL из System32/VS. Microsoft redist может распространять лицензированный VS user
  только по Microsoft Software License Terms/REDIST list; prefer официальный prerequisite.

## Происхождение и лицензии

Собственный bridge — GPL-3.0-only как проект. `vendor/intel-ipf` — **Apache-2.0**, Intel
Copyright (c) 2020–2026. Внутри — 12 заголовков из официального
[репозитория intel/ipf](https://github.com/intel/ipf/tree/25a8a441884d060287a96a6ffebd2b90c9fa9ee9),
commit `25a8a441884d060287a96a6ffebd2b90c9fa9ee9` (текущий SDK семейства 2.4), и полный
`LICENSE.txt`. ABI `ClientApiProxy.h`, `IClientApiR5.h` и общие ABI types неизменны.
`DllProxy.h` локально изменён (помечено в файле): Unicode/restricted loader,
release/runtime flags comparison, dynamic signature pathname вместо fixed-size MAX_PATH.
Все original copyright/license notices сохраняются; upstream NOTICE в этом snapshot отсутствует.
Apache-2.0 совместим с GPLv3, но **не предоставляет лицензии redistribution для закрытых
установленных Intel/OEM proxy/provider/driver binaries**. Они не vendored. JSON dependency не нужна:
официальные `Exceptions.h`, `ClientApi.cpp` и JSON C wrapper здесь не используются.

Ссылки:

- [Intel README: Windows11 23H2+, VS2022, source SDK](https://github.com/intel/ipf/blob/25a8a441884d060287a96a6ffebd2b90c9fa9ee9/README.md).
- [Official R5 class/factory ABI](https://github.com/intel/ipf/blob/25a8a441884d060287a96a6ffebd2b90c9fa9ee9/IPF_EF/src/clientapi/ClientApiProxy.h).
- [Official DllProxy signature/build flags](https://github.com/intel/ipf/blob/25a8a441884d060287a96a6ffebd2b90c9fa9ee9/IPF_EF/src/shared/DllProxy.h).
- [Apache-2.0 license](https://github.com/intel/ipf/blob/25a8a441884d060287a96a6ffebd2b90c9fa9ee9/LICENSE.txt).
- [Microsoft /MD](https://learn.microsoft.com/en-us/cpp/build/reference/md-mt-ld-use-run-time-library).
- [MSVC binary compatibility 2015–2026](https://learn.microsoft.com/en-us/cpp/porting/binary-compat-2015-2017).
- [VC++ redistribution licensing](https://learn.microsoft.com/en-us/cpp/windows/redistributing-visual-cpp-files).
- [Native DLL security](https://learn.microsoft.com/en-us/windows/win32/dlls/dynamic-link-library-security).
