namespace FluxCord;

public static class Th
{
    public const uint Bg = 0xFF0C0D10;
    public const uint Rail = 0xFF090A0D;
    public const uint Sidebar = 0xFF121318;
    public const uint Panel = 0xFF171920;
    public const uint Card = 0xFF1D2029;
    public const uint Hover = 0x16FFFFFF;
    public const uint Active = 0x24FFFFFF;
    public const uint Border = 0x18FFFFFF;
    public const uint Text = 0xFFF3F4F6;
    public const uint Sub = 0xFF9CA3AF;
    public const uint Dim = 0xFF6B7280;
    public const uint Brand = 0xFF5865F2;
    public const uint BrandHover = 0xFF6E79F7;
    public const uint Green = 0xFF23A55A;
    public const uint Red = 0xFFED4245;
    public const uint Amber = 0xFFF59E0B;
    public const uint Cyan = 0xFF38BDF8;
}

public static class UI
{
    public static float MX = -1, MY = -1;
    public static bool Down, Pressed, Released, RightClicked;
    public static float Wheel;
    /// <summary>When true, background widgets ignore hover/click/right-click/wheel so modals & popups NEVER click through!</summary>
    public static bool InputBlocked;
    private static int _activeId;
    private static bool _wantHand;
    private static bool _animate;

    public static bool WantHand => _wantHand;
    public static void Animate() => _animate = true;
    public static bool TakeAnimate() { var a = _animate; _animate = false; return a; }
    public static void CancelDrag() { Down = false; _activeId = 0; }

    public static void BeginFrame() { _wantHand = false; InputBlocked = false; }
    public static void EndFrame()
    {
        if (Released) _activeId = 0;
        Pressed = Released = RightClicked = false;
        Wheel = 0;
        InputBlocked = false;
    }

    public static int Id(string key, int idx = 0) => HashCode.Combine(key, idx);
    public static bool Hover(R r) => !InputBlocked && r.Contains(MX, MY) && Gfx.Clip.Contains(MX, MY);
    public static bool RightClick(R r) => !InputBlocked && RightClicked && Hover(r);

    public static bool Click(int id, R r)
    {
        bool over = Hover(r);
        if (over) _wantHand = true;
        if (over && Pressed) _activeId = id;
        return !InputBlocked && Released && _activeId == id && over;
    }

    public static bool Button(int id, R r, string label, uint bg, uint text = Th.Text, float radius = 6f, float fontSize = 13f, int weight = 600)
    {
        bool over = Hover(r);
        Gfx.FillRect(r, bg, radius);
        if (over) Gfx.FillRect(r, Th.Hover, radius);
        Gfx.DrawText(label, new R(r.X + 4, r.Y + (r.H - fontSize * 1.35f) * 0.5f, r.W - 8, fontSize * 1.5f), fontSize, text, weight, 1);
        return Click(id, r);
    }

    public static bool Toggle(int id, R r, string label, bool value)
    {
        bool over = Hover(r);
        if (over) Gfx.FillRect(r, Th.Hover, 6);
        Gfx.DrawText(label, new R(r.X + 8, r.Y + 6, r.W - 56, 22), 13, Th.Text, 500);
        var pill = new R(r.Right - 44, r.Y + (r.H - 22) * 0.5f, 36, 22);
        Gfx.FillRect(pill, value ? Th.Green : 0xFF374151, 11);
        Gfx.FillCircle(value ? pill.Right - 11 : pill.X + 11, pill.Y + 11, 8, 0xFFFFFFFF);
        return Click(id, r);
    }

    public static bool Slider(int id, R r, string label, ref float val, float min, float max, string suffix = "")
    {
        Gfx.DrawText(label, new R(r.X, r.Y, r.W - 64, 18), 12.5f, Th.Sub, 500);
        Gfx.DrawText($"{val:0.##}{suffix}", new R(r.Right - 64, r.Y, 64, 18), 12.5f, Th.Text, 600, 2);

        var track = new R(r.X, r.Y + 24, r.W, 6);
        var hit = new R(r.X - 4, r.Y + 16, r.W + 8, 22);
        float t = Math.Clamp((val - min) / MathF.Max(1e-5f, max - min), 0f, 1f);
        Gfx.FillRect(track, 0xFF2A2D38, 3);
        Gfx.FillRect(new R(track.X, track.Y, track.W * t, track.H), Th.Brand, 3);
        Gfx.FillCircle(track.X + track.W * t, track.Y + 3, 7, 0xFFFFFFFF);

        bool over = Hover(hit);
        if (over) _wantHand = true;
        if (over && Pressed) _activeId = id;
        if (!InputBlocked && _activeId == id && Down)
        {
            float nt = Math.Clamp((MX - track.X) / MathF.Max(1f, track.W), 0f, 1f);
            float nv = min + nt * (max - min);
            if (MathF.Abs(nv - val) > 1e-4f)
            {
                val = nv;
                return true;
            }
        }
        return false;
    }
}
