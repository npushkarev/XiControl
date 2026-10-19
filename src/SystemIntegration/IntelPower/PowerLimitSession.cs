using System.Text.Json;

namespace XiControl.SystemIntegration;

public sealed record PowerLimitSnapshot(int Pl1Milliwatts, int Pl2Milliwatts, bool Pl1Enabled,
    bool Pl2Enabled, int? Lock, bool Programmable, int? TdpMilliwatts)
{
    public bool CanControl => Lock == 0 && Programmable && Pl1Enabled && Pl2Enabled &&
        ValidPower(Pl1Milliwatts) && ValidPower(Pl2Milliwatts) && Pl1Milliwatts <= Pl2Milliwatts;

    internal static bool ValidPower(int value) => value is >= 1000 and <= 250000;

    public static PowerLimitSnapshot Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var pl1 = Required(root, "PL1");
        var pl2 = Required(root, "PL2");
        return new(Integer(Required(pl1, "Limit")), Integer(Required(pl2, "Limit")),
            Enabled(Required(pl1, "Enable")), Enabled(Required(pl2, "Enable")),
            OptionalInteger(root, "PowerLimitLock"), OptionalBoolean(root, "PowerLimitEnable"),
            OptionalInteger(root, "ThermalDesignPower"));
    }

    private static JsonElement Required(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value)
            ? value : throw new JsonException($"Missing IPF property: {name}");

    private static int Integer(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int result)
            ? result : throw new JsonException("Expected an IPF integer.");

    private static int? OptionalInteger(JsonElement parent, string name) =>
        !parent.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null
            ? null : Integer(value);

    private static bool Enabled(JsonElement value) => value.ValueKind == JsonValueKind.String
        ? value.GetString() switch
        {
            "Enabled" => true,
            "Disabled" => false,
            _ => throw new JsonException("Unknown IPF power-limit enable state."),
        } : throw new JsonException("Expected an IPF power-limit enable state.");

    private static bool OptionalBoolean(JsonElement parent, string name) =>
        !parent.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null ? false :
        value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new JsonException("Expected an IPF boolean."),
        };
}

public interface IPowerLimitBackend
{
    PowerLimitSnapshot Read();
    void SetLimit(int index, int milliwatts);
}

public sealed record PowerLimitJournal(int OriginalPl1, int OriginalPl2, int TargetPl1, int TargetPl2,
    bool? OnAc = null);

public interface IPowerLimitJournal
{
    PowerLimitJournal? Load();
    void Save(PowerLimitJournal journal);
    void Clear();
}

/// <summary>
/// Экспериментальная ручная сессия: только PL1/PL2, без Enable/TimeWindow/PL4 и автоповторов.
/// 10 Вт и исходные значения — наши ограничения опыта, НЕ опубликованные Intel границы модели.
/// Вызовы сериализует владелец. Чужую запись не перетягиваем; compare-before-write не является
/// аппаратным CAS и не исключает гонку с DTT/прошивкой между последним GET и SET.
/// </summary>
public sealed class PowerLimitSession(IPowerLimitBackend backend, IPowerLimitJournal journal,
    Func<bool>? isOnline = null, Func<int>? generation = null)
{
    private bool? _baselineSource;
    private int _baselineGeneration;
    public PowerLimitSnapshot? Baseline { get; private set; }
    public bool Active => journal.Load() is not null;

    public void Invalidate() { Baseline = null; _baselineSource = null; }

    public PowerLimitSnapshot Read()
    {
        bool? source = isOnline?.Invoke();
        int epoch = generation?.Invoke() ?? 0;
        var current = backend.Read();
        CheckContext(source, epoch);
        if (!Active)
        {
            Baseline = current;
            _baselineSource = source;
            _baselineGeneration = epoch;
        }
        return current;
    }

    private void CheckContext(bool? source, int epoch)
    {
        if ((isOnline is not null && source != isOnline()) || (generation?.Invoke() ?? 0) != epoch)
            throw new InvalidOperationException("Power source or session changed; read limits again in the original context.");
    }

    public void Apply(int pl1Watts, int pl2Watts)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pl1Watts, 10);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pl1Watts, 250);
        ArgumentOutOfRangeException.ThrowIfLessThan(pl2Watts, 10);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pl2Watts, 250);
        int target1 = pl1Watts * 1000, target2 = pl2Watts * 1000;
        bool? source = isOnline?.Invoke();
        int epoch = generation?.Invoke() ?? 0;
        if (isOnline is not null && Baseline is null)
            throw new InvalidOperationException("Read power limits in this power context before applying.");
        if (Baseline is not null) CheckContext(_baselineSource, _baselineGeneration);
        var pending = journal.Load();
        var current = backend.Read();
        CheckContext(source, epoch);
        RequireControl(current);
        if (pending is not null)
        {
            Validate(pending);
            RequireValues(current, pending.TargetPl1, pending.TargetPl2);
            if (current.Pl1Milliwatts == target1 && current.Pl2Milliwatts == target2) return;
        }
        var baseline = Baseline ?? (pending is null ? current :
            throw new InvalidOperationException("Recover the pending power-limit session first."));
        RequireControl(baseline);
        if (pending is null) RequireValues(current, baseline.Pl1Milliwatts, baseline.Pl2Milliwatts);
        if (target1 > target2 || target1 > baseline.Pl1Milliwatts || target2 > baseline.Pl2Milliwatts)
            throw new ArgumentOutOfRangeException(nameof(pl1Watts), "Power limits exceed the captured baseline.");
        Baseline = baseline;

        // Четыре числа журнала не описывают сразу старую и новую незавершённую запись.
        // Перед повторным Apply закрываем прежнюю транзакцию, иначе потеряется ownership при падении.
        if (pending is not null)
        {
            Restore();
            current = backend.Read();
            RequireControl(current);
            RequireValues(current, pending.OriginalPl1, pending.OriginalPl2);
            Baseline = baseline;
            _baselineSource = source;
            _baselineGeneration = epoch;
        }
        if (current.Pl1Milliwatts == target1 && current.Pl2Milliwatts == target2) return;
        CheckContext(source, epoch);
        journal.Save(new(current.Pl1Milliwatts, current.Pl2Milliwatts, target1, target2, source));
        try
        {
            WritePair(current, target1, target2, source: source, epoch: epoch);
            RequireValues(backend.Read(), target1, target2);
            CheckContext(source, epoch);
        }
        catch (Exception applyError)
        {
            try { Restore(); }
            catch (Exception rollbackError)
            {
                throw new AggregateException("Power-limit apply and rollback failed.", applyError, rollbackError);
            }
            throw;
        }
    }

    public void Restore()
    {
        Invalidate();
        if (journal.Load() is not PowerLimitJournal pending) return;
        Validate(pending);
        bool? source = isOnline?.Invoke();
        int epoch = generation?.Invoke() ?? 0;
        if (isOnline is not null && pending.OnAc != source)
            throw new InvalidOperationException("Recovery belongs to another or unknown power source; return to that source first.");
        var current = backend.Read();
        CheckContext(source, epoch);
        RequireControl(current);
        int target1 = current.Pl1Milliwatts == pending.TargetPl1 ? pending.OriginalPl1 : current.Pl1Milliwatts;
        int target2 = current.Pl2Milliwatts == pending.TargetPl2 ? pending.OriginalPl2 : current.Pl2Milliwatts;
        if (target1 > target2)
            throw new InvalidOperationException("Restoring an owned limit would conflict with an external limit.");
        WritePair(current, target1, target2, index =>
        {
            // Сначала подтверждённый SET, затем снимаем ownership этого поля. Старый журнал
            // тоже восстанавливаем: live==Original уже не требует записи после перезапуска.
            pending = index == 1 ? pending with { TargetPl1 = pending.OriginalPl1 }
                : pending with { TargetPl2 = pending.OriginalPl2 };
            journal.Save(pending);
        }, source, epoch);
        RequireValues(backend.Read(), target1, target2);
        CheckContext(source, epoch);
        journal.Clear();
    }

    private void WritePair(PowerLimitSnapshot current, int target1, int target2,
        Action<int>? written = null, bool? source = null, int epoch = 0)
    {
        int[] order = target1 <= current.Pl2Milliwatts ? [1, 2] : [2, 1];
        foreach (int index in order)
        {
            int target = index == 1 ? target1 : target2;
            if ((index == 1 ? current.Pl1Milliwatts : current.Pl2Milliwatts) == target) continue;
            RequireValues(backend.Read(), current.Pl1Milliwatts, current.Pl2Milliwatts);
            CheckContext(source, epoch); // ещё раз непосредственно перед SET, не только перед постановкой в очередь
            backend.SetLimit(index, target);
            current = index == 1 ? current with { Pl1Milliwatts = target } : current with { Pl2Milliwatts = target };
            RequireValues(backend.Read(), current.Pl1Milliwatts, current.Pl2Milliwatts);
            written?.Invoke(index);
        }
    }

    private static void RequireControl(PowerLimitSnapshot snapshot)
    {
        if (!snapshot.CanControl) throw new InvalidOperationException("IPF power limits are unavailable, disabled or locked.");
    }

    private static void RequireValues(PowerLimitSnapshot snapshot, int pl1, int pl2)
    {
        RequireControl(snapshot);
        if (snapshot.Pl1Milliwatts != pl1 || snapshot.Pl2Milliwatts != pl2)
            throw new InvalidOperationException("IPF power limits changed externally or failed readback.");
    }

    private static void Validate(PowerLimitJournal entry)
    {
        // Target по частям становится Original при откате, поэтому промежуточная пара
        // журнала не обязана быть аппаратной парой. Каждое число всё равно проверяем.
        if (!PowerLimitSnapshot.ValidPower(entry.OriginalPl1) || !PowerLimitSnapshot.ValidPower(entry.OriginalPl2) ||
            !PowerLimitSnapshot.ValidPower(entry.TargetPl1) || !PowerLimitSnapshot.ValidPower(entry.TargetPl2) ||
            entry.OriginalPl1 > entry.OriginalPl2 || entry.TargetPl1 > entry.OriginalPl1 || entry.TargetPl2 > entry.OriginalPl2)
            throw new InvalidOperationException("Invalid power-limit recovery journal.");
    }
}
