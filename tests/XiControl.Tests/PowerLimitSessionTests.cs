using System.Text.Json;
using FluentAssertions;
using XiControl.SystemIntegration;
using Xunit;

namespace XiControl.Tests;

public sealed class PowerLimitSessionTests
{
    private const string ValidJson = """
        {"PL1":{"Enable":"Enabled","Limit":40000,"TimeWindow":28000},
         "PL2":{"Enable":"Enabled","Limit":65000},"PL4":{"Limit":175000},
         "PowerLimitEnable":true,"PowerLimitLock":0,"ThermalDesignPower":25000}
        """;

    private sealed class FakeBackend : IPowerLimitBackend
    {
        public PowerLimitSnapshot Current = PowerLimitSnapshot.Parse(ValidJson);
        public readonly List<(int Index, int Milliwatts)> Writes = [];
        public Action<int, int>? BeforeWrite;
        public Action<int, int>? AfterWrite;
        public bool FailRead;
        public int? IgnoreWriteNumber;
        public bool ReadAfterWriteFailed;

        public PowerLimitSnapshot Read()
        {
            if (FailRead) throw new IOException("read failed");
            if (ReadAfterWriteFailed) { ReadAfterWriteFailed = false; throw new IOException("readback failed"); }
            return Current;
        }

        public void SetLimit(int index, int milliwatts)
        {
            BeforeWrite?.Invoke(index, milliwatts);
            Writes.Add((index, milliwatts));
            if (IgnoreWriteNumber != Writes.Count)
                Current = index == 1 ? Current with { Pl1Milliwatts = milliwatts }
                    : Current with { Pl2Milliwatts = milliwatts };
            Current.Pl1Milliwatts.Should().BeLessThanOrEqualTo(Current.Pl2Milliwatts);
            AfterWrite?.Invoke(index, milliwatts);
        }
    }

    private sealed class FakeJournal : IPowerLimitJournal
    {
        public PowerLimitJournal? Value;
        public bool FailSave;
        public bool FailClear;
        public readonly List<PowerLimitJournal> Saved = [];
        public PowerLimitJournal? Load() => Value;
        public void Save(PowerLimitJournal journal)
        {
            if (FailSave) throw new IOException("save failed");
            Saved.Add(journal);
            Value = journal;
        }
        public void Clear()
        {
            if (FailClear) throw new IOException("clear failed");
            Value = null;
        }
    }

    private readonly FakeBackend _backend = new();
    private readonly FakeJournal _journal = new();
    private readonly PowerLimitSession _session;

    public PowerLimitSessionTests() => _session = new(_backend, _journal);

    [Fact]
    public void Parse_читает_живой_IPF_формат_в_милливаттах()
    {
        var snapshot = PowerLimitSnapshot.Parse(ValidJson);
        snapshot.Should().Be(new PowerLimitSnapshot(40000, 65000, true, true, 0, true, 25000));
        snapshot.CanControl.Should().BeTrue();
    }

    [Theory]
    [InlineData("\"PowerLimitLock\":0", "\"PowerLimitLock\":null")]
    [InlineData("\"PowerLimitLock\":0", "\"PowerLimitLock\":1")]
    [InlineData("\"PowerLimitEnable\":true", "\"PowerLimitEnable\":null")]
    [InlineData("\"PowerLimitEnable\":true", "\"PowerLimitEnable\":false")]
    [InlineData("\"Enable\":\"Enabled\"", "\"Enable\":\"Disabled\"")]
    [InlineData("\"Limit\":40000", "\"Limit\":500")]
    [InlineData("\"Limit\":40000", "\"Limit\":70000")]
    [InlineData("\"Limit\":65000", "\"Limit\":250001")]
    public void Parse_не_разрешает_неизвестное_или_заблокированное(string before, string after) =>
        PowerLimitSnapshot.Parse(ValidJson.Replace(before, after, StringComparison.Ordinal)).CanControl.Should().BeFalse();

    [Fact]
    public void Parse_отсутствующий_lock_не_равен_нулю()
    {
        var snapshot = PowerLimitSnapshot.Parse(ValidJson.Replace("\"PowerLimitLock\":0,", "", StringComparison.Ordinal));
        snapshot.Lock.Should().BeNull();
        snapshot.CanControl.Should().BeFalse();
    }

    [Theory]
    [InlineData("\"Limit\":40000", "\"Limit\":\"40000\"")]
    [InlineData("\"Limit\":40000", "\"Limit\":null")]
    [InlineData("\"Limit\":40000", "\"Limit\":40000.5")]
    [InlineData("\"Enable\":\"Enabled\"", "\"Enable\":true")]
    [InlineData("\"Enable\":\"Enabled\"", "\"Enable\":\"Unknown\"")]
    [InlineData("\"PowerLimitLock\":0", "\"PowerLimitLock\":false")]
    [InlineData("\"PowerLimitEnable\":true", "\"PowerLimitEnable\":1")]
    public void Parse_ошибку_контракта_не_скрывает(string before, string after)
    {
        Action parse = () => PowerLimitSnapshot.Parse(ValidJson.Replace(before, after, StringComparison.Ordinal));
        parse.Should().Throw<JsonException>();
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"PL1\":{}}")]
    public void Parse_требует_обе_пары(string json)
    {
        Action parse = () => PowerLimitSnapshot.Parse(json);
        parse.Should().Throw<JsonException>();
    }

    [Fact]
    public void Read_обновляет_baseline_только_без_активного_журнала()
    {
        _session.Read().Pl1Milliwatts.Should().Be(40000);
        _session.Apply(30, 50);
        _session.Read().Pl1Milliwatts.Should().Be(30000);
        _session.Baseline!.Pl1Milliwatts.Should().Be(40000);
    }

    [Fact]
    public void Изменение_между_Read_и_Apply_требует_нового_явного_чтения()
    {
        _session.Read();
        _backend.Current = _backend.Current with { Pl1Milliwatts = 30000, Pl2Milliwatts = 50000 };
        Action apply = () => _session.Apply(35, 55);
        apply.Should().Throw<InvalidOperationException>();
        _backend.Writes.Should().BeEmpty();
        _journal.Value.Should().BeNull();
        _session.Read();
        Action aboveRefreshedBaseline = () => _session.Apply(35, 55);
        aboveRefreshedBaseline.Should().Throw<ArgumentOutOfRangeException>();
        _session.Apply(20, 40);
        _journal.Value.Should().Be(new PowerLimitJournal(30000, 50000, 20000, 40000));
    }

    [Fact]
    public void Apply_сохраняет_журнал_до_SET_и_пропускает_равные_поля()
    {
        _backend.BeforeWrite = (_, _) => _journal.Value.Should().NotBeNull();
        _session.Apply(30, 65);
        _backend.Writes.Should().Equal((1, 30000));
        _journal.Value.Should().Be(new PowerLimitJournal(40000, 65000, 30000, 65000));
        _session.Active.Should().BeTrue();
    }

    [Theory]
    [InlineData(9, 50)]
    [InlineData(30, 9)]
    [InlineData(41, 65)]
    [InlineData(30, 66)]
    [InlineData(40, 30)]
    [InlineData(int.MaxValue, int.MaxValue)]
    public void Apply_отсекает_границы_и_рост_выше_начала(int pl1, int pl2)
    {
        Action apply = () => _session.Apply(pl1, pl2);
        apply.Should().Throw<ArgumentOutOfRangeException>();
        _backend.Writes.Should().BeEmpty();
        _journal.Value.Should().BeNull();
    }

    [Theory]
    [InlineData(null, true, true, true)]
    [InlineData(1, true, true, true)]
    [InlineData(0, false, true, true)]
    [InlineData(0, true, false, true)]
    [InlineData(0, true, true, false)]
    public void Apply_не_пишет_при_lock_или_выключенных_лимитах(int? locked, bool programmable, bool pl1, bool pl2)
    {
        _backend.Current = _backend.Current with { Lock = locked, Programmable = programmable, Pl1Enabled = pl1, Pl2Enabled = pl2 };
        Action apply = () => _session.Apply(30, 50);
        apply.Should().Throw<InvalidOperationException>();
        _backend.Writes.Should().BeEmpty();
    }

    [Fact]
    public void Ошибка_save_не_оставляет_аппаратную_запись()
    {
        _journal.FailSave = true;
        Action apply = () => _session.Apply(30, 50);
        apply.Should().Throw<IOException>();
        _backend.Writes.Should().BeEmpty();
    }

    [Fact]
    public void Ошибка_readback_откатывает_только_успевшее_примениться()
    {
        _backend.AfterWrite = (_, _) =>
        {
            if (_backend.Writes.Count == 1) _backend.ReadAfterWriteFailed = true;
        };
        Action apply = () => _session.Apply(30, 50);
        apply.Should().Throw<IOException>();
        _backend.Writes.Should().Equal((1, 30000), (1, 40000));
        _backend.Current.Pl1Milliwatts.Should().Be(40000);
        _journal.Value.Should().BeNull();
    }

    [Fact]
    public void Отказ_второй_записи_восстанавливает_первую()
    {
        _backend.BeforeWrite = (index, watts) =>
        {
            if (index == 2 && watts == 50000) throw new IOException("second SET failed");
        };
        Action apply = () => _session.Apply(30, 50);
        apply.Should().Throw<IOException>();
        _backend.Writes.Should().Equal((1, 30000), (1, 40000));
        _journal.Value.Should().BeNull();
    }

    [Fact]
    public void SET_с_неуспешным_readback_не_притворяется_успехом()
    {
        _backend.IgnoreWriteNumber = 1;
        Action apply = () => _session.Apply(30, 50);
        apply.Should().Throw<InvalidOperationException>();
        _backend.Writes.Should().Equal((1, 30000));
        _journal.Value.Should().BeNull();
    }

    [Fact]
    public void Ошибка_rollback_сохраняет_журнал_для_перезапуска()
    {
        _backend.BeforeWrite = (index, watts) =>
        {
            if (index == 2 || watts == 40000) throw new IOException("SET failed");
        };
        Action apply = () => _session.Apply(30, 50);
        apply.Should().Throw<AggregateException>();
        _backend.Current.Pl1Milliwatts.Should().Be(30000);
        _journal.Value.Should().NotBeNull();
        _backend.BeforeWrite = null;
        new PowerLimitSession(_backend, _journal).Restore();
        _backend.Current.Pl1Milliwatts.Should().Be(40000);
        _journal.Value.Should().BeNull();
    }

    [Fact]
    public void Apply_при_чужом_изменении_активной_сессии_не_пишет()
    {
        _session.Apply(30, 50);
        _backend.Current = _backend.Current with { Pl2Milliwatts = 45000 };
        int written = _backend.Writes.Count;
        Action apply = () => _session.Apply(20, 40);
        apply.Should().Throw<InvalidOperationException>();
        _backend.Writes.Count.Should().Be(written);
        _journal.Value.Should().NotBeNull();
    }

    [Fact]
    public void Restore_не_перезаписывает_чужое_поле()
    {
        _session.Apply(30, 50);
        _backend.Current = _backend.Current with { Pl1Milliwatts = 25000 };
        _backend.Writes.Clear();
        _session.Restore();
        _backend.Writes.Should().Equal((2, 65000));
        _backend.Current.Pl1Milliwatts.Should().Be(25000);
        _journal.Value.Should().BeNull();
        _session.Baseline.Should().BeNull();
    }

    [Fact]
    public void Restore_не_повышает_PL1_выше_чужого_PL2()
    {
        _session.Apply(30, 50);
        _backend.Current = _backend.Current with { Pl2Milliwatts = 35000 };
        _backend.Writes.Clear();
        Action restore = _session.Restore;
        restore.Should().Throw<InvalidOperationException>();
        _backend.Writes.Should().BeEmpty();
        _journal.Value.Should().NotBeNull();
    }

    [Fact]
    public void Restore_после_перезапуска_пишет_PL2_перед_PL1_если_нужно()
    {
        _session.Apply(20, 30);
        _backend.Writes.Clear();
        new PowerLimitSession(_backend, _journal).Restore();
        _backend.Writes.Should().Equal((2, 65000), (1, 40000));
        _journal.Value.Should().BeNull();
    }

    [Fact]
    public void Restore_документирует_частичный_откат_после_каждого_поля()
    {
        _session.Apply(20, 30);
        _backend.BeforeWrite = (index, _) =>
        {
            if (index == 1) throw new IOException("restore failed");
        };
        Action restore = _session.Restore;
        restore.Should().Throw<IOException>();
        _journal.Value.Should().Be(new PowerLimitJournal(40000, 65000, 20000, 65000));
        _backend.BeforeWrite = null;
        _backend.Writes.Clear();
        new PowerLimitSession(_backend, _journal).Restore();
        _backend.Writes.Should().Equal((1, 40000));
        _journal.Value.Should().BeNull();
    }

    [Fact]
    public void Частичный_журнал_после_чужого_лимита_не_обязан_быть_аппаратной_парой()
    {
        _backend.Current = _backend.Current with { Pl1Milliwatts = 100000, Pl2Milliwatts = 150000 };
        _session.Apply(20, 50);
        _backend.Current = _backend.Current with { Pl2Milliwatts = 120000 };
        _journal.FailClear = true;
        Action restore = _session.Restore;
        restore.Should().Throw<IOException>();
        _journal.Value.Should().Be(new PowerLimitJournal(100000, 150000, 100000, 50000));
        _backend.Current.Pl1Milliwatts.Should().Be(100000);
        _backend.Current.Pl2Milliwatts.Should().Be(120000);
        _journal.FailClear = false;
        _backend.Writes.Clear();
        new PowerLimitSession(_backend, _journal).Restore();
        _backend.Writes.Should().BeEmpty();
        _journal.Value.Should().BeNull();
    }

    [Fact]
    public void Ошибка_checkpoint_после_успешного_отката_не_повторяет_SET()
    {
        _session.Apply(30, 50);
        _journal.FailSave = true;
        Action restore = _session.Restore;
        restore.Should().Throw<IOException>();
        _journal.Value.Should().Be(new PowerLimitJournal(40000, 65000, 30000, 50000));
        _backend.Current.Pl1Milliwatts.Should().Be(40000);
        _journal.FailSave = false;
        _backend.Writes.Clear();
        _session.Restore();
        _backend.Writes.Should().Equal((2, 65000));
        _journal.Value.Should().BeNull();
    }

    [Fact]
    public void Чужая_запись_между_двумя_SET_не_перетягивается_второй_записью()
    {
        _backend.AfterWrite = (_, _) =>
        {
            if (_backend.Writes.Count == 1) _backend.Current = _backend.Current with { Pl2Milliwatts = 45000 };
        };
        Action apply = () => _session.Apply(30, 50);
        apply.Should().Throw<InvalidOperationException>();
        _backend.Writes.Should().Equal((1, 30000), (1, 40000));
        _backend.Current.Pl2Milliwatts.Should().Be(45000);
        _journal.Value.Should().BeNull();
    }

    [Fact]
    public void Повторный_Apply_сначала_закрывает_старый_журнал()
    {
        _session.Apply(30, 50);
        _backend.Writes.Clear();
        _session.Apply(20, 40);
        _backend.Writes.Should().Equal((1, 40000), (2, 65000), (1, 20000), (2, 40000));
        _journal.Value.Should().Be(new PowerLimitJournal(40000, 65000, 20000, 40000));
    }

    [Fact]
    public void Повтор_того_же_лимита_не_пишет()
    {
        _session.Apply(30, 50);
        _backend.Writes.Clear();
        _session.Apply(30, 50);
        _backend.Writes.Should().BeEmpty();
    }

    [Fact]
    public void Неизвестный_GET_или_lock_не_стирает_журнал_отката()
    {
        _session.Apply(30, 50);
        _backend.FailRead = true;
        Action restore = _session.Restore;
        restore.Should().Throw<IOException>();
        _journal.Value.Should().NotBeNull();
        _backend.FailRead = false;
        _backend.Current = _backend.Current with { Lock = 1 };
        restore.Should().Throw<InvalidOperationException>();
        _journal.Value.Should().NotBeNull();
    }

    [Fact]
    public void Неуспешный_Clear_оставляет_безопасный_повторный_откат()
    {
        _session.Apply(30, 50);
        _journal.FailClear = true;
        Action restore = _session.Restore;
        restore.Should().Throw<IOException>();
        _journal.Value.Should().NotBeNull();
        _journal.FailClear = false;
        _backend.Writes.Clear();
        _session.Restore();
        _backend.Writes.Should().BeEmpty();
        _journal.Value.Should().BeNull();
    }

    [Fact]
    public void Источник_питания_привязан_к_журналу_и_откат_на_другом_не_повышает_ватты()
    {
        bool online = true;
        int generation = 0;
        var session = new PowerLimitSession(_backend, _journal, () => online, () => generation);
        session.Read();
        session.Apply(20, 30);
        _journal.Value!.OnAc.Should().BeTrue();
        online = false;
        generation++;
        _backend.Writes.Clear();
        Action restore = session.Restore;
        restore.Should().Throw<InvalidOperationException>();
        _backend.Writes.Should().BeEmpty();
        _journal.Value.Should().NotBeNull();
        online = true;
        generation++;
        session.Restore();
        _backend.Current.Pl1Milliwatts.Should().Be(40000);
        _backend.Current.Pl2Milliwatts.Should().Be(65000);
        _journal.Value.Should().BeNull();
    }

    [Fact]
    public void Старый_журнал_без_источника_не_разрешает_повышающий_откат()
    {
        _journal.Value = new(40000, 65000, 20000, 30000);
        _backend.Current = _backend.Current with { Pl1Milliwatts = 20000, Pl2Milliwatts = 30000 };
        var session = new PowerLimitSession(_backend, _journal, () => false);
        Action restore = session.Restore;
        restore.Should().Throw<InvalidOperationException>();
        _backend.Writes.Should().BeEmpty();
        _journal.Value.Should().NotBeNull();
    }

    [Fact]
    public void Смена_питания_после_чтения_не_пускает_старый_Apply_даже_если_ватты_те_же()
    {
        bool online = true;
        int generation = 0;
        var session = new PowerLimitSession(_backend, _journal, () => online, () => generation);
        session.Read();
        online = false;
        generation++;
        Action apply = () => session.Apply(20, 30);
        apply.Should().Throw<InvalidOperationException>();
        _backend.Writes.Should().BeEmpty();
        _journal.Value.Should().BeNull();
        session.Read();
        session.Apply(20, 30);
        _journal.Value!.OnAc.Should().BeFalse();
    }

    [Fact]
    public void Смена_поколения_перед_SET_не_пишет_в_новом_контексте()
    {
        int generation = 0;
        var session = new PowerLimitSession(_backend, _journal, () => true, () => generation);
        session.Read();
        _backend.AfterWrite = (_, _) => generation++;
        Action apply = () => session.Apply(20, 30);
        apply.Should().Throw<Exception>();
        _backend.Writes.Should().NotContain(x => x.Index == 2 && x.Milliwatts == 30000);
    }

    [Fact]
    public void Invalidate_требует_нового_Read_до_Apply()
    {
        var session = new PowerLimitSession(_backend, _journal, () => true);
        session.Read();
        session.Invalidate();
        Action apply = () => session.Apply(20, 30);
        apply.Should().Throw<InvalidOperationException>();
        _backend.Writes.Should().BeEmpty();
    }

    [Fact]
    public void Повреждённый_журнал_не_доходит_до_железа()
    {
        _journal.Value = new(40000, 65000, -1, 50000);
        Action restore = _session.Restore;
        restore.Should().Throw<InvalidOperationException>();
        _backend.Writes.Should().BeEmpty();
        _journal.Value.Should().NotBeNull();
    }
}
