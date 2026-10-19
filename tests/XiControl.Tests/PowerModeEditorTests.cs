using FluentAssertions;
using XiControl.SystemIntegration;
using XiControl.Ui;
using XiControl.Wmi;
using Xunit;

namespace XiControl.Tests;

public sealed class PowerModeEditorTests
{
    private readonly PowerModeEditor _editor = new();

    [Fact]
    public void Selected_не_навязывает_Auto_и_меняется_только_явно()
    {
        _editor.Selected.Should().BeNull();
        _editor.Values(PerfMode.Quiet, true, [], Snapshot(40000, 65000));
        _editor.Selected.Should().BeNull();
        _editor.Select(PerfMode.Turbo);
        _editor.Selected.Should().Be(PerfMode.Turbo);
        _editor.Values(PerfMode.Eco, false, [], null);
        _editor.Selected.Should().Be(PerfMode.Turbo);
    }

    [Fact]
    public void Values_автозагружает_точные_сохранённые_ватты_не_ограничивая_текущим_режимом()
    {
        PowerModeProfile[] profiles = [new(PerfMode.Turbo, true, 80, 120)];
        _editor.Values(PerfMode.Turbo, true, profiles, Snapshot(25000, 40000))
            .Should().Be(new PowerModeDraft(80, 120));
        _editor.Dirty(PerfMode.Turbo, true).Should().BeFalse();
        _editor.Values(PerfMode.Turbo, true, profiles, null).Should().Be(new PowerModeDraft(80, 120));
    }

    [Fact]
    public void Values_загружает_свой_режим_и_AC_DC()
    {
        PowerModeProfile[] profiles = [
            new(PerfMode.Quiet, true, 25, 40), new(PerfMode.Quiet, false, 10, 20),
            new(PerfMode.Turbo, true, 50, 80), new(PerfMode.Turbo, false, 20, 35)];
        _editor.Values(PerfMode.Quiet, true, profiles, null).Should().Be(new PowerModeDraft(25, 40));
        _editor.Values(PerfMode.Quiet, false, profiles, null).Should().Be(new PowerModeDraft(10, 20));
        _editor.Values(PerfMode.Turbo, true, profiles, null).Should().Be(new PowerModeDraft(50, 80));
        _editor.Values(PerfMode.Turbo, false, profiles, null).Should().Be(new PowerModeDraft(20, 35));
    }

    [Theory]
    [InlineData(25999, 40999, 25, 40)]
    [InlineData(1, 9999, 10, 10)]
    [InlineData(300000, int.MaxValue, 250, 250)]
    [InlineData(int.MinValue, 250001, 10, 250)]
    [InlineData(65000, 40000, 40, 40)]
    public void Values_без_профиля_округляет_снимок_вниз_и_даёт_допустимую_пару(int pl1, int pl2, int expected1, int expected2)
    {
        _editor.Values(PerfMode.Quiet, true, [], Snapshot(pl1, pl2))
            .Should().Be(new PowerModeDraft(expected1, expected2));
        _editor.Dirty(PerfMode.Quiet, true).Should().BeFalse();
    }

    [Fact]
    public void Values_без_снимка_начинает_с_10_10()
    {
        _editor.Values(PerfMode.Eco, false, [], null).Should().Be(new PowerModeDraft(10, 10));
        _editor.Dirty(PerfMode.Eco, false).Should().BeFalse();
    }

    [Fact]
    public void Values_чистое_поле_обновляется_после_свежего_чтения_или_смены_профиля()
    {
        _editor.Values(PerfMode.Quiet, true, [], Snapshot(25000, 40000));
        _editor.Values(PerfMode.Quiet, true, [], Snapshot(20000, 30000))
            .Should().Be(new PowerModeDraft(20, 30));
        PowerModeProfile[] profiles = [new(PerfMode.Quiet, true, 30, 50)];
        _editor.Values(PerfMode.Quiet, true, profiles, Snapshot(60000, 90000))
            .Should().Be(new PowerModeDraft(30, 50));
        _editor.Dirty(PerfMode.Quiet, true).Should().BeFalse();
        _editor.Change(PerfMode.Quiet, true, 25, 45);
        _editor.Values(PerfMode.Quiet, true, [], Snapshot(20000, 30000))
            .Should().Be(new PowerModeDraft(25, 45));
        _editor.Dirty(PerfMode.Quiet, true).Should().BeTrue();
    }

    [Fact]
    public void Change_хранит_черновики_режимов_независимо_от_выбора()
    {
        _editor.Values(PerfMode.Quiet, true, [], Snapshot(25000, 40000));
        _editor.Select(PerfMode.Quiet);
        _editor.Change(PerfMode.Quiet, true, 20, 35);
        _editor.Select(PerfMode.Turbo);
        _editor.Values(PerfMode.Turbo, true, [], Snapshot(50000, 80000));
        _editor.Change(PerfMode.Turbo, true, 45, 70);
        _editor.Select(PerfMode.Quiet);

        _editor.Values(PerfMode.Quiet, true, [], null).Should().Be(new PowerModeDraft(20, 35));
        _editor.Values(PerfMode.Turbo, true, [], null).Should().Be(new PowerModeDraft(45, 70));
        _editor.Dirty(PerfMode.Quiet, true).Should().BeTrue();
        _editor.Dirty(PerfMode.Turbo, true).Should().BeTrue();
        _editor.Values(PerfMode.Eco, true, [], null).Should().Be(new PowerModeDraft(10, 10));
        _editor.Dirty(PerfMode.Eco, true).Should().BeFalse();
    }

    [Fact]
    public void Change_разделяет_AC_DC_того_же_режима()
    {
        _editor.Values(PerfMode.Quiet, true, [], Snapshot(25000, 40000));
        _editor.Values(PerfMode.Quiet, false, [], Snapshot(15000, 25000));
        _editor.Change(PerfMode.Quiet, true, 20, 35);
        _editor.Values(PerfMode.Quiet, false, [], null).Should().Be(new PowerModeDraft(15, 25));
        _editor.Dirty(PerfMode.Quiet, false).Should().BeFalse();
        _editor.Change(PerfMode.Quiet, false, 10, 20);
        _editor.Values(PerfMode.Quiet, true, [], null).Should().Be(new PowerModeDraft(20, 35));
        _editor.Values(PerfMode.Quiet, false, [], null).Should().Be(new PowerModeDraft(10, 20));
        _editor.Dirty(PerfMode.Quiet, true).Should().BeTrue();
        _editor.Dirty(PerfMode.Quiet, false).Should().BeTrue();
    }

    [Fact]
    public void Dirty_сравнивает_с_первым_снимком_даже_после_его_обновления()
    {
        _editor.Dirty(PerfMode.Quiet, true).Should().BeFalse();
        _editor.Values(PerfMode.Quiet, true, [], Snapshot(25999, 40999));
        _editor.Change(PerfMode.Quiet, true, 25, 40);
        _editor.Dirty(PerfMode.Quiet, true).Should().BeFalse();
        _editor.Change(PerfMode.Quiet, true, 20, 35);
        _editor.Values(PerfMode.Quiet, true, [], Snapshot(20000, 35000));
        _editor.Dirty(PerfMode.Quiet, true).Should().BeTrue();
        _editor.Change(PerfMode.Quiet, true, 25, 40);
        _editor.Dirty(PerfMode.Quiet, true).Should().BeFalse();
    }

    [Fact]
    public void Dirty_изначально_сравнивает_с_профилем_а_не_живыми_ваттами()
    {
        PowerModeProfile[] profiles = [new(PerfMode.Turbo, true, 80, 120)];
        _editor.Values(PerfMode.Turbo, true, profiles, Snapshot(25000, 40000));
        _editor.Change(PerfMode.Turbo, true, 80, 120);
        _editor.Dirty(PerfMode.Turbo, true).Should().BeFalse();
        _editor.Change(PerfMode.Turbo, true, 25, 40);
        _editor.Dirty(PerfMode.Turbo, true).Should().BeTrue();
        _editor.Change(PerfMode.Turbo, true, 80, 120);
        _editor.Dirty(PerfMode.Turbo, true).Should().BeFalse();
    }

    [Fact]
    public void Change_не_исправляет_временно_неверную_пару()
    {
        _editor.Values(PerfMode.Quiet, true, [], Snapshot(25000, 40000));
        _editor.Change(PerfMode.Quiet, true, 60, 20);
        _editor.Values(PerfMode.Quiet, true, [], Snapshot(25000, 40000))
            .Should().Be(new PowerModeDraft(60, 20));
        _editor.Dirty(PerfMode.Quiet, true).Should().BeTrue();
    }

    [Fact]
    public void Change_до_первого_Values_сохраняет_черновик_и_позже_сравнивает_с_seed()
    {
        _editor.Change(PerfMode.Quiet, true, 25, 40);
        _editor.Dirty(PerfMode.Quiet, true).Should().BeTrue();
        _editor.Values(PerfMode.Quiet, true, [], Snapshot(25000, 40000))
            .Should().Be(new PowerModeDraft(25, 40));
        _editor.Dirty(PerfMode.Quiet, true).Should().BeFalse();
    }

    [Fact]
    public void Saved_забывает_только_свой_черновик_и_автозагружает_новый_список()
    {
        PowerModeProfile[] oldProfiles = [new(PerfMode.Quiet, true, 25, 40)];
        _editor.Select(PerfMode.Quiet);
        _editor.Values(PerfMode.Quiet, true, oldProfiles, null);
        _editor.Values(PerfMode.Quiet, false, [], Snapshot(15000, 25000));
        _editor.Values(PerfMode.Turbo, true, [], Snapshot(50000, 80000));
        _editor.Change(PerfMode.Quiet, true, 20, 35);
        _editor.Change(PerfMode.Quiet, false, 10, 20);
        _editor.Change(PerfMode.Turbo, true, 45, 70);
        _editor.Saved(PerfMode.Quiet, true);
        _editor.Dirty(PerfMode.Quiet, true).Should().BeFalse();
        _editor.Selected.Should().Be(PerfMode.Quiet);

        PowerModeProfile[] updated = [new(PerfMode.Quiet, true, 30, 50)];
        _editor.Values(PerfMode.Quiet, true, updated, Snapshot(10000, 10000))
            .Should().Be(new PowerModeDraft(30, 50));
        _editor.Dirty(PerfMode.Quiet, true).Should().BeFalse();
        _editor.Values(PerfMode.Quiet, false, [], null).Should().Be(new PowerModeDraft(10, 20));
        _editor.Values(PerfMode.Turbo, true, [], null).Should().Be(new PowerModeDraft(45, 70));
        _editor.Dirty(PerfMode.Quiet, false).Should().BeTrue();
        _editor.Dirty(PerfMode.Turbo, true).Should().BeTrue();
    }

    [Fact]
    public void Saved_после_удаления_профиля_использует_свежий_снимок()
    {
        _editor.Values(PerfMode.Quiet, true, [new(PerfMode.Quiet, true, 25, 40)], null);
        _editor.Saved(PerfMode.Quiet, true);
        _editor.Values(PerfMode.Quiet, true, [], Snapshot(30000, 50000))
            .Should().Be(new PowerModeDraft(30, 50));
        _editor.Change(PerfMode.Quiet, true, 30, 50);
        _editor.Dirty(PerfMode.Quiet, true).Should().BeFalse();
    }

    [Fact]
    public void Reset_очищает_выбор_черновики_и_исходные_значения()
    {
        _editor.Select(PerfMode.Turbo);
        _editor.Values(PerfMode.Turbo, true, [], Snapshot(50000, 80000));
        _editor.Values(PerfMode.Quiet, false, [], Snapshot(15000, 25000));
        _editor.Change(PerfMode.Turbo, true, 45, 70);
        _editor.Change(PerfMode.Quiet, false, 10, 20);
        _editor.Reset();

        _editor.Selected.Should().BeNull();
        _editor.Dirty(PerfMode.Turbo, true).Should().BeFalse();
        _editor.Dirty(PerfMode.Quiet, false).Should().BeFalse();
        _editor.Values(PerfMode.Turbo, true, [], Snapshot(30000, 50000))
            .Should().Be(new PowerModeDraft(30, 50));
        _editor.Values(PerfMode.Quiet, false, [], null).Should().Be(new PowerModeDraft(10, 10));
    }

    [Theory]
    [InlineData(9, 40)]
    [InlineData(251, 250)]
    [InlineData(20, 9)]
    [InlineData(20, 251)]
    public void Change_и_Draft_отвергают_ватты_вне_диапазона_без_потери_черновика(int pl1, int pl2)
    {
        _editor.Values(PerfMode.Quiet, true, [], Snapshot(25000, 40000));
        _editor.Change(PerfMode.Quiet, true, 20, 35);
        Action change = () => _editor.Change(PerfMode.Quiet, true, pl1, pl2);
        Action draft = () => _ = new PowerModeDraft(pl1, pl2);
        change.Should().Throw<ArgumentOutOfRangeException>();
        draft.Should().Throw<ArgumentOutOfRangeException>();
        _editor.Values(PerfMode.Quiet, true, [], null).Should().Be(new PowerModeDraft(20, 35));
        _editor.Dirty(PerfMode.Quiet, true).Should().BeTrue();
    }

    [Fact]
    public void Draft_принимает_границы_и_проверяет_также_record_with()
    {
        var values = new PowerModeDraft(10, 250);
        var (pl1, pl2) = values;
        pl1.Should().Be(10);
        pl2.Should().Be(250);
        (values with { Pl1 = 250, Pl2 = 10 }).Should().Be(new PowerModeDraft(250, 10));
        Action badPl1 = () => _ = values with { Pl1 = 9 };
        Action badPl2 = () => _ = values with { Pl2 = 251 };
        badPl1.Should().Throw<ArgumentOutOfRangeException>();
        badPl2.Should().Throw<ArgumentOutOfRangeException>();
        values.Should().Be(new PowerModeDraft(10, 250));
    }

    [Fact]
    public void Values_не_скрывает_повреждённый_профиль()
    {
        Action belowMinimum = () => _editor.Values(PerfMode.Quiet, true, [new(PerfMode.Quiet, true, 9, 40)], null);
        Action aboveMaximum = () => _editor.Values(PerfMode.Quiet, true, [new(PerfMode.Quiet, true, 25, 251)], null);
        Action reversed = () => _editor.Values(PerfMode.Quiet, true, [new(PerfMode.Quiet, true, 40, 25)], null);
        Action duplicate = () => _editor.Values(PerfMode.Quiet, true,
            [new(PerfMode.Quiet, true, 25, 40), new(PerfMode.Quiet, true, 30, 50)], null);
        Action missing = () => _editor.Values(PerfMode.Quiet, true, null!, null);
        belowMinimum.Should().Throw<InvalidDataException>();
        aboveMaximum.Should().Throw<InvalidDataException>();
        reversed.Should().Throw<InvalidDataException>();
        duplicate.Should().Throw<InvalidDataException>();
        missing.Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(255)]
    public void Неизвестный_режим_не_попадает_в_состояние(int value)
    {
        var mode = (PerfMode)value;
        Action select = () => _editor.Select(mode);
        Action values = () => _editor.Values(mode, true, [], null);
        Action change = () => _editor.Change(mode, true, 20, 30);
        Action saved = () => _editor.Saved(mode, true);
        Action dirty = () => _editor.Dirty(mode, true);
        select.Should().Throw<ArgumentOutOfRangeException>();
        values.Should().Throw<ArgumentOutOfRangeException>();
        change.Should().Throw<ArgumentOutOfRangeException>();
        saved.Should().Throw<ArgumentOutOfRangeException>();
        dirty.Should().Throw<ArgumentOutOfRangeException>();
        _editor.Selected.Should().BeNull();
    }

    private static PowerLimitSnapshot Snapshot(int pl1, int pl2) => new(pl1, pl2, true, true, 0, true, null);
}
