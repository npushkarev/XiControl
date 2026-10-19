namespace XiControl.SystemIntegration;

/// <summary>Шов над Планировщиком: контроллер тестируется без создания настоящих задач.</summary>
public interface IAutoStart
{
    bool IsEnabled();
    bool Set(bool enabled);
    void RepairIfBroken();
}

/// <summary>Состояние операции для настроек: желаемое не выдаём за реально применённое.</summary>
public readonly record struct AutoStartView(bool Enabled, bool Pending, bool Failed);

internal sealed class TaskAutoStart : IAutoStart
{
    public bool IsEnabled() => AutoStart.IsEnabled();
    public bool Set(bool enabled) => AutoStart.Set(enabled);
    public void RepairIfBroken() => AutoStart.RepairIfBroken();
}
