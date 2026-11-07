using PRTSCN.Core;

namespace PRTSCN;

/// <summary>Set up a recording that starts by itself at a time of day and stops after some minutes.</summary>
internal sealed class ScheduleForm : Form
{
    readonly DateTimePicker _time;
    readonly NumericUpDown _minutes;
    readonly RadioButton _screen, _sound;
    readonly Label _when;

    public RecordSchedule? Result { get; private set; }

    public ScheduleForm(RecordSchedule? current)
    {
        float s = DeviceDpi / 96f;
        int S(float v) => (int)Math.Round(v * s);
        Text = "Schedule a recording";
        Icon = Theme.AppIcon(32);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = Theme.UiFont(Theme.TypeBase, false, s);
        ClientSize = new Size(S(380), S(250));
        SizeDialog.DarkTitleBar(Handle);

        int pad = S(20), y = pad, labelW = S(110);
        Label L(string text, int top) { var l = new Label { Text = text, AutoSize = true, Location = new Point(pad, top + S(4)), ForeColor = Theme.TextMuted }; Controls.Add(l); return l; }

        var start = current?.Start > DateTime.Now ? current.Start : DateTime.Now.AddMinutes(5);
        L("Start at", y);
        _time = new DateTimePicker { Format = DateTimePickerFormat.Custom, CustomFormat = "HH:mm", ShowUpDown = true, Value = start, Location = new Point(pad + labelW, y), Width = S(90) };
        _time.ValueChanged += (_, _) => UpdateWhen();
        Controls.Add(_time);
        _when = new Label { AutoSize = true, Location = new Point(_time.Right + S(10), y + S(4)), ForeColor = Theme.TextMuted };
        Controls.Add(_when);
        y += S(40);

        L("Record for", y);
        _minutes = new NumericUpDown { Minimum = 1, Maximum = 600, Value = current?.Minutes ?? 10, Location = new Point(pad + labelW, y), Width = S(70), BackColor = Theme.Surface, ForeColor = Theme.Text };
        Controls.Add(_minutes);
        Controls.Add(new Label { Text = "minutes", AutoSize = true, Location = new Point(_minutes.Right + S(8), y + S(4)), ForeColor = Theme.TextMuted });
        y += S(40);

        L("What", y);
        _screen = new RadioButton { Text = "Full screen (the screen with the mouse)", AutoSize = true, Location = new Point(pad + labelW, y + S(2)), Checked = current?.SoundOnly != true };
        _sound = new RadioButton { Text = "Sound only", AutoSize = true, Location = new Point(pad + labelW, y + S(28)), Checked = current?.SoundOnly == true };
        Controls.Add(_screen);
        Controls.Add(_sound);
        y += S(70);

        var ok = new Button { Text = current != null ? "Update" : "Schedule", DialogResult = DialogResult.OK, FlatStyle = FlatStyle.Flat, BackColor = Theme.Accent, ForeColor = Theme.OnAccent, Size = new Size(S(100), S(32)) };
        ok.FlatAppearance.BorderSize = 0;
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, FlatStyle = FlatStyle.Flat, BackColor = Theme.Surface, ForeColor = Theme.Text, Size = new Size(S(90), S(32)) };
        cancel.FlatAppearance.BorderColor = Theme.Border;
        cancel.Location = new Point(ClientSize.Width - pad - cancel.Width, ClientSize.Height - pad - cancel.Height);
        ok.Location = new Point(cancel.Left - S(8) - ok.Width, cancel.Top);
        Controls.Add(ok);
        Controls.Add(cancel);
        AcceptButton = ok;
        CancelButton = cancel;
        ok.Click += (_, _) => Result = new RecordSchedule { Start = Next(), Minutes = (int)_minutes.Value, SoundOnly = _sound.Checked };
        UpdateWhen();
    }

    DateTime Next() => RecordSchedule.NextAt(DateTime.Now, new TimeSpan(_time.Value.Hour, _time.Value.Minute, 0));

    void UpdateWhen() => _when.Text = RecordSchedule.Describe(Next(), DateTime.Now).Split(' ')[0];
}
