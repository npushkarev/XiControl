using XiControl.SystemIntegration;
using XiControl.Wmi;

namespace XiControl.Ui;

public sealed record PowerModeDraft(int Pl1, int Pl2)
{
    private int _pl1 = Validate(Pl1, nameof(Pl1)), _pl2 = Validate(Pl2, nameof(Pl2));
    public int Pl1 { get => _pl1; init => _pl1 = Validate(value, nameof(Pl1)); }
    public int Pl2 { get => _pl2; init => _pl2 = Validate(value, nameof(Pl2)); }

    private static int Validate(int value, string name)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(value, 10, name);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 250, name);
        return value;
    }
}

/// <summary>Черновики UI переживают пересборку формы; выбор режима сам по себе ничего не применяет.</summary>
public sealed class PowerModeEditor
{
    private readonly Dictionary<(PerfMode Mode, bool OnAc), (PowerModeDraft? Original, PowerModeDraft Current)> _drafts = [];
    public PerfMode? Selected { get; private set; }

    public void Select(PerfMode mode)
    {
        ValidateMode(mode);
        Selected = mode;
    }

    public void Reset()
    {
        _drafts.Clear();
        Selected = null;
    }

    public PowerModeDraft Values(PerfMode mode, bool onAc, IReadOnlyList<PowerModeProfile> profiles, PowerLimitSnapshot? current)
    {
        ValidateMode(mode);
        ArgumentNullException.ThrowIfNull(profiles);
        var key = (mode, onAc);
        bool cached = _drafts.TryGetValue(key, out var draft);
        if (cached && draft.Original is not null && draft.Current != draft.Original) return draft.Current;
        var saved = PowerModeProfiles.Find(profiles, mode, onAc);
        if (cached && current is null && saved is null) return draft.Current; // нет свежих данных — не заменять известное шаблоном 10/10
        // Сохранённые ватты принадлежат выбранному режиму, а снимок — текущему: не клэмпим одно другим.
        var seed = saved is null ? Seed(current) : new PowerModeDraft(saved.Pl1Watts, saved.Pl2Watts);
        var values = cached && draft.Original is null ? draft.Current : seed;
        _drafts[key] = (seed, values);
        return values;
    }

    public void Change(PerfMode mode, bool onAc, int pl1, int pl2)
    {
        ValidateMode(mode);
        var values = new PowerModeDraft(pl1, pl2);
        _drafts.TryGetValue((mode, onAc), out var draft);
        // PL1 > PL2 допустимо при редактировании одного поля: решение о применении принимает UI.
        _drafts[(mode, onAc)] = (draft.Original, values);
    }

    public void Saved(PerfMode mode, bool onAc)
    {
        ValidateMode(mode);
        _drafts.Remove((mode, onAc));
    }

    public bool Dirty(PerfMode mode, bool onAc)
    {
        ValidateMode(mode);
        return _drafts.TryGetValue((mode, onAc), out var draft) && draft.Current != draft.Original;
    }

    private static PowerModeDraft Seed(PowerLimitSnapshot? current)
    {
        int pl1 = Math.Clamp((current?.Pl1Milliwatts ?? 10000) / 1000, 10, 250);
        int pl2 = Math.Clamp((current?.Pl2Milliwatts ?? 10000) / 1000, 10, 250);
        return new(Math.Min(pl1, pl2), pl2);
    }

    private static void ValidateMode(PerfMode mode)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
    }
}
