using PRTSCN.Core;

namespace PRTSCN;

/// <summary>Shows a finished scrolling capture: copy it, save it, or both.</summary>
internal sealed class ScrollResultForm : Form
{
    readonly Bitmap _image;
    readonly AppSettings _settings;
    readonly Action<string, string, string?> _notify;

    public ScrollResultForm(Bitmap image, AppSettings settings, string summary, Action<string, string, string?> notify)
    {
        _image = image;
        _settings = settings;
        _notify = notify;
        Text = $"Scrolling capture – {image.Width} × {image.Height}";
        Icon = Theme.AppIcon(32);
        Font = SystemFonts.MessageBoxFont ?? Control.DefaultFont;
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;
        var work = Screen.FromPoint(Cursor.Position).WorkingArea;
        Size = new Size(Math.Min(work.Width * 8 / 10, image.Width + 60), work.Height * 85 / 100);

        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.FromArgb(0x2a, 0x2d, 0x36) };
        scroll.Controls.Add(new PictureBox { Image = image, SizeMode = PictureBoxSizeMode.AutoSize, Location = new Point(0, 0) });

        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
        Button B(string t, Action a) { var b = new Button { Text = t, AutoSize = true, MinimumSize = new Size(90, 0) }; b.Click += (_, _) => a(); return b; }
        bar.Controls.Add(B("Close", Close));
        bar.Controls.Add(B("Save as…", SaveAs));
        bar.Controls.Add(B("Save to folder", SaveToFolder));
        var copy = B("Copy", Copy);
        bar.Controls.Add(copy);
        bar.Controls.Add(new Label { Text = summary, AutoSize = true, Margin = new Padding(0, 8, 16, 0), ForeColor = SystemColors.GrayText });
        AcceptButton = copy;

        Controls.Add(scroll);
        Controls.Add(bar);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Activate();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Control && e.KeyCode == Keys.C) { Copy(); e.Handled = true; }
        else if (e.Control && e.KeyCode == Keys.S) { SaveAs(); e.Handled = true; }
        else if (e.KeyCode == Keys.Escape) Close();
    }

    void Copy()
    {
        try
        {
            ImageOutput.CopyToClipboard(_image);
            _notify("Copied", $"{_image.Width} × {_image.Height} scrolling capture is on the clipboard.", null);
            Close();
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
            MessageBox.Show(this, "Another app is holding the clipboard. Try again.", "PRTSCN", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    void SaveToFolder()
    {
        try
        {
            var path = ImageOutput.SaveToFolder(_image, _settings);
            _notify("Saved", Path.GetFileName(path), path);
            Close();
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
            MessageBox.Show(this, $"Could not save:\n{ex.Message}", "PRTSCN", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    void SaveAs()
    {
        Directory.CreateDirectory(_settings.SaveFolder);
        using var dlg = new SaveFileDialog
        {
            Title = "Save scrolling capture",
            InitialDirectory = _settings.SaveFolder,
            FileName = FileNaming.DefaultName(DateTime.Now, _settings.Format),
            Filter = "PNG image (*.png)|*.png|JPEG image (*.jpg)|*.jpg",
            FilterIndex = _settings.Format == Core.ImageFormat.Jpg ? 2 : 1,
            OverwritePrompt = true,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var fmt = FileNaming.FormatFromPath(dlg.FileName, dlg.FilterIndex == 2 ? Core.ImageFormat.Jpg : Core.ImageFormat.Png);
            ImageOutput.Save(_image, dlg.FileName, fmt, _settings.JpegQuality);
            _notify("Saved", Path.GetFileName(dlg.FileName), dlg.FileName);
            Close();
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
            MessageBox.Show(this, $"Could not save:\n{ex.Message}", "PRTSCN", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
        _image.Dispose();
    }
}
