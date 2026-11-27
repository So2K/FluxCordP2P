namespace FluxCord;

public static class Th
{
    public static uint Bg = 0xFF0C0D10;
    public static uint Rail = 0xFF090A0D;
    public static uint Sidebar = 0xFF121318;
    public static uint Panel = 0xFF171920;
    public static uint Card = 0xFF1D2029;
    public static uint Hover = 0x16FFFFFF;
    public static uint Active = 0x24FFFFFF;
    public static uint Border = 0x18FFFFFF;
    public static uint Text = 0xFFF3F4F6;
    public static uint Sub = 0xFF9CA3AF;
    public static uint Dim = 0xFF6B7280;
    public static uint Brand = 0xFF5865F2;
    public static uint BrandHover = 0xFF6E79F7;
    public static uint Green = 0xFF23A55A;
    public static uint Red = 0xFFED4245;
    public static uint Amber = 0xFFF59E0B;
    public static uint Cyan = 0xFF38BDF8;

    public static void ApplyTheme(string? name)
    {
        switch ((name ?? "Obsidian").Trim())
        {
            case "Onyx": // Pure OLED Discord Onyx
                Bg = 0xFF050507;
                Rail = 0xFF020203;
                Sidebar = 0xFF0A0A0E;
                Panel = 0xFF0E0F14;
                Card = 0xFF15171F;
                Border = 0x1AFFFFFF;
                Text = 0xFFF8FAFC;
                Sub = 0xFF9CA3AF;
                Dim = 0xFF64748B;
                Brand = 0xFF5865F2;
                break;
            case "Ash": // Official Discord 2025 Ash
                Bg = 0xFF2B2D31;
                Rail = 0xFF1E1F22;
                Sidebar = 0xFF232428;
                Panel = 0xFF313338;
                Card = 0xFF383A40;
                Border = 0x1EFFFFFF;
                Text = 0xFFF2F3F5;
                Sub = 0xFFB5BAC1;
                Dim = 0xFF80848E;
                Brand = 0xFF5865F2;
                break;
            case "Midnight": // Deep Blue-Violet Midnight
                Bg = 0xFF0A0E1A;
                Rail = 0xFF060912;
                Sidebar = 0xFF0F1526;
                Panel = 0xFF151C32;
                Card = 0xFF1C2541;
                Border = 0x22818CF8;
                Text = 0xFFF1F5F9;
                Sub = 0xFF94A3B8;
                Dim = 0xFF64748B;
                Brand = 0xFF6366F1;
                break;
            default: // "Obsidian" (FluxCord Classic)
                Bg = 0xFF0C0D10;
                Rail = 0xFF090A0D;
                Sidebar = 0xFF121318;
                Panel = 0xFF171920;
                Card = 0xFF1D2029;
                Border = 0x18FFFFFF;
                Text = 0xFFF3F4F6;
                Sub = 0xFF9CA3AF;
                Dim = 0xFF6B7280;
                Brand = 0xFF5865F2;
                break;
        }
    }
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

    public static bool Button(int id, R r, string label, uint bg, uint text = 0, float radius = 6f, float fontSize = 13f, int weight = 600)
    {
        bool over = Hover(r);
        Gfx.FillRect(r, bg, radius);
        if (over) Gfx.FillRect(r, Th.Hover, radius);
        Gfx.DrawText(label, new R(r.X + 4, r.Y + (r.H - fontSize * 1.35f) * 0.5f, r.W - 8, fontSize * 1.5f), fontSize, text == 0 ? Th.Text : text, weight, 1);
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
