using System.Drawing.Drawing2D;
using XiControl.Localization;
using XiControl.SystemIntegration;
using XiControl.Wmi;

namespace XiControl.Ui.Settings;

/// <summary>Компактные карточки PL1/PL2: снимок, выбранные ватты и явное применение.</summary>
internal static class PowerLimitsPanel
{
    private static bool _showDetails;
    private static readonly PowerModeEditor Editor = new();
    public static void SelectMode(PerfMode mode) => Editor.Select(mode);

    public static void Add(SettingsPane pane, SettingsToolkit ui, SettingsActions act, Action rebuild)
    {
        var view = act.GetPowerLimits();
        bool busy = view.Status == PowerLimitStatus.Busy, online = act.IsOnlineNow();
        var profiles = act.GetPowerModeProfiles();
        PerfMode selected = Editor.Selected ?? view.Mode ?? PerfMode.Auto;
        if (Editor.Selected is null && view.Mode is not null) Editor.Select(selected);
        bool isActive = view.Mode == selected;
        var saved = PowerModeProfiles.Find(profiles, selected, online);
        ui.AddGroup(pane, "settings.tdp.group");
        AddModePicker(pane, ui, selected, view.Mode, profiles, online, busy, rebuild);
        string name = Loc.T(ModeUi.Key(selected) ?? "mode.auto");
        string source = Loc.T(online ? "settings.perf.modes.ac" : "settings.perf.modes.battery");
        var check = Button(ui, busy ? "settings.tdp.busy" : "settings.tdp.editor.refresh", () => act.RefreshPowerLimits(rebuild));
        check.Enabled = !busy;
        string context = Loc.T("settings.tdp.editor.context", name, source,
            Loc.T(isActive ? "settings.tdp.editor.active" : "settings.tdp.editor.inactive"));
        pane.Controls.Add(ui.Row(context, Loc.T(isActive ? "settings.tdp.editor.activehint" : "settings.tdp.editor.savedhint"), check));
        if (view.Status is PowerLimitStatus.Busy or PowerLimitStatus.Applied or PowerLimitStatus.Restored or PowerLimitStatus.Changed)
            pane.Controls.Add(new Label { Text = Loc.T(StatusKey(view.Status)), AutoSize = true, MaximumSize = new Size(ui.RowW, 0),
                Font = ui.DescFont, ForeColor = ui.T.Text2, Margin = new Padding(0, 0, 0, ui.Sc(5)) });
        if (view.Status == PowerLimitStatus.Failed) ui.AddNote(pane, "settings.tdp.error");
        if (view.Active) Recovery(pane, ui, act, rebuild, busy);
        if (view.Current is not { } current)
        {
            ui.AddNote(pane, "settings.tdp.note");
            return;
        }

        var values = Editor.Values(selected, online, profiles, current);
        bool editable = !busy && view.Status != PowerLimitStatus.Failed && current.CanControl;
        // У другого режима могут быть большие сохранённые значения: не подменяем их текущим Eco-cap.
        // Для активного режима превышение baseline видно как ошибка и блокирует применение.
        int cap1 = Math.Clamp((view.Baseline?.Pl1Milliwatts ?? current.Pl1Milliwatts) / 1000, 10, 250);
        int cap2 = Math.Clamp((view.Baseline?.Pl2Milliwatts ?? current.Pl2Milliwatts) / 1000, 10, 250);
        int max1 = isActive ? Math.Max(cap1, values.Pl1) : 250;
        int max2 = isActive ? Math.Max(cap2, values.Pl2) : 250;
        int gap = ui.Sc(10), cardW = (ui.RowW - gap) / 2;
        var first = LimitCard(ui, cardW, "settings.tdp.editor.pl1", "settings.tdp.target.pl1", values.Pl1 * 1000, max1, editable, out var pl1);
        var second = LimitCard(ui, cardW, "settings.tdp.editor.pl2", "settings.tdp.target.pl2", values.Pl2 * 1000, max2, editable, out var pl2);
        var cards = new Panel { Width = ui.RowW, Height = Math.Max(first.Height, second.Height), BackColor = ui.T.WinBg, Margin = new Padding(0, ui.Sc(7), 0, ui.Sc(8)) };
        first.Height = second.Height = cards.Height;
        second.Left = cardW + gap;
        cards.Controls.Add(first); cards.Controls.Add(second); pane.Controls.Add(cards);

        var state = new Label { AutoSize = true, MaximumSize = new Size(ui.RowW, 0), Font = ui.DescFont,
            ForeColor = ui.T.Text2, BackColor = ui.T.WinBg, Margin = new Padding(0, 0, 0, ui.Sc(8)) };
        pane.Controls.Add(state);
        var save = Button(ui, isActive ? "settings.tdp.editor.saveapply" : "settings.tdp.editor.save", () =>
        {
            if (pl1.Value > pl2.Value) return;
            if (isActive && MessageBox.Show(Loc.T("settings.tdp.editor.confirm", name), Loc.T("settings.tdp.editor.title"),
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            act.SavePowerModeEditor(selected, online, (int)pl1.Value, (int)pl2.Value, isActive, () =>
            {
                if (act.GetPowerLimits().Status is not (PowerLimitStatus.Busy or PowerLimitStatus.Failed)) Editor.Saved(selected, online);
                rebuild();
            });
        });
        save.BackColor = ui.T.Accent; save.ForeColor = ui.T.Dark ? ui.T.WinBg : ui.T.Card;
        var cancel = Button(ui, "settings.tdp.editor.cancel", () => { Editor.Saved(selected, online); rebuild(); });
        void Validate(object? sender, EventArgs e)
        {
            Editor.Change(selected, online, (int)pl1.Value, (int)pl2.Value);
            bool pair = pl1.Value <= pl2.Value;
            bool cap = !isActive || (pl1.Value <= cap1 && pl2.Value <= cap2);
            bool dirty = Editor.Dirty(selected, online);
            bool notApplied = isActive && ((int)pl1.Value * 1000 != current.Pl1Milliwatts || (int)pl2.Value * 1000 != current.Pl2Milliwatts);
            save.Enabled = editable && pair && cap && (dirty || saved is null || notApplied)
                && (!isActive || view.CanApply);
            cancel.Enabled = !busy && dirty;
            state.Text = !pair ? Loc.T("settings.tdp.editor.invalidpair") : !cap ? Loc.T("settings.tdp.editor.abovecap")
                : dirty ? Loc.T("settings.tdp.editor.unsaved") : saved is not null
                    ? Loc.T("settings.tdp.editor.autoloaded", saved.Pl1Watts, saved.Pl2Watts)
                    : Loc.T("settings.tdp.editor.template");
        }
        pl1.ValueChanged += Validate; pl2.ValueChanged += Validate; Validate(null, EventArgs.Empty);
        var buttons = new FlowLayoutPanel { Width = ui.RowW, AutoSize = true, WrapContents = true,
            BackColor = ui.T.WinBg, Margin = new Padding(0, 0, 0, ui.Sc(8)) };
        buttons.Controls.Add(save); buttons.Controls.Add(cancel); pane.Controls.Add(buttons);
        string live = Loc.T("settings.tdp.editor.live", current.Pl1Milliwatts / 1000.0, current.Pl2Milliwatts / 1000.0);
        pane.Controls.Add(new Label { Text = live, AutoSize = true, MaximumSize = new Size(ui.RowW, 0), Font = ui.DescFont,
            ForeColor = ui.T.Text2, BackColor = ui.T.WinBg, Margin = new Padding(0, 0, 0, ui.Sc(8)) });
        if (!current.CanControl) ui.AddNote(pane, "settings.tdp.unsupported");
        var details = Button(ui, "settings.tdp.details", () => { _showDetails = !_showDetails; rebuild(); });
        pane.Controls.Add(details);
        if (_showDetails)
        {
            ui.AddNote(pane, "settings.tdp.session");
            var remove = Button(ui, "settings.tdp.editor.remove", () => act.RemovePowerModeProfile(selected, online, () =>
            {
                if (act.GetPowerLimits().Status is not (PowerLimitStatus.Busy or PowerLimitStatus.Failed)) Editor.Saved(selected, online);
                rebuild();
            }));
            remove.Enabled = saved is not null && !busy; pane.Controls.Add(remove);
        }
        ui.AddNote(pane, "settings.tdp.note");
    }

    private static void AddModePicker(SettingsPane pane, SettingsToolkit ui, PerfMode selected, PerfMode? active,
        IReadOnlyList<PowerModeProfile> profiles, bool online, bool busy, Action rebuild)
    {
        int gap = ui.Sc(6), width = (ui.RowW - gap * 2) / 3, height = ui.Sc(48);
        var grid = new Panel { Width = ui.RowW, Height = height * 2 + gap, BackColor = ui.T.WinBg,
            Margin = new Padding(0, ui.Sc(2), 0, ui.Sc(8)) };
        var modes = AppController.AllModes;
        for (int i = 0; i < modes.Length; i++)
        {
            var mode = modes[i];
            string title = Loc.T(ModeUi.Key(mode) ?? "mode.auto");
            if (active == mode) title += " · " + Loc.T("settings.tdp.editor.active");
            var saved = PowerModeProfiles.Find(profiles, mode, online);
            string subtitle = saved is null ? Loc.T("settings.tdp.editor.stock")
                : Loc.T("settings.tdp.editor.short", saved.Pl1Watts, saved.Pl2Watts);
            var button = new Button { Text = title + "\n" + subtitle, Font = ui.CtlFont,
                Location = new Point(i % 3 * (width + gap), i / 3 * (height + gap)), Size = new Size(width, height),
                FlatStyle = FlatStyle.Flat, BackColor = selected == mode ? ui.T.Sel : ui.T.Card,
                ForeColor = selected == mode ? ui.T.Accent : ui.T.Text, Enabled = !busy,
                AccessibleName = title + ", " + subtitle, Cursor = Cursors.Hand };
            button.FlatAppearance.BorderColor = selected == mode ? ui.T.Accent : ui.T.Border;
            button.Click += (_, _) => { Editor.Select(mode); rebuild(); };
            grid.Controls.Add(button);
        }
        pane.Controls.Add(grid);
    }

    private static Panel LimitCard(SettingsToolkit ui, int width, string captionKey, string targetKey,
        int milliwatts, int maximum, bool enabled, out NumericUpDown number)
    {
        int pad = ui.Sc(16), textW = width - pad * 2;
        string caption = Loc.T(captionKey);
        int captionH = TextRenderer.MeasureText(caption, ui.DescFont, new Size(textW, 0), TextFormatFlags.WordBreak).Height;
        var card = new Panel { Width = width, Height = ui.Sc(120) + captionH, BackColor = ui.T.Card };
        card.Paint += (_, e) => ui.PaintCardBorder(e.Graphics, card.Width, card.Height);
        card.Controls.Add(new Label { Text = caption, ForeColor = ui.T.Text2, Font = ui.DescFont,
            Location = new Point(pad, ui.Sc(13)), Size = new Size(textW, captionH + ui.Sc(2)) });
        var valueText = new Label { Text = Watts(milliwatts), ForeColor = ui.T.Text, Font = ui.HeadFont,
            Location = new Point(pad, ui.Sc(21) + captionH), Size = new Size(textW - ui.Sc(82), ui.Sc(32)) };
        card.Controls.Add(valueText);
        string target = Loc.T(targetKey);
        number = new NumericUpDown
        {
            Minimum = 10, Maximum = maximum, Value = Math.Clamp(milliwatts / 1000, 10, maximum), Increment = 1,
            Width = ui.Sc(76), Font = ui.CtlFont, BackColor = ui.T.Field, ForeColor = ui.T.Text,
            BorderStyle = BorderStyle.FixedSingle, Enabled = enabled, AccessibleName = target,
            Location = new Point(width - pad - ui.Sc(76), ui.Sc(25) + captionH),
        };
        card.Controls.Add(number);
        var slider = new PowerSlider(ui, 10, maximum, (int)number.Value)
        {
            Location = new Point(pad, ui.Sc(66) + captionH), Size = new Size(textW, ui.Sc(27)),
            Enabled = enabled, AccessibleName = target,
        };
        var input = number;
        input.ValueChanged += (_, _) =>
        {
            slider.Value = (int)input.Value;
            valueText.Text = Watts((int)input.Value * 1000);
        };
        slider.ValueChanged += () => input.Value = slider.Value;
        card.Controls.Add(slider);
        string range = Watts(10000) + " — " + Watts(maximum * 1000);
        card.Controls.Add(new Label { Text = range, Font = ui.DescFont, ForeColor = ui.T.Text2,
            Location = new Point(pad, ui.Sc(94) + captionH), Size = new Size(textW, ui.Sc(20)) });
        return card;
    }

    private static void Recovery(SettingsPane pane, SettingsToolkit ui, SettingsActions act, Action rebuild, bool busy)
    {
        var restore = Button(ui, "settings.tdp.restore", () => act.RestorePowerLimits(rebuild));
        restore.Enabled = !busy;
        pane.Controls.Add(ui.Row(Loc.T("settings.tdp.restore"), Loc.T("settings.tdp.recovery"), restore));
    }

    private static string StatusKey(PowerLimitStatus status) => status switch
    {
        PowerLimitStatus.Busy => "settings.tdp.status.busy", PowerLimitStatus.Ready => "settings.tdp.status.ready",
        PowerLimitStatus.Applied => "settings.tdp.status.applied", PowerLimitStatus.Restored => "settings.tdp.status.restored",
        PowerLimitStatus.Changed => "settings.tdp.status.changed", PowerLimitStatus.Failed => "settings.tdp.status.failed",
        _ => "settings.tdp.status.initial",
    };

    private static string Watts(int milliwatts) => Loc.T("settings.tdp.watts", milliwatts / 1000.0);

    private static Button Button(SettingsToolkit ui, string key, Action click)
    {
        var button = ui.LinkButton(key, click);
        button.AutoSize = false;
        button.Width = TextRenderer.MeasureText(button.Text, ui.CtlFont).Width + ui.Sc(28);
        button.Height = ui.Sc(34);
        return button;
    }

    private sealed class PowerSlider : Control
    {
        private readonly SettingsToolkit _ui;
        private readonly int _minimum, _maximum;
        private int _value;
        public event Action? ValueChanged;
        public int Value
        {
            get => _value;
            set { int next = Math.Clamp(value, _minimum, _maximum); if (next == _value) return; _value = next; Invalidate(); ValueChanged?.Invoke(); }
        }
        public PowerSlider(SettingsToolkit ui, int minimum, int maximum, int value)
        {
            _ui = ui; _minimum = minimum; _maximum = maximum; _value = value;
            BackColor = ui.T.Card; TabStop = true; Cursor = Cursors.Hand; AccessibleRole = AccessibleRole.Slider;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, true);
        }
        private void Pick(int x) => Value = _minimum + (int)Math.Round(Math.Clamp((x - _ui.Sc(8)) / (float)Math.Max(1, Width - _ui.Sc(16)), 0, 1) * (_maximum - _minimum));
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { Focus(); Capture = true; Pick(e.X); } base.OnMouseDown(e); }
        protected override void OnMouseMove(MouseEventArgs e) { if (Capture) Pick(e.X); base.OnMouseMove(e); }
        protected override void OnMouseUp(MouseEventArgs e) { Capture = false; base.OnMouseUp(e); }
        protected override bool IsInputKey(Keys keyData) => keyData is Keys.Left or Keys.Right or Keys.Home or Keys.End || base.IsInputKey(keyData);
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Left) Value--; else if (e.KeyCode == Keys.Right) Value++;
            else if (e.KeyCode == Keys.Home) Value = _minimum; else if (e.KeyCode == Keys.End) Value = _maximum;
            else { base.OnKeyDown(e); return; }
            e.Handled = true;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            float left = _ui.Sc(8), right = Width - left, cy = Height / 2f;
            float x = left + (right - left) * (_value - _minimum) / Math.Max(1, _maximum - _minimum);
            using var rail = new Pen(_ui.T.Border, _ui.Sc(4)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            using var fill = new Pen(Enabled ? _ui.T.Accent : _ui.T.Text2, _ui.Sc(4)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(rail, left, cy, right, cy); g.DrawLine(fill, left, cy, x, cy);
            using var knob = new SolidBrush(Enabled ? _ui.T.Accent : _ui.T.Text2);
            float r = _ui.Sc(6); g.FillEllipse(knob, x - r, cy - r, r * 2, r * 2);
            if (Focused) ControlPaint.DrawFocusRectangle(g, ClientRectangle);
        }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    }
}
