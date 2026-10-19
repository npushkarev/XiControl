using FluentAssertions;
using XiControl.SystemIntegration;
using XiControl.Wmi;
using Xunit;

namespace XiControl.Tests;

public sealed class PowerLimitControlTests
{
    private sealed class Backend : IPowerLimitBackend
    {
        public PowerLimitSnapshot Current = new(40000, 65000, true, true, 0, true, 25000);
        public readonly List<(int Index, int Value)> Writes = [];
        public PowerLimitSnapshot Read() => Current;
        public void SetLimit(int index, int milliwatts)
        {
            Writes.Add((index, milliwatts));
            Current = index == 1 ? Current with { Pl1Milliwatts = milliwatts } : Current with { Pl2Milliwatts = milliwatts };
        }
    }

    private sealed class Journal : IPowerLimitJournal
    {
        public PowerLimitJournal? Entry;
        public bool FailSave;
        public PowerLimitJournal? Load() => Entry;
        public void Save(PowerLimitJournal journal)
        {
            if (FailSave) throw new IOException("journal unavailable");
            Entry = journal;
        }
        public void Clear() => Entry = null;
    }

    private sealed class Profiles : IPowerModeProfiles
    {
        public IReadOnlyList<PowerModeProfile> Entries = Array.Empty<PowerModeProfile>();
        public bool FailSave;
        public int Saves;
        public IReadOnlyList<PowerModeProfile> Load() => Entries;
        public void Save(IReadOnlyList<PowerModeProfile> entries)
        {
            Saves++;
            if (FailSave) throw new IOException("profiles unavailable");
            Entries = entries;
        }
    }

    private readonly FakePowerEvents _power = new();
    private readonly FakeMifsClient _mifs = new() { Mode = PerfMode.Auto };
    private readonly Backend _backend = new();
    private readonly Journal _journal = new();
    private readonly Profiles _profiles = new();

    private PowerLimitControl Make() => new(_power, _mifs, _backend, _journal, _profiles, () => true);

    public PowerLimitControlTests() => Log.Enabled = false;

    [Fact]
    public async Task Refresh_читает_активный_режим_без_SET()
    {
        using var control = Make();
        await control.RefreshAsync();
        control.View.Mode.Should().Be(PerfMode.Auto);
        control.View.CanApply.Should().BeTrue();
        _backend.Writes.Should().BeEmpty();
        _profiles.Saves.Should().Be(0);
    }

    [Fact]
    public async Task Active_SaveAndApply_сохраняет_и_подтверждает_ватты_одной_операцией()
    {
        using var control = Make();
        await control.RefreshAsync();
        await control.SaveEditorAsync(PerfMode.Auto, true, 30, 50, true);
        control.View.Status.Should().Be(PowerLimitStatus.Applied);
        control.View.Mode.Should().Be(PerfMode.Auto);
        _profiles.Entries.Should().Equal(new PowerModeProfile(PerfMode.Auto, true, 30, 50));
        _backend.Current.Pl1Milliwatts.Should().Be(30000);
        _backend.Current.Pl2Milliwatts.Should().Be(50000);
        _journal.Entry.Should().NotBeNull();
    }

    [Fact]
    public async Task Другой_режим_сохраняется_без_записи_и_не_ограничивается_чужим_Eco_cap()
    {
        using var control = Make();
        _mifs.Mode = PerfMode.Eco;
        await control.RefreshAsync();
        await control.SaveEditorAsync(PerfMode.Turbo, true, 80, 120, false);
        control.View.Status.Should().Be(PowerLimitStatus.Ready);
        _profiles.Entries.Should().Equal(new PowerModeProfile(PerfMode.Turbo, true, 80, 120));
        _backend.Writes.Should().BeEmpty();
        _mifs.PerfModeCalls.Should().BeEmpty();
        _journal.Entry.Should().BeNull();
    }

    [Fact]
    public async Task Смена_режима_после_UI_не_применяет_не_тот_профиль()
    {
        using var control = Make();
        await control.RefreshAsync();
        _mifs.Mode = PerfMode.Quiet;
        await control.SaveEditorAsync(PerfMode.Auto, true, 30, 50, true);
        control.View.Status.Should().Be(PowerLimitStatus.Failed);
        control.View.Mode.Should().Be(PerfMode.Quiet);
        _backend.Writes.Should().BeEmpty();
        _profiles.Saves.Should().Be(0);
    }

    [Fact]
    public async Task Неизвестный_режим_не_разрешает_немедленное_применение()
    {
        using var control = Make();
        await control.RefreshAsync();
        _mifs.ThrowOnGetPerfMode = true;
        await control.SaveEditorAsync(PerfMode.Auto, true, 30, 50, true);
        control.View.Status.Should().Be(PowerLimitStatus.Failed);
        control.View.Mode.Should().BeNull();
        _backend.Writes.Should().BeEmpty();
        _profiles.Saves.Should().Be(0);
    }

    [Fact]
    public async Task Ошибка_сохранения_после_Apply_возвращает_мощность_и_не_теряет_старый_профиль()
    {
        using var control = Make();
        _profiles.Entries = new[] { new PowerModeProfile(PerfMode.Auto, true, 35, 55) };
        await control.RefreshAsync();
        _profiles.FailSave = true;
        await control.SaveEditorAsync(PerfMode.Auto, true, 30, 50, true);
        control.View.Status.Should().Be(PowerLimitStatus.Failed);
        _backend.Current.Pl1Milliwatts.Should().Be(40000);
        _backend.Current.Pl2Milliwatts.Should().Be(65000);
        _journal.Entry.Should().BeNull();
        _profiles.Entries.Should().Equal(new PowerModeProfile(PerfMode.Auto, true, 35, 55));
    }

    [Fact]
    public async Task Ошибка_журнала_или_выход_за_baseline_не_сохраняет_и_не_пишет()
    {
        using var control = Make();
        await control.RefreshAsync();
        await control.SaveEditorAsync(PerfMode.Auto, true, 45, 65, true);
        control.View.Status.Should().Be(PowerLimitStatus.Failed);
        _backend.Writes.Should().BeEmpty();
        _profiles.Saves.Should().Be(0);
        await control.RefreshAsync();
        _journal.FailSave = true;
        await control.SaveEditorAsync(PerfMode.Auto, true, 30, 50, true);
        _backend.Writes.Should().BeEmpty();
        _profiles.Saves.Should().Be(0);
    }

    [Fact]
    public async Task SaveOnly_не_сохраняет_профиль_на_другом_источнике()
    {
        using var control = Make();
        await control.RefreshAsync();
        _power.IsOnline = false;
        await control.SaveEditorAsync(PerfMode.Turbo, true, 30, 50, false);
        control.View.Status.Should().Be(PowerLimitStatus.Failed);
        _profiles.Saves.Should().Be(0);
        _backend.Writes.Should().BeEmpty();
    }
}
