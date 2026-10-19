# Управление мощностью в форке: исследование штатного TDP API

> **Исследование, не реализованная функция.** Снимок на 2026-10-19, Xiaomi Book Pro 14 / TM2424.
> Найден штатный Intel IPF user-mode кандидат для PL1/PL2. Работоспособность и безопасность
> установки ватт на этом ноутбуке **не проверены**. Ползунок или ваттные пресеты пока не обоснованы.

## 1. Область проверки

Прочитаны [правила проекта](../CLAUDE.md), [протокол](01-wmi-protocol.md),
[история TDP-вопроса](05-open-questions.md#L125-L179), [описание OEM-служб](17-oem-services.md).
Проверены только регистрация служб, установочные пути, INF, PE imports/exports, машинные байты,
встроенные строки/схемы и общедоступные исходники Intel. Приватные логи и пользовательские данные
не исследовались; серийные номера и аккаунты для выводов не нужны.

Не выполнялись запуск OEM-программ, загрузка исследуемых DLL (`LoadLibrary`), WMI invoke,
подключение/handshake к IPC, аппаратные GET/SET, EC/MSR, установка драйверов, изменение служб,
реестра, конфигураций или сборка приложения. Все перечисленные ниже возможности — статические.

## 2. TDP: почему первоначального отказа MIFS недостаточно

[Предыдущие пробы](05-open-questions.md#L125-L179) на TM2424 получили `0xE0` для `0x16–0x18`,
включая Tongfang-команду «CPU power» `0x17`. Это отрицательный результат **для исследованного
MIFS-канала и аргументов**, не доказательство отсутствия иных штатных интерфейсов.
Сами записи признают GET-only свип неисчерпывающим; сырого локального журнала в checkout нет.

[WMI-клиент](../src/Wmi/IMifsClient.cs) предоставляет режимы и заряд, но не setter ватт.
[Чтение мощности адаптера](../src/Wmi/MifsClient.cs#L155-L164) — согласованная PD-мощность БП,
не CPU power limit. Режимы `0x08`, CPU%, паспортный TDP и текущие измеренные ватты не взаимозаменяемы.

[Позднее описание MiSmartTuning](17-oem-services.md#L58-L61) сообщает об изменении PL1/ODVP,
но не раскрывает контракт. На исследованной системе MiService/MiDeviceService не зарегистрированы,
обычных каталогов Xiaomi/Timi Personal Computing в Program Files не найдено. Поэтому именно
Xiaomi-обвязка MiSmartTuning статически не проверена; её отсутствие здесь не означает отсутствие в OEM-пакетах.

## 3. Найденные штатные Intel-компоненты

Службы `ipfsvc` и `dptftcs` на момент чтения работали. Их ImagePath ведут в пакеты DriverStore:

```text
IPF = %SystemRoot%\System32\DriverStore\FileRepository\ipf_cpu.inf_amd64_39a837658d091e72
DTT = %SystemRoot%\System32\DriverStore\FileRepository\dtt_sw.inf_amd64_27c1316661666cc3
ipfsvc  -> IPF\ipf_uf.exe     IPF package 2.3.20201.4882
 dptftcs -> DTT\ipfsvc.exe    DTT package 9.1.10003.555
```

В IPF INF строки 358–373 и 427–429 регистрируют клиентский интерфейс
`{9DD57132-5929-476A-8CCB-9D42B1E4ABED}` с InstallDir и провайдерский
`{73FDD548-3C00-4279-B021-D0655F0B1E41}` с Core Provider.
DTT INF строки 211–234 задают `pipe://ipfsrv.dptftcs` и `pipe://ipfsrv.dptf`,
335–336 — DTT/APO providers. Эти пути и provider packages подтверждены чтением соответствующих
DeviceClasses → Device Parameters в реестре: это не только декларации установщика.

Идентичность ключевых IPF-модулей (все FileVersion/ProductVersion `2.3.20201.4882`):

```text
Module                Bytes    SHA256
CoreProvider.dll      2114512  093e8c492087502b8973a0388aa658030e03807659787f61372bdea2d3cccdbb
ClientApiProxy.dll     697808  647eb7d6c7c08762cbb4e53290e49eba3a98664a01710f2c222444a50fc1111e
CoreApiProxy.dll       354776  373646952a0397ab7113639a3ad4d4626c2aafa7657ce3a11d591e2dd486b50d
ipfsrv.dll            913360  a67937a4f9eac8f0dd6876804f8af98a246415711e59eb335bd3a7509bab50be
ipfipc.dll            713680  dd5c24af3f77a37d18ab34cfd424fb67ec5cdd202d07377f20492a6d747bc9b2
```

## 4. Статически найденный контракт

Все offsets ниже — **файловые смещения**, не адреса записи и не runtime RVA.
В Core Provider manifest по `0x179940` есть `Intel.Ipf.SocPower`, unique_id
`083C76C9-6EF7-4570-8872-7F55188FF2A1`, а также `Intel.Ipf.SystemPower`.
Это имена providers, **не подтверждённые runtime node paths**.

Встроенная JSON-схема мощности по `0x181A90` описывает:

| Поле | Тип / единицы | Ограничение схемы |
|---|---|---|
| `PL1.Limit`, `PL2.Limit` | integer или null, **мВт** | minimum 0, без readOnly |
| `PL1.TimeWindow` | integer или null, мс | minimum 0 |
| `PL1.Enable`, `PL2.Enable` | Disabled / Enabled / null | сначала Limit, затем Enable |
| `PL4.Limit` | integer или null, мВт | сброс к BIOS default после hibernation |
| `ThermalDesignPower` | integer или null, мВт | **readOnly**, не устанавливаемый PL |
| `PowerLimitLock` | integer или null | readOnly; BIOS lock делает настройки read-only |
| `PowerLimitEnable` | boolean или null | readOnly; признак программируемости Turbo limits |

Вторая platform power схема с PL1/PL2 расположена по `0x185020`.
Наличие nullable-поля допускает отсутствие данных. `minimum: 0` **не является безопасным
минимумом для UI**; максимумы, шаги и допустимые сочетания PL1/PL2 здесь не заданы.

Client Proxy exports: ClientApiCreate/Destroy/Revision и варианты R4/R5; код возвращает
ревизии 3/4/5. Строки GetSchema/GetValue/GetCapabilities/SetValue лежат около `0x7DD90–0x7E088`.
Core Proxy exports CoreApiCreate/Destroy и R2, содержит GetPrimitive/SetPrimitive и адрес
`pipe://ipfsrv.elevated` по `0x42558`. Это C++ interfaces, не готовый C# SetTdpWatts.
**Публичные high-level Client ABI headers rev3/4/5 и полный контракт вызова не найдены.**

## 5. Elevated IPC: больше, чем строки enum

В серверном модуле таблица AccessDef по `0xCB4A0` связывает public endpoint с ролью `0xA0002`
и Everyone; по `0xCB4C8` elevated endpoint — с ролью `0xA0003` и **Administrators**, appKey null.
Это штатная роль; подмена DPTF/TCS/XTU, обход ACL или отключение Intel-защиты не рассматриваются.

Public primitive ACL по `0xCB700` содержит 130 permit/id записей: GET limit есть, SET limit нет.
Elevated ACL по `0xCBDE0` содержит 163 записи, в том числе permit=1 для:

| ID | Операция | Offset записи |
|---|---|---|
| 38 | GET_RAPL_POWER_LIMIT | `0xCC110` |
| 172 | GET_RAPL_POWER_LIMIT_LOCK | `0xCC128` |
| 334 | SET_PLATFORM_POWER_LIMIT | `0xCC280` |
| 130 | SET_RAPL_POWER_LIMIT | `0xCC2B0` |
| 222 | SET_RAPL_POWER_LIMIT_ENABLE | `0xCC2C0` |
| 340 | SET_RAPL_POWER_LIMIT_TIME_WINDOW | `0xCC2C8` |

Связь ACL с ролями проверена кодовыми ссылками: public name load `0x1C6A1` → ACL LEA `0x1C6BF`;
elevated name load `0x1C6F4` → ACL LEA `0x1C712`. Это не вывод лишь из общего списка primitive names.
Наличие GET-контракта даёт **кандидат для readback**, а не уже выполненную проверку записи.

## 6. Публичные первоисточники и совместимость

Ссылки закреплены на Intel commit `468699545442788999071d0a8ccb71b35332cdf1`:

- [Auth manager:336–412](https://github.com/intel/dptf/blob/468699545442788999071d0a8ccb71b35332cdf1/IPF/Sources/ipfsrv/ipfsrv_authmgr.c#L336-L412): elevated GET/SET ACL, сопоставленный с локальным Windows бинарником.
- [Primitive IDs](https://github.com/intel/dptf/blob/468699545442788999071d0a8ccb71b35332cdf1/Common/esif_sdk_primitive_type.h): GET limit 38, SET limit 130, SET platform limit 334.
- [Public IPC ABI:25–70](https://github.com/intel/dptf/blob/468699545442788999071d0a8ccb71b35332cdf1/IPF/Sources/Common/ipf_ipc_iface.h#L25-L70): GetIpcInterface, packed rev1, Init/SetAppIface/SessionCreate/Connect/Destroy.
- [Session info и endpoints:35–68](https://github.com/intel/dptf/blob/468699545442788999071d0a8ccb71b35332cdf1/IPF/Sources/Common/ipf_core_api.h#L35-L68); [primitive RPC:590–603](https://github.com/intel/dptf/blob/468699545442788999071d0a8ccb71b35332cdf1/IPF/Sources/Common/ipf_ipc_codec.h#L590-L603).
- [README](https://github.com/intel/dptf/blob/468699545442788999071d0a8ccb71b35332cdf1/README.txt): Chromium/Linux-oriented проект архивирован; Intel не гарантирует дальнейшую поддержку.

Локальный IPC export по `0x72B0` статически проверяет iface type=8 и принимает
rev1/size `0x84` либо rev2/size `0x8C`. Это согласуется с открытым low-level rev1,
но **не доказывает полную ABI-совместимость и поддержку стороннего Windows клиента**.

## 7. Что отделяет находку от безопасной интеграции

1. Не проверены живой elevated listener, подключение и runtime capability/namespace.
2. Не восстановлен/не подтверждён high-level C++ ABI; low-level контракт требует сверки версий и lifecycle.
3. Не прочитаны реальные participant/domain/instance, BIOS locks, текущие и эффективные PL1/PL2.
4. Нет подтверждённых per-model/AC/DC bounds, шага и безопасных сочетаний; schema bounds недостаточны.
5. Не проверены DTT/firmware arbitration, удержание лимита, сон, смена питания, режимы и откат.
6. SET→GET, физическое действие и температурная безопасность не проверялись и не входят в read-only этап.

**Итог:** найден конкретный штатный admin user-mode IPF-кандидат; утверждение «единственный путь —
наш kernel-driver» не доказано. Использование уже установленного Intel API потенциально сохраняет
принцип driver-free, но **работающий безопасный watt control пока не установлен**. До подтверждения
ABI, capabilities, readback, bounds и locks не добавляем фиктивный UI и не обещаем установку ватт.
