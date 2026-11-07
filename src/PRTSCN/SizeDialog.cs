using System.Runtime.InteropServices;

namespace PRTSCN;

/// <summary>A small dark dialog that asks for an exact width × height in pixels.</summary>
internal sealed class SizeDialog : Form
{
    readonly NumericUpDown _w, _h;

    SizeDialog(Size current)
    {
        Text = "Exact size";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = new Font("Segoe UI", 9.5f);
        Padding = new Padding(16);

        _w = Number(current.Width);
        _h = Number(current.Height);
        var times = new Label { Text = "×", AutoSize = true, ForeColor = Theme.TextMuted, Margin = new Padding(6, 7, 6, 0) };
        var px = new Label { Text = "px", AutoSize = true, ForeColor = Theme.TextMuted, Margin = new Padding(6, 7, 0, 0) };
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 14) };
        row.Controls.AddRange(new Control[] { _w, times, _h, px });

        var ok = DarkButton("Apply", primary: true);
        ok.DialogResult = DialogResult.OK;
        var cancel = DarkButton("Cancel", primary: false);
        cancel.DialogResult = DialogResult.Cancel;
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, WrapContents = false };
        buttons.Controls.AddRange(new Control[] { ok, cancel });

        var hint = new Label { Text = "Width and height of the area to record.", AutoSize = true, ForeColor = Theme.TextMuted, Margin = new Padding(0, 0, 0, 8) };
        var layout = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Fill };
        layout.Controls.Add(hint);
        layout.Controls.Add(row);
        layout.Controls.Add(buttons);
        Controls.Add(layout);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        AcceptButton = ok;
        CancelButton = cancel;
    }

    static NumericUpDown Number(int value) => new()
    {
        Minimum = 16, Maximum = 8192, Value = Math.Clamp(value, 16, 8192), Width = 84,
        BackColor = Theme.Surface, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle, TextAlign = HorizontalAlignment.Right,
    };

    internal static Button DarkButton(string text, bool primary)
    {
        var b = new Button
        {
            Text = text, AutoSize = true, MinimumSize = new Size(84, 30), FlatStyle = FlatStyle.Flat,
            BackColor = primary ? Theme.Accent : Theme.Surface, ForeColor = primary ? Theme.OnAccent : Theme.Text,
            Margin = new Padding(8, 0, 0, 0), Cursor = Cursors.Hand,
        };
        b.FlatAppearance.BorderColor = primary ? Theme.Accent : Theme.Border;
        b.FlatAppearance.MouseOverBackColor = primary ? ControlPaint.Light(Theme.Accent, 0.2f) : Theme.SurfaceActive;
        return b;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        DarkTitleBar(Handle);
    }

    /// <summary>Ask Windows to draw this window's title bar dark (Windows 10 20H1 and later).</summary>
    internal static void DarkTitleBar(IntPtr hwnd)
    {
        int on = 1;
        try { DwmSetWindowAttribute(hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref on, sizeof(int)); } catch { }
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static Size? Ask(IWin32Window owner, Size current)
    {
        using var d = new SizeDialog(current);
        if (d.ShowDialog(owner) != DialogResult.OK) return null;
        return new Size((int)d._w.Value, (int)d._h.Value);
    }
}
