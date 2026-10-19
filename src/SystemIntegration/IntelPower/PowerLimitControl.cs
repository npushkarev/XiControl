using Microsoft.Win32;
using XiControl.Wmi;

namespace XiControl.SystemIntegration;

public enum PowerLimitStatus { NotChecked, Busy, Ready, Applied, Restored, Changed, Failed }
public sealed record PowerLimitView(PowerLimitStatus Status, PowerLimitSnapshot? Current,
    PowerLimitSnapshot? Baseline, bool Active, bool CanApply, string? Error);

/// <summary>Ручное, только понижающее управление: нет автоприменения, таймера, API-маршрута или «борьбы» с DTT.</summary>
public sealed class PowerLimitControl : IDisposable
{
    private readonly NativePowerLimitBackend _backend;
    private readonly PowerLimitSession _session;
    private readonly IPowerEvents _power;
    private readonly object _stateLock = new(), _operationLock = new();
    private PowerLimitView _view = new(PowerLimitStatus.NotChecked, null, null, false, false, null);
    private bool _disposed;
    private bool _lastOnline;
    private int _generation;
    private readonly PowerModeProfilesStore _profileStore = new();
    private IReadOnlyList<PowerModeProfile> _profiles = Array.Empty<PowerModeProfile>();
    private bool _profilesLoaded;

    public IReadOnlyList<PowerModeProfile> Profiles { get { lock (_stateLock) return _profiles; } }

    public PowerLimitControl(IPowerEvents power)
    {
        _power = power;
        _lastOnline = power.IsOnline;
        _backend = new NativePowerLimitBackend();
        _session = new PowerLimitSession(_backend, new PowerLimitJournalStore(),
            () => power.IsOnline, () => Volatile.Read(ref _generation));
        power.PowerModeChanged += OnPower;
        power.SessionEnding += OnSessionEnding;
    }

    public PowerLimitView View { get { lock (_stateLock) return _view; } }

    public Task RefreshAsync() => Run(() =>
    {
        // «Прочитать» действительно не пишет: оставшийся backup восстанавливается
        // отдельной кнопкой, а не незаметным побочным эффектом запроса.
        if (!_profilesLoaded) LoadProfiles();
        var current = _session.Read();
        Publish(PowerLimitStatus.Ready, current, null);
    });

    public Task ApplyAsync(int pl1Watts, int pl2Watts) => Run(() =>
    {
        _session.Apply(pl1Watts, pl2Watts);
        Publish(PowerLimitStatus.Applied, _backend.Read(), null);
    });

    public Task RestoreAsync() => Run(() =>
    {
        _session.Restore();
        Publish(PowerLimitStatus.Restored, _session.Read(), null);
    });

    public Task SaveProfileAsync(PerfMode mode, bool onAc, int pl1Watts, int pl2Watts) => Run(() =>
    {
        var baseline = _session.Baseline ?? throw new InvalidOperationException("Read limits before saving a mode profile.");
        if (!baseline.CanControl || onAc != _power.IsOnline || pl1Watts * 1000L > baseline.Pl1Milliwatts
            || pl2Watts * 1000L > baseline.Pl2Milliwatts)
            throw new InvalidOperationException("Profile exceeds the current confirmed power-source baseline.");
        var next = PowerModeProfiles.Upsert(_profileStore.Load(), new(mode, onAc, pl1Watts, pl2Watts));
        _profileStore.Save(next);
        lock (_stateLock) { _profiles = next; _profilesLoaded = true; }
        Publish(PowerLimitStatus.Ready, _backend.Read(), null);
    });

    public Task RemoveProfileAsync(PerfMode mode, bool onAc) => Run(() =>
    {
        var next = PowerModeProfiles.Remove(_profileStore.Load(), mode, onAc);
        _profileStore.Save(next);
        lock (_stateLock) { _profiles = next; _profilesLoaded = true; }
        Publish(PowerLimitStatus.Ready, View.Current, null);
    });

    // Только явный выбор режима из панели: вернуть старый cap ДО смены OEM-режима,
    // затем снять новый baseline. Не восстанавливаем старые OEM-ватты поверх нового режима.
    public Task SelectModeAsync(PerfMode mode, Func<bool> switchMode) => Run(() =>
    {
        if (!_profilesLoaded) LoadProfiles();
        bool source = _power.IsOnline;
        int epoch = Volatile.Read(ref _generation);
        var profile = PowerModeProfiles.Find(Profiles, mode, source);
        void Context()
        {
            if (source != _power.IsOnline || epoch != Volatile.Read(ref _generation))
                throw new InvalidOperationException("Power source changed during mode selection; select again.");
        }
        // При старом активном cap сначала вернуть только свои значения в исходном AC/DC.
        if (_session.Active) _session.Restore();
        _session.Invalidate();
        Context();
        if (!switchMode()) throw new InvalidOperationException("Firmware rejected the requested performance mode.");
        if (profile is null)
        {
            // Машина без IPF по-прежнему переключает штатные режимы; native API не нужен.
            Publish(PowerLimitStatus.Changed, null, null);
            return;
        }
        Context();
        _session.Read();
        Context();
        _session.Apply(profile.Pl1Watts, profile.Pl2Watts);
        Publish(PowerLimitStatus.Applied, _backend.Read(), null);
    });

    private void LoadProfiles()
    {
        var loaded = _profileStore.Load();
        lock (_stateLock) { _profiles = loaded; _profilesLoaded = true; }
    }

    private Task Run(Action action)
    {
        int generation = Volatile.Read(ref _generation);
        lock (_stateLock)
        {
            if (_disposed || _view.Status == PowerLimitStatus.Busy) return Task.CompletedTask;
            _view = _view with { Status = PowerLimitStatus.Busy, CanApply = false, Error = null };
        }
        return Task.Run(() =>
        {
            lock (_operationLock)
            {
                if (_disposed) return;
                try
                {
                    if (generation != Volatile.Read(ref _generation))
                        throw new InvalidOperationException("Power context changed while the operation was queued; read again.");
                    action();
                }
                catch (Exception ex)
                {
                    Log.Ex("IntelPower", ex);
                    Publish(PowerLimitStatus.Failed, View.Current, ex.Message);
                }
            }
        });
    }

    private void Publish(PowerLimitStatus status, PowerLimitSnapshot? snapshot, string? error)
    {
        bool active;
        bool journalKnown = true;
        try { active = _session.Active; }
        catch { active = true; journalKnown = false; } // неизвестный журнал — не предлагаем новое применение
        lock (_stateLock)
            _view = new(status, snapshot, _session.Baseline, active,
                (status is PowerLimitStatus.Ready or PowerLimitStatus.Applied or PowerLimitStatus.Restored)
                && journalKnown && _session.Baseline is not null
                && snapshot?.CanControl == true && NativePowerLimitBackend.IsElevated, error);
    }

    private void OnPower(PowerModes mode)
    {
        if (mode == PowerModes.Suspend)
        {
            Interlocked.Increment(ref _generation);
            ReleaseOwnedLimits();
            return;
        }
        if (mode == PowerModes.Resume || (mode == PowerModes.StatusChange && _lastOnline != _power.IsOnline))
        {
            Interlocked.Increment(ref _generation);
            _lastOnline = _power.IsOnline;
            _ = Task.Run(ReleaseOwnedLimits); // не перезаписываем лимит для нового источника
        }
    }

    private void OnSessionEnding()
    {
        Interlocked.Increment(ref _generation);
        ReleaseOwnedLimits();
    }

    private void ReleaseOwnedLimits()
    {
        lock (_operationLock)
        {
            if (_disposed) return;
            _session.Invalidate();
            try
            {
                if (_session.Active) _session.Restore();
                lock (_stateLock) _view = _view with { Status = PowerLimitStatus.Changed, Active = false, CanApply = false };
            }
            catch (Exception ex) { Log.Ex("IntelPower.Restore", ex); Publish(PowerLimitStatus.Failed, View.Current, ex.Message); }
        }
    }

    public void Dispose()
    {
        _power.PowerModeChanged -= OnPower;
        _power.SessionEnding -= OnSessionEnding;
        lock (_operationLock)
        {
            if (_disposed) return;
            ReleaseOwnedLimits();
            _disposed = true;
            _backend.Dispose();
        }
    }
}
