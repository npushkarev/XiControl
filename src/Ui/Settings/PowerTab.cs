using XiControl.Config;
using XiControl.Localization;

namespace XiControl.Ui.Settings;

/// <summary>Питание, крышка и автозапуск — в одном месте, без правки config.json.</summary>
public sealed class PowerTab : SettingsPane
{
    public PowerTab(SettingsToolkit ui, AppConfig cfg, SettingsActions act, Action rebuild) : base(ui)
    {
        ui.AddHero(this, "settings.tab.power", "settings.power.sub",
            Loc.T(cfg.Awake ? "settings.power.active" : "settings.power.idle"), cfg.Awake);

        PowerLimitsPanel.Add(this, ui, act, rebuild);

        ui.AddGroup(this, "settings.power.startup");
        var startup = act.GetAutoStartState();
        var autoStart = ui.Toggle(startup.Enabled, on => act.SetAutoStart(on, rebuild));
        autoStart.Enabled = !startup.Pending;
        ui.AddRow(this, "settings.autostart",
            startup.Pending ? "settings.autostart.pending" : "settings.autostart.desc", autoStart);
        if (startup.Failed) ui.AddNote(this, "settings.autostart.failed");

        ui.AddGroup(this, "settings.owl.group");
        var awake = ui.Toggle(cfg.Awake, on => { act.SetAwake(on); rebuild(); });
        awake.Enabled = cfg.OwlMode;
        ui.AddRow(this, "settings.owl.active", "settings.owl.active.desc", awake);
        if (!cfg.OwlMode) ui.AddNote(this, "settings.owl.hidden");
        else
        {
            void Options(bool ac, bool battery, bool display)
            {
                act.SetAwakeOptions(ac, battery, display);
                rebuild(); // при отказе показываем реальное, а не желаемое состояние
            }
            ui.AddRow(this, "settings.owl.lid.ac", "settings.owl.lid.ac.desc",
                ui.Toggle(cfg.OwlLidOnAc, on => Options(on, cfg.OwlLidOnBattery, !cfg.OwlIgnoreDisplay)));
            ui.AddRow(this, "settings.owl.lid.battery", "settings.owl.lid.battery.desc",
                ui.Toggle(cfg.OwlLidOnBattery, on => Options(cfg.OwlLidOnAc, on, !cfg.OwlIgnoreDisplay)));
            ui.AddRow(this, "settings.owl.display", "settings.owl.display.desc",
                ui.Toggle(!cfg.OwlIgnoreDisplay, on => Options(cfg.OwlLidOnAc, cfg.OwlLidOnBattery, on)));
            if (act.GetAwakeFailed()) ui.AddNote(this, "settings.owl.failed");
            ui.AddNote(this, "settings.owl.safety");
        }
    }
}
