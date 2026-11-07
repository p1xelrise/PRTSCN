using System.Drawing;

namespace PRTSCN.Core;

/// <summary>Which keystrokes the recording shows. Typing can reveal passwords, so it is opt-in.</summary>
public enum KeyDisplay { Off, Shortcuts, All }

/// <summary>
/// "Follow the mouse": a fixed-size view over a larger source that pans after the pointer.
/// The view stays still while the pointer is in its middle part and glides when it nears an edge,
/// so the video stays calm.
/// </summary>
public sealed class FollowCamera
{
    /// <summary>Fraction of the view, from each edge, where the pointer starts the view moving.</summary>
    public const double EdgeZone = 0.2;
    /// <summary>How fast the view catches up, per second (exponential).</summary>
    public const double Speed = 6;

    readonly Size _view;
    readonly Rectangle _bounds;
    PointF _center;
    PointF _goal;

    public FollowCamera(Rectangle start, Rectangle bounds)
    {
        _view = new Size(Math.Min(start.Width, bounds.Width), Math.Min(start.Height, bounds.Height));
        _bounds = bounds;
        _center = _goal = new PointF(start.X + start.Width / 2f, start.Y + start.Height / 2f);
        _center = Clamp(_center);
        _goal = _center;
    }

    public Rectangle View => ViewAt(_center);

    /// <summary>Move toward the pointer. <paramref name="seconds"/> is the time since the last update.</summary>
    public Rectangle Update(Point pointer, double seconds)
    {
        var v = ViewAt(_center);
        int mx = (int)(v.Width * EdgeZone), my = (int)(v.Height * EdgeZone);
        var calm = Rectangle.Inflate(v, -mx, -my);
        if (!calm.Contains(pointer))
        {
            // aim so the pointer ends up just inside the calm zone
            float gx = _center.X, gy = _center.Y;
            if (pointer.X < calm.Left) gx -= calm.Left - pointer.X;
            else if (pointer.X >= calm.Right) gx += pointer.X - calm.Right + 1;
            if (pointer.Y < calm.Top) gy -= calm.Top - pointer.Y;
            else if (pointer.Y >= calm.Bottom) gy += pointer.Y - calm.Bottom + 1;
            _goal = Clamp(new PointF(gx, gy));
        }
        double k = 1 - Math.Exp(-Speed * Math.Max(0, seconds));
        _center = Clamp(new PointF((float)(_center.X + (_goal.X - _center.X) * k), (float)(_center.Y + (_goal.Y - _center.Y) * k)));
        return ViewAt(_center);
    }

    Rectangle ViewAt(PointF c)
    {
        int x = (int)Math.Round(c.X - _view.Width / 2f), y = (int)Math.Round(c.Y - _view.Height / 2f);
        x = Math.Clamp(x, _bounds.Left, _bounds.Right - _view.Width);
        y = Math.Clamp(y, _bounds.Top, _bounds.Bottom - _view.Height);
        return new Rectangle(x, y, _view.Width, _view.Height);
    }

    PointF Clamp(PointF c) => new(
        Math.Clamp(c.X, _bounds.Left + _view.Width / 2f, _bounds.Right - _view.Width / 2f),
        Math.Clamp(c.Y, _bounds.Top + _view.Height / 2f, _bounds.Bottom - _view.Height / 2f));
}

/// <summary>Text for the keystroke overlay ("Ctrl + Shift + S", "Enter", typed letters).</summary>
public static class KeystrokeText
{
    // Windows virtual-key codes
    const int VK_BACK = 0x08, VK_TAB = 0x09, VK_RETURN = 0x0D, VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12,
        VK_PAUSE = 0x13, VK_CAPITAL = 0x14, VK_ESCAPE = 0x1B, VK_SPACE = 0x20, VK_PRIOR = 0x21, VK_NEXT = 0x22, VK_END = 0x23,
        VK_HOME = 0x24, VK_LEFT = 0x25, VK_UP = 0x26, VK_RIGHT = 0x27, VK_DOWN = 0x28, VK_SNAPSHOT = 0x2C, VK_INSERT = 0x2D,
        VK_DELETE = 0x2E, VK_LWIN = 0x5B, VK_RWIN = 0x5C, VK_APPS = 0x5D, VK_NUMPAD0 = 0x60, VK_NUMPAD9 = 0x69,
        VK_F1 = 0x70, VK_F24 = 0x87, VK_LSHIFT = 0xA0, VK_RMENU = 0xA5;

    public static bool IsModifier(int vk) => vk is VK_SHIFT or VK_CONTROL or VK_MENU or VK_LWIN or VK_RWIN or VK_CAPITAL
        || (vk >= VK_LSHIFT && vk <= VK_RMENU);

    /// <summary>
    /// The label for a key press, or null when it is not shown in this mode.
    /// <paramref name="typed"/> is true for a plain character (letters, digits, space) that can be
    /// joined onto the previous typed text.
    /// </summary>
    public static string? Describe(int vk, bool ctrl, bool alt, bool shift, bool win, KeyDisplay mode, out bool typed)
    {
        typed = false;
        if (mode == KeyDisplay.Off || IsModifier(vk)) return null;
        bool combo = ctrl || alt || win;
        bool fkey = vk >= VK_F1 && vk <= VK_F24;
        if (mode == KeyDisplay.Shortcuts && !combo && !fkey && vk != VK_ESCAPE) return null;

        if (!combo)
        {
            string? ch = Character(vk, shift);
            if (ch != null) { typed = true; return ch; }
        }
        var parts = new List<string>(5);
        if (ctrl) parts.Add("Ctrl");
        if (win) parts.Add("Win");
        if (alt) parts.Add("Alt");
        if (shift) parts.Add("Shift");
        parts.Add(Name(vk));
        return string.Join(" + ", parts);
    }

    /// <summary>A plain typed character, or null for keys that are not one.</summary>
    static string? Character(int vk, bool shift)
    {
        if (vk >= 'A' && vk <= 'Z') return ((char)(shift ? vk : vk + 32)).ToString();
        if (vk >= '0' && vk <= '9' && !shift) return ((char)vk).ToString();
        if (vk >= VK_NUMPAD0 && vk <= VK_NUMPAD9) return ((char)('0' + vk - VK_NUMPAD0)).ToString();
        if (vk == VK_SPACE) return " ";
        return null;
    }

    public static string Name(int vk) => vk switch
    {
        VK_BACK => "Backspace",
        VK_TAB => "Tab",
        VK_RETURN => "Enter",
        VK_PAUSE => "Pause",
        VK_ESCAPE => "Esc",
        VK_SPACE => "Space",
        VK_PRIOR => "Page Up",
        VK_NEXT => "Page Down",
        VK_END => "End",
        VK_HOME => "Home",
        VK_LEFT => "←",
        VK_UP => "↑",
        VK_RIGHT => "→",
        VK_DOWN => "↓",
        VK_SNAPSHOT => "Print Screen",
        VK_INSERT => "Insert",
        VK_DELETE => "Delete",
        VK_APPS => "Menu",
        >= VK_F1 and <= VK_F24 => "F" + (vk - VK_F1 + 1),
        >= 'A' and <= 'Z' => ((char)vk).ToString(),
        >= '0' and <= '9' => ((char)vk).ToString(),
        >= VK_NUMPAD0 and <= VK_NUMPAD9 => "Num " + (vk - VK_NUMPAD0),
        0xBA => ";",
        0xBB => "=",
        0xBC => ",",
        0xBD => "-",
        0xBE => ".",
        0xBF => "/",
        0xC0 => "`",
        0xDB => "[",
        0xDC => "\\",
        0xDD => "]",
        0xDE => "'",
        _ => $"Key {vk}",
    };
}

/// <summary>Sizes and timing for the drawn overlays, relative to the video frame.</summary>
public static class OverlayMath
{
    /// <summary>One "design pixel" in output pixels: 1 at 1280 × 720, bigger for larger videos.</summary>
    public static float Unit(Size output) =>
        Math.Clamp(Math.Min(output.Width / 1280f, output.Height / 720f), 0.6f, 2.5f);

    public const double ClickRippleSeconds = 0.45;
    public const double KeyVisibleSeconds = 1.6;
    public const double KeyFadeSeconds = 0.4;
    /// <summary>Typed characters within this time join into one label.</summary>
    public const double TypingJoinSeconds = 1.0;

    /// <summary>Ease-out for the click ripple: fast start, soft end.</summary>
    public static float EaseOut(double t) => (float)(1 - Math.Pow(1 - Math.Clamp(t, 0, 1), 3));

    /// <summary>Opacity of a keystroke label of the given age.</summary>
    public static float KeyOpacity(double age) =>
        age < KeyVisibleSeconds ? 1f : (float)Math.Clamp(1 - (age - KeyVisibleSeconds) / KeyFadeSeconds, 0, 1);

    /// <summary>A screen point in the video frame: relative to the recorded view, then scaled.</summary>
    public static PointF ToOutput(Point screen, Point sourceOrigin, Rectangle view, float scale) =>
        new((screen.X - sourceOrigin.X - view.X) * scale, (screen.Y - sourceOrigin.Y - view.Y) * scale);
}
