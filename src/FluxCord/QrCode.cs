using System.Text;

namespace FluxCord;

/// <summary>
/// Compact, zero-dependency ISO/IEC 18004 QR Code encoder (Byte mode, ECC Level M, Versions 1..10).
/// NativeAOT-friendly, zero allocations beyond the returned bool[,] matrix.
/// </summary>
public static class QrCode
{
    // Total codewords, EC codewords per block, (blocksG1, dataPerBlockG1, blocksG2, dataPerBlockG2) for ECC Level M, Versions 1..10
    private static readonly (int Total, int EcPerBlock, int G1Blocks, int G1Data, int G2Blocks, int G2Data)[] EccM =
    [
        default, // 0 unused
        (26,  10, 1, 16, 0, 0),  // V1 (21x21) max 14 bytes
        (44,  16, 1, 28, 0, 0),  // V2 (25x25) max 26 bytes
        (70,  26, 1, 44, 0, 0),  // V3 (29x29) max 42 bytes
        (100, 18, 2, 32, 0, 0),  // V4 (33x33) max 62 bytes
        (134, 24, 2, 43, 0, 0),  // V5 (37x37) max 84 bytes (Discord RA URL ~67 bytes fits here!)
        (172, 16, 4, 27, 0, 0),  // V6 (41x41) max 106 bytes
        (196, 18, 4, 31, 0, 0),  // V7 (45x45) max 122 bytes
        (242, 22, 2, 38, 2, 39), // V8 (49x49) max 152 bytes
        (292, 22, 3, 36, 2, 37), // V9 (53x53) max 180 bytes
        (346, 26, 4, 43, 1, 44), // V10 (57x57) max 213 bytes
    ];

    private static readonly int[][] AlignCoords =
    [
        [],
        [],
        [6, 18],
        [6, 22],
        [6, 26],
        [6, 30],
        [6, 34],
        [6, 22, 38],
        [6, 24, 42],
        [6, 26, 46],
        [6, 28, 50],
    ];

    // Precomputed format info bits (15-bit BCH + XOR 0x5412) for ECC Level M (00) and masks 0..7
    private static readonly int[] FormatBitsM =
    [
        0x5412, 0x5125, 0x5E7C, 0x5B4B, 0x45F9, 0x40CE, 0x4F97, 0x4AA0
    ];

    // Precomputed version info bits (18-bit Golay) for Versions 7..10
    private static readonly int[] VersionBits =
    [
        0, 0, 0, 0, 0, 0, 0,
        0x07C94, 0x085BC, 0x09A99, 0x0A4D3
    ];

    public static bool[,] Encode(string text)
    {
        byte[] data = Encoding.UTF8.GetBytes(text);
        int ver = 1;
        for (; ver <= 10; ver++)
        {
            var e = EccM[ver];
            int capBytes = e.G1Blocks * e.G1Data + e.G2Blocks * e.G2Data;
            int headerBits = 4 + (ver <= 9 ? 8 : 16);
            if ((headerBits + data.Length * 8 + 7) / 8 <= capBytes) break;
        }
        if (ver > 10) throw new ArgumentException("QR payload too long");

        var spec = EccM[ver];
        int totalDataBytes = spec.G1Blocks * spec.G1Data + spec.G2Blocks * spec.G2Data;

        // 1. Build data bit stream (Byte mode = 0100)
        var bits = new List<bool>(totalDataBytes * 8);
        AppendBits(bits, 0b0100, 4);
        AppendBits(bits, data.Length, ver <= 9 ? 8 : 16);
        foreach (byte b in data) AppendBits(bits, b, 8);
        int term = Math.Min(4, totalDataBytes * 8 - bits.Count);
        AppendBits(bits, 0, term);
        while (bits.Count % 8 != 0) bits.Add(false);

        byte[] dataCw = new byte[totalDataBytes];
        int cwIdx = 0;
        for (int i = 0; i < bits.Count; i += 8)
        {
            int val = 0;
            for (int b = 0; b < 8; b++) val = (val << 1) | (bits[i + b] ? 1 : 0);
            dataCw[cwIdx++] = (byte)val;
        }
        byte[] pad = [0xEC, 0x11];
        for (int p = 0; cwIdx < totalDataBytes; p++) dataCw[cwIdx++] = pad[p & 1];

        // 2. Split into RS blocks and interleave data + EC codewords
        int numBlocks = spec.G1Blocks + spec.G2Blocks;
        byte[][] dataBlocks = new byte[numBlocks][];
        byte[][] ecBlocks = new byte[numBlocks][];
        byte[] rsGen = RsGenerator(spec.EcPerBlock);

        int offset = 0;
        for (int b = 0; b < numBlocks; b++)
        {
            int len = b < spec.G1Blocks ? spec.G1Data : spec.G2Data;
            dataBlocks[b] = new byte[len];
            Array.Copy(dataCw, offset, dataBlocks[b], 0, len);
            offset += len;
            ecBlocks[b] = RsRemainder(dataBlocks[b], rsGen);
        }

        byte[] finalCw = new byte[spec.Total];
        int fi = 0;
        int maxDataLen = Math.Max(spec.G1Data, spec.G2Data);
        for (int i = 0; i < maxDataLen; i++)
            for (int b = 0; b < numBlocks; b++)
                if (i < dataBlocks[b].Length) finalCw[fi++] = dataBlocks[b][i];
        for (int i = 0; i < spec.EcPerBlock; i++)
            for (int b = 0; b < numBlocks; b++)
                finalCw[fi++] = ecBlocks[b][i];

        // 3. Draw function patterns
        int size = ver * 4 + 17;
        bool[,] mod = new bool[size, size];
        bool[,] isFunc = new bool[size, size];

        DrawFinder(mod, isFunc, 0, 0);
        DrawFinder(mod, isFunc, size - 7, 0);
        DrawFinder(mod, isFunc, 0, size - 7);

        // Timing patterns
        for (int i = 8; i < size - 8; i++)
        {
            SetFunc(mod, isFunc, i, 6, (i & 1) == 0);
            SetFunc(mod, isFunc, 6, i, (i & 1) == 0);
        }

        // Alignment patterns
        int[] ac = AlignCoords[ver];
        foreach (int cy in ac)
        {
            foreach (int cx in ac)
            {
                if (isFunc[cy, cx]) continue;
                for (int dy = -2; dy <= 2; dy++)
                    for (int dx = -2; dx <= 2; dx++)
                        SetFunc(mod, isFunc, cx + dx, cy + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
            }
        }

        // Dark module & reserve format/version areas
        SetFunc(mod, isFunc, 8, size - 8, true);
        DrawFormat(mod, isFunc, 0);
        if (ver >= 7) DrawVersion(mod, isFunc, ver);

        // 4. Place data bits in upward/downward zigzag
        int bitIdx = 0;
        int totalBits = finalCw.Length * 8;
        for (int right = size - 1; right >= 1; right -= 2)
        {
            if (right == 6) right = 5;
            bool upward = ((right + 1) & 2) == 0;
            for (int vert = 0; vert < size; vert++)
            {
                int y = upward ? size - 1 - vert : vert;
                for (int j = 0; j < 2; j++)
                {
                    int x = right - j;
                    if (!isFunc[y, x])
                    {
                        bool dark = false;
                        if (bitIdx < totalBits)
                            dark = ((finalCw[bitIdx >> 3] >> (7 - (bitIdx & 7))) & 1) != 0;
                        mod[y, x] = dark;
                        bitIdx++;
                    }
                }
            }
        }

        // 5. Choose best mask pattern (0..7)
        int bestMask = 0;
        int bestPenalty = int.MaxValue;
        for (int m = 0; m < 8; m++)
        {
            ApplyMask(mod, isFunc, m);
            DrawFormat(mod, isFunc, m);
            int p = Penalty(mod);
            if (p < bestPenalty) { bestPenalty = p; bestMask = m; }
            ApplyMask(mod, isFunc, m); // revert
        }
        ApplyMask(mod, isFunc, bestMask);
        DrawFormat(mod, isFunc, bestMask);
        return mod;
    }

    private static void AppendBits(List<bool> bits, int val, int len)
    {
        for (int i = len - 1; i >= 0; i--) bits.Add(((val >> i) & 1) != 0);
    }

    private static void SetFunc(bool[,] mod, bool[,] isFunc, int x, int y, bool dark)
    {
        int n = mod.GetLength(0);
        if (x < 0 || y < 0 || x >= n || y >= n) return;
        mod[y, x] = dark;
        isFunc[y, x] = true;
    }

    private static void DrawFinder(bool[,] mod, bool[,] isFunc, int ox, int oy)
    {
        for (int dy = -1; dy <= 7; dy++)
        {
            for (int dx = -1; dx <= 7; dx++)
            {
                int dist = Math.Max(Math.Abs(dx - 3), Math.Abs(dy - 3));
                bool dark = dist != 2 && dist != 4;
                SetFunc(mod, isFunc, ox + dx, oy + dy, dark);
            }
        }
    }

    private static void DrawFormat(bool[,] mod, bool[,] isFunc, int mask)
    {
        int n = mod.GetLength(0);
        int bits = FormatBitsM[mask];
        for (int i = 0; i <= 5; i++) SetFunc(mod, isFunc, 8, i, ((bits >> i) & 1) != 0);
        SetFunc(mod, isFunc, 8, 7, ((bits >> 6) & 1) != 0);
        SetFunc(mod, isFunc, 8, 8, ((bits >> 7) & 1) != 0);
        SetFunc(mod, isFunc, 7, 8, ((bits >> 8) & 1) != 0);
        for (int i = 9; i < 15; i++) SetFunc(mod, isFunc, 14 - i, 8, ((bits >> i) & 1) != 0);

        for (int i = 0; i < 8; i++) SetFunc(mod, isFunc, n - 1 - i, 8, ((bits >> i) & 1) != 0);
        for (int i = 8; i < 15; i++) SetFunc(mod, isFunc, 8, n - 15 + i, ((bits >> i) & 1) != 0);
        SetFunc(mod, isFunc, 8, n - 8, true);
    }

    private static void DrawVersion(bool[,] mod, bool[,] isFunc, int ver)
    {
        int n = mod.GetLength(0);
        int bits = VersionBits[ver];
        for (int i = 0; i < 18; i++)
        {
            bool dark = ((bits >> i) & 1) != 0;
            int a = n - 11 + (i % 3);
            int b = i / 3;
            SetFunc(mod, isFunc, a, b, dark);
            SetFunc(mod, isFunc, b, a, dark);
        }
    }

    private static void ApplyMask(bool[,] mod, bool[,] isFunc, int mask)
    {
        int n = mod.GetLength(0);
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                if (isFunc[y, x]) continue;
                bool inv = mask switch
                {
                    0 => (x + y) % 2 == 0,
                    1 => y % 2 == 0,
                    2 => x % 3 == 0,
                    3 => (x + y) % 3 == 0,
                    4 => (x / 3 + y / 2) % 2 == 0,
                    5 => (x * y) % 2 + (x * y) % 3 == 0,
                    6 => ((x * y) % 2 + (x * y) % 3) % 2 == 0,
                    _ => ((x + y) % 2 + (x * y) % 3) % 2 == 0,
                };
                if (inv) mod[y, x] = !mod[y, x];
            }
        }
    }

    private static int Penalty(bool[,] mod)
    {
        int n = mod.GetLength(0), pen = 0;
        for (int y = 0; y < n; y++)
        {
            int run = 1;
            for (int x = 1; x < n; x++)
            {
                if (mod[y, x] == mod[y, x - 1]) { run++; if (run == 5) pen += 3; else if (run > 5) pen++; }
                else run = 1;
            }
        }
        for (int x = 0; x < n; x++)
        {
            int run = 1;
            for (int y = 1; y < n; y++)
            {
                if (mod[y, x] == mod[y - 1, x]) { run++; if (run == 5) pen += 3; else if (run > 5) pen++; }
                else run = 1;
            }
        }
        for (int y = 0; y < n - 1; y++)
            for (int x = 0; x < n - 1; x++)
                if (mod[y, x] == mod[y, x + 1] && mod[y, x] == mod[y + 1, x] && mod[y, x] == mod[y + 1, x + 1]) pen += 3;
        return pen;
    }

    private static byte GfMul(byte a, byte b)
    {
        int r = 0;
        for (int i = 7; i >= 0; i--)
        {
            r = (r << 1) ^ ((r >> 7) * 0x11D);
            if (((b >> i) & 1) != 0) r ^= a;
        }
        return (byte)r;
    }

    private static byte[] RsGenerator(int degree)
    {
        byte[] g = new byte[degree];
        g[degree - 1] = 1;
        byte root = 1;
        for (int i = 0; i < degree; i++)
        {
            for (int j = 0; j < degree; j++)
            {
                g[j] = GfMul(g[j], root);
                if (j + 1 < degree) g[j] ^= g[j + 1];
            }
            root = GfMul(root, 0x02);
        }
        return g;
    }

    private static byte[] RsRemainder(byte[] data, byte[] gen)
    {
        byte[] res = new byte[gen.Length];
        foreach (byte b in data)
        {
            byte factor = (byte)(b ^ res[0]);
            Array.Copy(res, 1, res, 0, res.Length - 1);
            res[^1] = 0;
            for (int i = 0; i < gen.Length; i++)
                res[i] ^= GfMul(gen[i], factor);
        }
        return res;
    }
}
