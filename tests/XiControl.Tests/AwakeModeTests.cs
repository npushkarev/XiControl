using System.Text.Json;
using FluentAssertions;
using XiControl.Config;
using XiControl.SystemIntegration;
using Xunit;

namespace XiControl.Tests;

/// <summary>
/// AwakeMode: независимые AC/DC, возврат в исходную схему и откат при любом сбое включения.
/// Только фейки плана, запроса питания и persistence — без железа, реестра и файлов конфига.
/// </summary>
public sealed class AwakeModeTests
{
    public AwakeModeTests() => Log.Enabled = false; // сбои пишут в лог — не сорим в реальный файл

    private static readonly Guid Balanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    private static readonly Guid HighPerf = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    private const uint Sleep = 1, Hibernate = 2;

    private sealed class FakePlan : AwakeMode.IPowerPlan
    {
        public Guid Active = Balanced;
        public readonly Dictionary<Guid, uint> LidAc = new() { [Balanced] = Sleep, [HighPerf] = Sleep };
        public readonly Dictionary<Guid, uint> LidDc = new() { [Balanced] = Hibernate, [HighPerf] = Hibernate };
        public readonly List<(string Source, Guid Scheme)> Reads = [];
        public readonly List<(string Source, Guid Scheme, uint Value)> Writes = [];
        public readonly List<Guid> Sets = [];
        public int ActiveReads;
        public bool FailActive, FailRead, FailReadDc, FailWrite, FailWriteAc, FailWriteDc, FailSet;
        public Func<string, uint, bool>? WriteResult;
        public Action? BeforeWrite;

        public bool TryGetActive(out Guid scheme) { ActiveReads++; scheme = Active; return !FailActive; }
        public bool TryReadLidAc(Guid scheme, out uint value)
        {
            Reads.Add(("AC", scheme));
            return LidAc.TryGetValue(scheme, out value) && !FailRead;
        }
        public bool TryReadLidDc(Guid scheme, out uint value)
        {
            Reads.Add(("DC", scheme));
            return LidDc.TryGetValue(scheme, out value) && !FailReadDc;
        }
        public bool WriteLidAc(Guid scheme, uint value) => Write("AC", scheme, value, LidAc, FailWriteAc);
        public bool WriteLidDc(Guid scheme, uint value) => Write("DC", scheme, value, LidDc, FailWriteDc);
        private bool Write(string source, Guid scheme, uint value, Dictionary<Guid, uint> values, bool fail)
        {
            Writes.Add((source, scheme, value));
            BeforeWrite?.Invoke();
            if (FailWrite || fail || WriteResult?.Invoke(source, value) == false) return false;
            values[scheme] = value;
            return true;
        }
        public bool SetActive(Guid scheme) { Sets.Add(scheme); return !FailSet; }
    }

    private sealed class FakeAwake : AwakeMode.IKeepAwake
    {
        public bool Refuse, ThrowOnHold, ThrowOnRelease;
        public bool Held;
        public bool? Display;

        public bool Hold(bool display)
        {
            // Отказ намеренно не снимает прежнее удержание: cleanup обязан сделать вызывающий.
            if (ThrowOnHold) throw new InvalidOperationException("запрос питания недоступен");
            if (Refuse) return false;
            Held = true;
            Display = display;
            return true;
        }

        public void Release()
        {
            if (ThrowOnRelease) throw new InvalidOperationException("запрос питания недоступен");
            Held = false;
        }
    }

    private sealed record LidBackup(int? Ac, int? Dc, Guid? Scheme);

    private sealed class SpyStore : IConfigStore
    {
        public readonly List<LidBackup> Saved = [];
        public AppConfig Load() => throw new NotSupportedException();
        public void Save(AppConfig cfg) => Saved.Add(new(cfg.AwakeSavedLidAc, cfg.AwakeSavedLidDc, cfg.AwakeSavedLidScheme));
    }

    private static FakeAwake Ok => new();
    private static FakeAwake Refused => new() { Refuse = true };

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"Awake\":true,\"OwlIgnoreDisplay\":true}")]
    public void Старые_конфиги_сохраняют_крышку_только_на_AC(string json)
    {
        var cfg = JsonSerializer.Deserialize<AppConfig>(json)!;

        cfg.OwlLidOnAc.Should().BeTrue();
        cfg.OwlLidOnBattery.Should().BeFalse();
        AwakeMode.HasLidBackup(cfg).Should().BeFalse();
    }

    [Fact]
    public void Новые_опции_и_оба_backup_переживают_JSON()
    {
        var cfg = new AppConfig
        {
            OwlLidOnAc = false, OwlLidOnBattery = true, OwlIgnoreDisplay = true,
            AwakeSavedLidAc = (int)Sleep, AwakeSavedLidDc = (int)Hibernate, AwakeSavedLidScheme = HighPerf,
        };

        var restored = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(cfg))!;

        restored.OwlLidOnAc.Should().BeFalse();
        restored.OwlLidOnBattery.Should().BeTrue();
        restored.OwlIgnoreDisplay.Should().BeTrue();
        restored.AwakeSavedLidAc.Should().Be((int)Sleep);
        restored.AwakeSavedLidDc.Should().Be((int)Hibernate);
        restored.AwakeSavedLidScheme.Should().Be(HighPerf);
    }

    [Fact]
    public void Включение_и_выключение_по_умолчанию_не_трогает_DC()
    {
        var cfg = new AppConfig();
        var plan = new FakePlan();

        cfg.OwlIgnoreDisplay.Should().BeFalse();
        AwakeMode.Enable(cfg, plan, Ok).Should().BeTrue();
        plan.LidAc[Balanced].Should().Be(AwakeMode.LidDoNothing);
        plan.LidDc[Balanced].Should().Be(Hibernate);
        cfg.AwakeSavedLidAc.Should().Be((int)Sleep);
        cfg.AwakeSavedLidDc.Should().BeNull();
        cfg.AwakeSavedLidScheme.Should().Be(Balanced);

        AwakeMode.Disable(cfg, plan, Ok);

        plan.LidAc[Balanced].Should().Be(Sleep);
        plan.Reads.Should().OnlyContain(call => call.Source == "AC");
        plan.Writes.Should().OnlyContain(call => call.Source == "AC");
        AwakeMode.HasLidBackup(cfg).Should().BeFalse();
        cfg.AwakeSavedLidScheme.Should().BeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Крышка_на_батарее_только_по_явному_выбору_и_тоже_возвращается(bool ac)
    {
        var cfg = new AppConfig { OwlLidOnAc = ac, OwlLidOnBattery = true };
        var plan = new FakePlan();

        AwakeMode.Enable(cfg, plan, Ok).Should().BeTrue();

        plan.LidAc[Balanced].Should().Be(ac ? AwakeMode.LidDoNothing : Sleep);
        plan.LidDc[Balanced].Should().Be(AwakeMode.LidDoNothing);
        cfg.AwakeSavedLidAc.Should().Be(ac ? (int)Sleep : null);
        cfg.AwakeSavedLidDc.Should().Be((int)Hibernate);
        AwakeMode.HasLidBackup(cfg).Should().BeTrue("DC-only тоже требует восстановления на старте/выходе");

        AwakeMode.Disable(cfg, plan, Ok);

        plan.LidAc[Balanced].Should().Be(Sleep);
        plan.LidDc[Balanced].Should().Be(Hibernate);
        AwakeMode.HasLidBackup(cfg).Should().BeFalse();
        cfg.AwakeSavedLidScheme.Should().BeNull();
        if (!ac)
        {
            plan.Reads.Should().OnlyContain(call => call.Source == "DC");
            plan.Writes.Should().OnlyContain(call => call.Source == "DC");
        }
    }

    [Fact]
    public void Схему_сменили_при_включённой_сове_оба_источника_возвращаются_в_исходную()
    {
        var cfg = new AppConfig { OwlLidOnBattery = true };
        var plan = new FakePlan();
        AwakeMode.Enable(cfg, plan, Ok).Should().BeTrue();
        plan.Sets.Clear();

        plan.Active = HighPerf;
        AwakeMode.Disable(cfg, plan, Ok);

        plan.LidAc[Balanced].Should().Be(Sleep);
        plan.LidDc[Balanced].Should().Be(Hibernate);
        plan.LidAc[HighPerf].Should().Be(Sleep, "чужую схему не трогаем");
        plan.LidDc[HighPerf].Should().Be(Hibernate);
        plan.Sets.Should().BeEmpty("неактивную исходную схему не включаем");
        AwakeMode.HasLidBackup(cfg).Should().BeFalse();
    }

    [Fact]
    public void Повторное_включение_в_другой_схеме_сначала_возвращает_старую()
    {
        var cfg = new AppConfig { OwlLidOnBattery = true };
        var plan = new FakePlan();
        AwakeMode.Enable(cfg, plan, Ok).Should().BeTrue();
        plan.Active = HighPerf;
        plan.LidAc[HighPerf] = 3;
        plan.LidDc[HighPerf] = Sleep;
        plan.Writes.Clear();

        AwakeMode.Enable(cfg, plan, Ok).Should().BeTrue();

        plan.Writes.Should().Equal(("AC", Balanced, Sleep), ("DC", Balanced, Hibernate),
            ("AC", HighPerf, AwakeMode.LidDoNothing), ("DC", HighPerf, AwakeMode.LidDoNothing));
        cfg.AwakeSavedLidScheme.Should().Be(HighPerf);
        cfg.AwakeSavedLidAc.Should().Be(3);
        cfg.AwakeSavedLidDc.Should().Be((int)Sleep);
    }

    [Fact]
    public void Старую_схему_вернуть_не_вышло_не_включаемся_и_запись_не_теряем()
    {
        var cfg = new AppConfig { AwakeSavedLidAc = 3, AwakeSavedLidDc = 2, AwakeSavedLidScheme = Balanced };
        var plan = new FakePlan { Active = HighPerf, FailWrite = true };
        var awake = new FakeAwake { Held = true };

        AwakeMode.Enable(cfg, plan, awake).Should().BeFalse();

        awake.Held.Should().BeFalse();
        cfg.AwakeSavedLidAc.Should().Be(3);
        cfg.AwakeSavedLidDc.Should().Be(2);
        cfg.AwakeSavedLidScheme.Should().Be(Balanced);
        plan.Writes.Should().OnlyContain(call => call.Scheme == Balanced);
        plan.Reads.Should().BeEmpty();
    }

    [Fact]
    public void Частичный_возврат_старой_схемы_не_теряет_оставшийся_backup_при_переходе_на_новую()
    {
        var cfg = new AppConfig { OwlLidOnBattery = true };
        var plan = new FakePlan();
        AwakeMode.Enable(cfg, plan, Ok).Should().BeTrue();
        plan.Active = HighPerf;
        plan.FailWriteDc = true;
        plan.Writes.Clear();

        AwakeMode.Enable(cfg, plan, Ok).Should().BeFalse();

        cfg.AwakeSavedLidAc.Should().BeNull();
        cfg.AwakeSavedLidDc.Should().Be((int)Hibernate);
        cfg.AwakeSavedLidScheme.Should().Be(Balanced);
        plan.Writes.Should().OnlyContain(call => call.Scheme == Balanced);
        plan.LidAc[HighPerf].Should().Be(Sleep);
        plan.LidDc[HighPerf].Should().Be(Hibernate);

        plan.FailWriteDc = false;
        AwakeMode.Enable(cfg, plan, Ok).Should().BeTrue();
        plan.LidDc[Balanced].Should().Be(Hibernate);
        cfg.AwakeSavedLidScheme.Should().Be(HighPerf);
        cfg.AwakeSavedLidAc.Should().Be((int)Sleep);
        cfg.AwakeSavedLidDc.Should().Be((int)Hibernate);
    }

    [Fact]
    public void Флаги_сна_не_приняты_обе_крышки_откатываются()
    {
        var cfg = new AppConfig { OwlLidOnBattery = true };
        var plan = new FakePlan();

        AwakeMode.Enable(cfg, plan, Refused).Should().BeFalse();

        plan.LidAc[Balanced].Should().Be(Sleep, "сова либо целиком, либо никак");
        plan.LidDc[Balanced].Should().Be(Hibernate);
        AwakeMode.HasLidBackup(cfg).Should().BeFalse();
    }

    [Fact]
    public void Не_применилась_схема_оба_значения_откатываются_но_backup_остаётся()
    {
        var cfg = new AppConfig { OwlLidOnBattery = true };
        var plan = new FakePlan { FailSet = true };

        AwakeMode.Enable(cfg, plan, Ok).Should().BeFalse();

        plan.LidAc[Balanced].Should().Be(Sleep);
        plan.LidDc[Balanced].Should().Be(Hibernate);
        cfg.AwakeSavedLidAc.Should().Be((int)Sleep, "откат тоже упёрся в SetActive — повторим на старте");
        cfg.AwakeSavedLidDc.Should().Be((int)Hibernate);
        cfg.AwakeSavedLidScheme.Should().Be(Balanced);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Не_прочитался_любой_источник_ни_одного_write_нет(bool ac)
    {
        var cfg = new AppConfig { OwlLidOnBattery = true };
        var plan = new FakePlan { FailRead = ac, FailReadDc = !ac };

        AwakeMode.Enable(cfg, plan, Ok).Should().BeFalse();

        plan.Writes.Should().BeEmpty("все исходные значения читаем до первой записи");
        plan.Sets.Should().BeEmpty();
        plan.LidAc[Balanced].Should().Be(Sleep);
        plan.LidDc[Balanced].Should().Be(Hibernate);
        AwakeMode.HasLidBackup(cfg).Should().BeFalse();
        cfg.AwakeSavedLidScheme.Should().BeNull();
    }

    [Theory]
    [InlineData("AC")]
    [InlineData("DC")]
    public void Отказ_записи_при_включении_откатывает_оба_источника(string failedSource)
    {
        var cfg = new AppConfig { OwlLidOnBattery = true };
        var plan = new FakePlan { WriteResult = (source, value) => source != failedSource || value != AwakeMode.LidDoNothing };
        var awake = new FakeAwake();

        AwakeMode.Enable(cfg, plan, awake).Should().BeFalse();

        plan.LidAc[Balanced].Should().Be(Sleep);
        plan.LidDc[Balanced].Should().Be(Hibernate);
        plan.Writes.Should().Contain(("AC", Balanced, Sleep)).And.Contain(("DC", Balanced, Hibernate));
        awake.Held.Should().BeFalse();
        AwakeMode.HasLidBackup(cfg).Should().BeFalse();
        cfg.AwakeSavedLidScheme.Should().BeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Частичный_возврат_очищает_только_успешный_источник_и_ретраит_оставшийся(bool failAc)
    {
        var store = new SpyStore();
        var cfg = new AppConfig { OwlLidOnBattery = true, Store = store };
        var plan = new FakePlan();
        var awake = new FakeAwake();
        AwakeMode.Enable(cfg, plan, awake).Should().BeTrue();
        plan.FailWriteAc = failAc;
        plan.FailWriteDc = !failAc;

        AwakeMode.Disable(cfg, plan, awake);

        awake.Held.Should().BeFalse();
        cfg.AwakeSavedLidAc.Should().Be(failAc ? (int)Sleep : null);
        cfg.AwakeSavedLidDc.Should().Be(failAc ? null : (int)Hibernate);
        cfg.AwakeSavedLidScheme.Should().Be(Balanced);
        store.Saved[^1].Should().Be(new LidBackup(failAc ? (int)Sleep : null, failAc ? null : (int)Hibernate, Balanced));
        AwakeMode.HasLidBackup(cfg).Should().BeTrue();
        plan.Writes.Clear();
        plan.FailWriteAc = plan.FailWriteDc = false;

        AwakeMode.RestoreLid(cfg, plan).Should().BeTrue();

        plan.Writes.Should().Equal((failAc ? "AC" : "DC", Balanced, failAc ? Sleep : Hibernate));
        plan.LidAc[Balanced].Should().Be(Sleep);
        plan.LidDc[Balanced].Should().Be(Hibernate);
        AwakeMode.HasLidBackup(cfg).Should().BeFalse();
        cfg.AwakeSavedLidScheme.Should().BeNull();
        store.Saved[^1].Should().Be(new LidBackup(null, null, null));
    }

    [Fact]
    public void Исключение_возврата_AC_не_мешает_вернуть_DC()
    {
        var cfg = new AppConfig { OwlLidOnBattery = true };
        var plan = new FakePlan();
        AwakeMode.Enable(cfg, plan, Ok).Should().BeTrue();
        plan.WriteResult = (source, _) => source == "AC" ? throw new InvalidOperationException("план недоступен") : true;

        AwakeMode.RestoreLid(cfg, plan).Should().BeFalse();

        cfg.AwakeSavedLidAc.Should().Be((int)Sleep);
        cfg.AwakeSavedLidDc.Should().BeNull();
        cfg.AwakeSavedLidScheme.Should().Be(Balanced);
        plan.LidDc[Balanced].Should().Be(Hibernate);
        plan.WriteResult = null;
        AwakeMode.RestoreLid(cfg, plan).Should().BeTrue();
    }

    [Fact]
    public void Запись_без_схемы_от_старой_версии_возвращается_в_активную()
    {
        var cfg = new AppConfig { AwakeSavedLidAc = 2 };
        var plan = new FakePlan { Active = HighPerf };
        plan.LidAc[HighPerf] = AwakeMode.LidDoNothing;

        AwakeMode.RestoreLid(cfg, plan).Should().BeTrue();

        plan.LidAc[HighPerf].Should().Be(2u);
        cfg.AwakeSavedLidAc.Should().BeNull();
        cfg.AwakeSavedLidScheme.Should().BeNull();
    }

    [Fact]
    public void Частичный_legacy_возврат_закрепляет_схему_до_повтора()
    {
        var store = new SpyStore();
        var cfg = new AppConfig { AwakeSavedLidAc = (int)Sleep, AwakeSavedLidDc = (int)Hibernate, Store = store };
        var plan = new FakePlan { Active = HighPerf, FailWriteDc = true };
        plan.LidAc[HighPerf] = plan.LidDc[HighPerf] = AwakeMode.LidDoNothing;
        plan.BeforeWrite = () => store.Saved[^1].Scheme.Should().Be(HighPerf, "схема закреплена до legacy-write");

        AwakeMode.RestoreLid(cfg, plan).Should().BeFalse();

        cfg.AwakeSavedLidAc.Should().BeNull();
        cfg.AwakeSavedLidDc.Should().Be((int)Hibernate);
        cfg.AwakeSavedLidScheme.Should().Be(HighPerf);
        plan.Active = Balanced;
        plan.FailWriteDc = false;

        AwakeMode.RestoreLid(cfg, plan).Should().BeTrue();

        plan.LidDc[HighPerf].Should().Be(Hibernate, "повтор не должен записать остаток в новую активную схему");
        cfg.AwakeSavedLidScheme.Should().BeNull();
    }

    [Fact]
    public void Удержание_берётся_при_включении_и_отпускается_при_выключении()
    {
        var cfg = new AppConfig();
        var plan = new FakePlan();
        var awake = new FakeAwake();

        AwakeMode.Enable(cfg, plan, awake).Should().BeTrue();
        awake.Held.Should().BeTrue();
        awake.Display.Should().BeTrue("экран держим, пока не задан OwlIgnoreDisplay");

        AwakeMode.Disable(cfg, plan, awake);
        awake.Held.Should().BeFalse();
    }

    [Fact]
    public void OwlIgnoreDisplay_держит_только_систему()
    {
        var awake = new FakeAwake();

        AwakeMode.Enable(new AppConfig { OwlIgnoreDisplay = true }, new FakePlan(), awake).Should().BeTrue();

        awake.Display.Should().BeFalse("для удалённого доступа экран может гаснуть");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Обе_крышки_выключены_без_backup_план_вообще_не_нужен(bool ignoreDisplay)
    {
        var cfg = new AppConfig { OwlLidOnAc = false, OwlLidOnBattery = false, OwlIgnoreDisplay = ignoreDisplay };
        var plan = new FakePlan { FailActive = true };
        var awake = new FakeAwake();

        AwakeMode.Enable(cfg, plan, awake).Should().BeTrue();

        awake.Held.Should().BeTrue();
        awake.Display.Should().Be(!ignoreDisplay);
        AwakeMode.Disable(cfg, plan, awake);
        awake.Held.Should().BeFalse();
        plan.ActiveReads.Should().Be(0);
        plan.Reads.Should().BeEmpty();
        plan.Writes.Should().BeEmpty();
        plan.Sets.Should().BeEmpty();
    }

    [Fact]
    public void Live_переход_AC_на_DC_потом_без_крышки_возвращает_выключенные_источники()
    {
        var cfg = new AppConfig();
        var plan = new FakePlan();
        var awake = new FakeAwake();
        AwakeMode.Enable(cfg, plan, awake).Should().BeTrue();

        cfg.OwlLidOnAc = false;
        cfg.OwlLidOnBattery = true;
        cfg.OwlIgnoreDisplay = true;
        AwakeMode.Enable(cfg, plan, awake).Should().BeTrue();

        plan.LidAc[Balanced].Should().Be(Sleep);
        plan.LidDc[Balanced].Should().Be(AwakeMode.LidDoNothing);
        cfg.AwakeSavedLidAc.Should().BeNull();
        cfg.AwakeSavedLidDc.Should().Be((int)Hibernate);
        awake.Held.Should().BeTrue();
        awake.Display.Should().BeFalse();

        cfg.OwlLidOnBattery = false;
        AwakeMode.Enable(cfg, plan, awake).Should().BeTrue();

        plan.LidDc[Balanced].Should().Be(Hibernate);
        AwakeMode.HasLidBackup(cfg).Should().BeFalse();
        cfg.AwakeSavedLidScheme.Should().BeNull();
        awake.Held.Should().BeTrue("систему держим и без изменения крышки");
        int reads = plan.ActiveReads;
        cfg.OwlIgnoreDisplay = false;
        AwakeMode.Enable(cfg, plan, awake).Should().BeTrue();
        plan.ActiveReads.Should().Be(reads, "смена только экрана больше не обращается к плану");
        awake.Display.Should().BeTrue();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Live_отключение_одного_источника_не_перезаписывает_backup_другого(bool disableAc)
    {
        var cfg = new AppConfig { OwlLidOnBattery = true };
        var plan = new FakePlan();
        AwakeMode.Enable(cfg, plan, Ok).Should().BeTrue();
        cfg.OwlLidOnAc = !disableAc;
        cfg.OwlLidOnBattery = disableAc;

        AwakeMode.Enable(cfg, plan, Ok).Should().BeTrue();

        plan.LidAc[Balanced].Should().Be(disableAc ? Sleep : AwakeMode.LidDoNothing);
        plan.LidDc[Balanced].Should().Be(disableAc ? AwakeMode.LidDoNothing : Hibernate);
        cfg.AwakeSavedLidAc.Should().Be(disableAc ? null : (int)Sleep);
        cfg.AwakeSavedLidDc.Should().Be(disableAc ? (int)Hibernate : null);
        cfg.AwakeSavedLidScheme.Should().Be(Balanced);
        AwakeMode.Disable(cfg, plan, Ok);
        plan.LidAc[Balanced].Should().Be(Sleep);
        plan.LidDc[Balanced].Should().Be(Hibernate);
    }

    [Fact]
    public void Повторное_включение_в_той_же_схеме_не_запоминает_свои_нули()
    {
        var cfg = new AppConfig { OwlLidOnBattery = true };
        var plan = new FakePlan();
        AwakeMode.Enable(cfg, plan, Ok).Should().BeTrue();

        AwakeMode.Enable(cfg, plan, Ok).Should().BeTrue();

        cfg.AwakeSavedLidAc.Should().Be((int)Sleep);
        cfg.AwakeSavedLidDc.Should().Be((int)Hibernate);
        plan.Reads.Should().HaveCount(2);
        AwakeMode.Disable(cfg, plan, Ok);
        plan.LidAc[Balanced].Should().Be(Sleep);
        plan.LidDc[Balanced].Should().Be(Hibernate);
    }

    [Theory]
    [InlineData("active")]
    [InlineData("read AC")]
    [InlineData("read DC")]
    [InlineData("write AC")]
    [InlineData("write DC")]
    [InlineData("set")]
    [InlineData("hold")]
    [InlineData("throw write")]
    [InlineData("throw hold")]
    public void Любой_сбой_live_reapply_снимает_даже_прежнее_удержание(string failure)
    {
        bool readAc = failure == "read AC";
        var cfg = new AppConfig { OwlLidOnAc = !readAc, OwlLidOnBattery = readAc };
        var plan = new FakePlan();
        var awake = new FakeAwake();
        AwakeMode.Enable(cfg, plan, awake).Should().BeTrue();
        cfg.OwlLidOnAc = cfg.OwlLidOnBattery = true;
        switch (failure)
        {
            case "active": plan.FailActive = true; break;
            case "read AC": plan.FailRead = true; break;
            case "read DC": plan.FailReadDc = true; break;
            case "write AC": plan.WriteResult = (source, value) => source != "AC" || value != AwakeMode.LidDoNothing; break;
            case "write DC": plan.WriteResult = (source, value) => source != "DC" || value != AwakeMode.LidDoNothing; break;
            case "set": plan.FailSet = true; break;
            case "hold": awake.Refuse = true; break;
            case "throw write": plan.WriteResult = (_, value) => value == AwakeMode.LidDoNothing ? throw new InvalidOperationException("план недоступен") : true; break;
            case "throw hold": awake.ThrowOnHold = true; break;
        }

        AwakeMode.Enable(cfg, plan, awake).Should().BeFalse();

        awake.Held.Should().BeFalse("ошибка до Hold тоже обязана снять старый запрос питания");
        plan.FailActive = plan.FailSet = false;
        plan.WriteResult = null;
        AwakeMode.Disable(cfg, plan, awake);
        plan.LidAc[Balanced].Should().Be(Sleep);
        plan.LidDc[Balanced].Should().Be(Hibernate);
        AwakeMode.HasLidBackup(cfg).Should().BeFalse();
    }

    [Fact]
    public void Backup_обоих_источников_сохраняется_до_первой_правки_плана()
    {
        var store = new SpyStore();
        var cfg = new AppConfig { OwlLidOnBattery = true, Store = store };
        var plan = new FakePlan();
        plan.BeforeWrite = () =>
        {
            store.Saved.Should().NotBeEmpty();
            store.Saved[^1].Should().Be(new LidBackup((int)Sleep, (int)Hibernate, Balanced));
        };

        AwakeMode.Enable(cfg, plan, Ok).Should().BeTrue();

        store.Saved.Should().ContainSingle();
        plan.Writes.Should().HaveCount(2);
    }

    [Fact]
    public void Исключение_снятия_удержания_не_пропускает_возврат_крышки()
    {
        var cfg = new AppConfig { OwlLidOnBattery = true };
        var plan = new FakePlan();
        var awake = new FakeAwake();
        AwakeMode.Enable(cfg, plan, awake).Should().BeTrue();
        awake.ThrowOnRelease = true;

        AwakeMode.Disable(cfg, plan, awake);

        plan.LidAc[Balanced].Should().Be(Sleep);
        plan.LidDc[Balanced].Should().Be(Hibernate);
        AwakeMode.HasLidBackup(cfg).Should().BeFalse();
    }

    [Fact]
    public void Без_записи_возвращать_нечего() =>
        AwakeMode.RestoreLid(new AppConfig(), new FakePlan { FailActive = true }).Should().BeTrue();
}
