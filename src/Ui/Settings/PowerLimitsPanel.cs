using System.Drawing.Drawing2D;
using XiControl.Localization;
using XiControl.SystemIntegration;

namespace XiControl.Ui.Settings;

/// <summary>Компактные карточки PL1/PL2: снимок, выбранные ватты и явное применение.</summary>
internal static class PowerLimitsPanel
{
    private static bool _showDetails;

    public static void Add(SettingsPane pane, SettingsToolkit ui, SettingsActions act, Action rebuild)
    {
        var view = act.GetPowerLimits();
        bool busy = view.Status == PowerLimitStatus.Busy;
        ui.AddGroup(pane, "settings.tdp.group");
        var check = Button(ui, busy ? "settings.tdp.busy" : "settings.tdp.read", () => act.RefreshPowerLimits(rebuild));
        check.Enabled = !busy;
        pane.Controls.Add(ui.Row(Loc.T("settings.tdp.driver"), Loc.T(StatusKey(view.Status)), check));
        if (view.Status == PowerLimitStatus.Failed) ui.AddNote(pane, "settings.tdp.error");
        if (view.Current is not { } current)
        {
            ui.AddNote(pane, "settings.tdp.note");
            if (view.Active) Recovery(pane, ui, act, rebuild, busy);
            return;
        }

        bool editable = view.CanApply && !busy && view.Baseline is { Pl1Milliwatts: >= 10000, Pl2Milliwatts: >= 10000 };
        int max1 = Math.Clamp((view.Baseline?.Pl1Milliwatts ?? current.Pl1Milliwatts) / 1000, 10, 250);
        int max2 = Math.Clamp((view.Baseline?.Pl2Milliwatts ?? current.Pl2Milliwatts) / 1000, 10, 250);
        int gap = ui.Sc(10), cardW = (ui.RowW - gap) / 2;
        var first = LimitCard(ui, cardW, "settings.tdp.pl1", "settings.tdp.target.pl1", current.Pl1Milliwatts, max1, editable, out var pl1);
        var second = LimitCard(ui, cardW, "settings.tdp.pl2", "settings.tdp.target.pl2", current.Pl2Milliwatts, max2, editable, out var pl2);
        var cards = new Panel { Width = ui.RowW, Height = Math.Max(first.Height, second.Height), BackColor = ui.T.WinBg, Margin = new Padding(0, ui.Sc(7), 0, ui.Sc(8)) };
        first.Height = second.Height = cards.Height;
        second.Left = cardW + gap;
        cards.Controls.Add(first);
        cards.Controls.Add(second);
        pane.Controls.Add(cards);

        string info = Loc.T("settings.tdp.tdp") + ": " + (current.TdpMilliwatts is int tdp ? Watts(tdp) : "—")
            + "  ·  " + Loc.T("settings.tdp.lock") + ": " + Loc.T(current.Lock == 0 ? "settings.tdp.unlocked" : "settings.tdp.locked");
        int infoH = TextRenderer.MeasureText(info, ui.DescFont, new Size(ui.RowW, 0), TextFormatFlags.WordBreak).Height;
        pane.Controls.Add(new Label { Text = info, Font = ui.DescFont, ForeColor = ui.T.Text2, BackColor = ui.T.WinBg,
            Size = new Size(ui.RowW, infoH + ui.Sc(3)), Margin = new Padding(0, 0, 0, ui.Sc(7)) });

        var apply = Button(ui, "settings.tdp.apply", () =>
        {
            if (pl1.Value > pl2.Value) return;
            if (MessageBox.Show(Loc.T("settings.tdp.confirm"), Loc.T("settings.tdp.group"),
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            act.ApplyPowerLimits((int)pl1.Value, (int)pl2.Value, rebuild);
        });
        apply.BackColor = ui.T.Accent;
        apply.ForeColor = ui.T.Dark ? ui.T.WinBg : ui.T.Card;
        void ValidatePair(object? sender, EventArgs e) => apply.Enabled = editable && pl1.Value <= pl2.Value;
        pl1.ValueChanged += ValidatePair;
        pl2.ValueChanged += ValidatePair;
        ValidatePair(null, EventArgs.Empty);
        var buttons = new FlowLayoutPanel { Width = ui.RowW, AutoSize = true, WrapContents = true,
            BackColor = ui.T.WinBg, Margin = new Padding(0, 0, 0, ui.Sc(5)) };
        buttons.Controls.Add(apply);
        var restore = Button(ui, "settings.tdp.restore", () => act.RestorePowerLimits(rebuild));
        restore.Enabled = view.Active && !busy;
        buttons.Controls.Add(restore);
        pane.Controls.Add(buttons);
        if (!current.CanControl) ui.AddNote(pane, "settings.tdp.unsupported");
        ui.AddNote(pane, "settings.tdp.note");
        var details = Button(ui, "settings.tdp.details", () => { _showDetails = !_showDetails; rebuild(); });
        pane.Controls.Add(details);
        if (_showDetails) ui.AddNote(pane, "settings.tdp.session");
    }

    private static Panel LimitCard(SettingsToolkit ui, int width, string captionKey, string targetKey,
        int milliwatts, int maximum, bool enabled, out NumericUpDown number)
    {
        int pad = ui.Sc(16), textW = width - pad * 2;
        string caption = Loc.T(captionKey);
        int captionH = TextRenderer.MeasureText(caption, ui.DescFont, new Size(textW, 0), TextFormatFlags.WordBreak).Height;
        var card = new Panel { Width = width, Height = ui.Sc(157) + captionH, BackColor = ui.T.Card };
        card.Paint += (_, e) => ui.PaintCardBorder(e.Graphics, card.Width, card.Height);
        card.Controls.Add(new Label { Text = caption, ForeColor = ui.T.Text2, Font = ui.DescFont,
            Location = new Point(pad, ui.Sc(13)), Size = new Size(textW, captionH + ui.Sc(2)) });
        card.Controls.Add(new Label { Text = Watts(milliwatts), ForeColor = ui.T.Text, Font = ui.HeadFont,
            Location = new Point(pad, ui.Sc(21) + captionH), Size = new Size(textW, ui.Sc(32)) });
        string target = Loc.T(targetKey);
        number = new NumericUpDown
        {
            Minimum = 10, Maximum = maximum, Value = Math.Clamp(milliwatts / 1000, 10, maximum), Increment = 1,
            Width = ui.Sc(76), Font = ui.CtlFont, BackColor = ui.T.Field, ForeColor = ui.T.Text,
            BorderStyle = BorderStyle.FixedSingle, Enabled = enabled, AccessibleName = target,
            Location = new Point(width - pad - ui.Sc(76), ui.Sc(65) + captionH),
        };
        card.Controls.Add(number);
        card.Controls.Add(new Label { Text = target, Font = ui.DescFont, ForeColor = ui.T.Text2,
            Location = new Point(pad, ui.Sc(67) + captionH), Size = new Size(textW - number.Width - ui.Sc(6), ui.Sc(31)) });
        var slider = new PowerSlider(ui, 10, maximum, (int)number.Value)
        {
            Location = new Point(pad, ui.Sc(103) + captionH), Size = new Size(textW, ui.Sc(27)),
            Enabled = enabled, AccessibleName = target,
        };
        var input = number;
        input.ValueChanged += (_, _) => slider.Value = (int)input.Value;
        slider.ValueChanged += () => input.Value = slider.Value;
        card.Controls.Add(slider);
        string range = Watts(10000) + " — " + Watts(maximum * 1000);
        card.Controls.Add(new Label { Text = range, Font = ui.DescFont, ForeColor = ui.T.Text2,
            Location = new Point(pad, ui.Sc(131) + captionH), Size = new Size(textW, ui.Sc(20)) });
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
