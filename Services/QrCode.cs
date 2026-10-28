using System.Text;

namespace WgSwitch.Services;

/// <summary>
/// QR-код без сторонних библиотек: только байтовый режим, уровень коррекции M, версии 1–40.
/// Порт алгоритма из «QR Code generator library» Project Nayuki (MIT License,
/// Copyright © Project Nayuki, https://www.nayuki.io/page/qr-code-generator-library).
/// </summary>
public sealed class QrCode
{
    public int Size { get; }
    private readonly bool[,] _modules;     // [y, x]: true — тёмный
    private readonly bool[,] _function;    // служебные модули, маска их не трогает

    public bool this[int x, int y] => _modules[y, x];

    // уровень M; индекс — версия
    private static readonly int[] EccPerBlock =
        [-1, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26, 30, 22, 22, 24, 24, 28, 28, 26, 26, 26, 26, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28];
    private static readonly int[] EccBlocks =
        [-1, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5, 5, 8, 9, 9, 10, 10, 11, 13, 14, 16, 17, 17, 18, 20, 21, 23, 25, 26, 28, 29, 31, 33, 35, 37, 38, 40, 43, 45, 47, 49];
    private const int FormatBitsM = 0;

    public static QrCode Encode(string text) => Encode(Encoding.UTF8.GetBytes(text));

    public static QrCode Encode(byte[] data)
    {
        int version = 1;
        for (; ; version++)
        {
            if (version > 40) throw new ArgumentException("Слишком длинный текст для QR-кода");
            int countBits = version <= 9 ? 8 : 16;
            if (4 + countBits + data.Length * 8 <= DataCodewords(version) * 8) break;
        }

        var bits = new List<bool>();
        void Append(int value, int length)
        {
            for (int i = length - 1; i >= 0; i--) bits.Add(((value >> i) & 1) != 0);
        }
        Append(0b0100, 4);                                 // байтовый режим
        Append(data.Length, version <= 9 ? 8 : 16);
        foreach (var b in data) Append(b, 8);

        int capacity = DataCodewords(version) * 8;
        Append(0, Math.Min(4, capacity - bits.Count));     // терминатор
        Append(0, (8 - bits.Count % 8) % 8);
        for (int pad = 0xEC; bits.Count < capacity; pad ^= 0xEC ^ 0x11) Append(pad, 8);

        var codewords = new byte[bits.Count / 8];
        for (int i = 0; i < bits.Count; i++)
            if (bits[i]) codewords[i >> 3] |= (byte)(1 << (7 - (i & 7)));

        return new QrCode(version, codewords);
    }

    private QrCode(int version, byte[] dataCodewords)
    {
        Size = version * 4 + 17;
        _modules = new bool[Size, Size];
        _function = new bool[Size, Size];

        DrawFunctionPatterns(version);
        DrawCodewords(AddEccAndInterleave(version, dataCodewords));

        // маска с наименьшим штрафом — так код увереннее читается камерой
        int best = 0, bestPenalty = int.MaxValue;
        for (int mask = 0; mask < 8; mask++)
        {
            ApplyMask(mask);
            DrawFormatBits(mask);
            int p = Penalty();
            if (p < bestPenalty) { best = mask; bestPenalty = p; }
            ApplyMask(mask); // повторное применение снимает маску
        }
        ApplyMask(best);
        DrawFormatBits(best);
    }

    // ---------- служебные узоры ----------

    private void DrawFunctionPatterns(int version)
    {
        for (int i = 0; i < Size; i++)
        {
            SetFunction(6, i, i % 2 == 0);
            SetFunction(i, 6, i % 2 == 0);
        }
        DrawFinder(3, 3);
        DrawFinder(Size - 4, 3);
        DrawFinder(3, Size - 4);

        var align = AlignmentPositions(version);
        int n = align.Length;
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                if (!(i == 0 && j == 0 || i == 0 && j == n - 1 || i == n - 1 && j == 0))
                    DrawAlignment(align[i], align[j]);

        DrawFormatBits(0);
        DrawVersion(version);
    }

    private void DrawFinder(int x, int y)
    {
        for (int dy = -4; dy <= 4; dy++)
            for (int dx = -4; dx <= 4; dx++)
            {
                int dist = Math.Max(Math.Abs(dx), Math.Abs(dy));
                int xx = x + dx, yy = y + dy;
                if (xx >= 0 && xx < Size && yy >= 0 && yy < Size) SetFunction(xx, yy, dist != 2 && dist != 4);
            }
    }

    private void DrawAlignment(int x, int y)
    {
        for (int dy = -2; dy <= 2; dy++)
            for (int dx = -2; dx <= 2; dx++)
                SetFunction(x + dx, y + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
    }

    private void DrawFormatBits(int mask)
    {
        int data = FormatBitsM << 3 | mask;
        int rem = data;
        for (int i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >> 9) * 0x537);
        int bits = (data << 10 | rem) ^ 0x5412;

        for (int i = 0; i <= 5; i++) SetFunction(8, i, Bit(bits, i));
        SetFunction(8, 7, Bit(bits, 6));
        SetFunction(8, 8, Bit(bits, 7));
        SetFunction(7, 8, Bit(bits, 8));
        for (int i = 9; i < 15; i++) SetFunction(14 - i, 8, Bit(bits, i));

        for (int i = 0; i < 8; i++) SetFunction(Size - 1 - i, 8, Bit(bits, i));
        for (int i = 8; i < 15; i++) SetFunction(8, Size - 15 + i, Bit(bits, i));
        SetFunction(8, Size - 8, true);
    }

    private void DrawVersion(int version)
    {
        if (version < 7) return;
        int rem = version;
        for (int i = 0; i < 12; i++) rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
        long bits = (long)version << 12 | (long)rem;
        for (int i = 0; i < 18; i++)
        {
            bool bit = ((bits >> i) & 1) != 0;
            int a = Size - 11 + i % 3, b = i / 3;
            SetFunction(a, b, bit);
            SetFunction(b, a, bit);
        }
    }

    private static int[] AlignmentPositions(int version)
    {
        if (version == 1) return [];
        int count = version / 7 + 2;
        int step = (version * 8 + count * 3 + 5) / (count * 4 - 4) * 2;
        var result = new int[count];
        result[0] = 6;
        for (int i = count - 1, pos = version * 4 + 17 - 7; i >= 1; i--, pos -= step) result[i] = pos;
        return result;
    }

    private void SetFunction(int x, int y, bool dark)
    {
        _modules[y, x] = dark;
        _function[y, x] = true;
    }

    // ---------- данные и коррекция ошибок ----------

    private static int RawDataModules(int version)
    {
        int result = (16 * version + 128) * version + 64;
        if (version >= 2)
        {
            int align = version / 7 + 2;
            result -= (25 * align - 10) * align - 55;
            if (version >= 7) result -= 36;
        }
        return result;
    }

    private static int DataCodewords(int version) => RawDataModules(version) / 8 - EccPerBlock[version] * EccBlocks[version];

    private static byte[] AddEccAndInterleave(int version, byte[] data)
    {
        int numBlocks = EccBlocks[version], eccLen = EccPerBlock[version];
        int raw = RawDataModules(version) / 8;
        int numShort = numBlocks - raw % numBlocks;
        int shortLen = raw / numBlocks;

        var divisor = ReedSolomonDivisor(eccLen);
        var blocks = new byte[numBlocks][];
        for (int i = 0, k = 0; i < numBlocks; i++)
        {
            int datLen = shortLen - eccLen + (i < numShort ? 0 : 1);
            var dat = data.AsSpan(k, datLen).ToArray();
            k += datLen;
            var block = new byte[shortLen + 1];
            dat.CopyTo(block, 0);
            ReedSolomonRemainder(dat, divisor).CopyTo(block, shortLen + 1 - eccLen);
            blocks[i] = block;
        }

        var result = new List<byte>(raw);
        for (int i = 0; i < blocks[0].Length; i++)
            for (int j = 0; j < numBlocks; j++)
                if (i != shortLen - eccLen || j >= numShort) result.Add(blocks[j][i]); // в коротких блоках здесь пусто
        return [.. result];
    }

    private static byte[] ReedSolomonDivisor(int degree)
    {
        var result = new byte[degree];
        result[degree - 1] = 1;
        int root = 1;
        for (int i = 0; i < degree; i++)
        {
            for (int j = 0; j < degree; j++)
            {
                result[j] = (byte)Multiply(result[j], root);
                if (j + 1 < degree) result[j] ^= result[j + 1];
            }
            root = Multiply(root, 0x02);
        }
        return result;
    }

    private static byte[] ReedSolomonRemainder(byte[] data, byte[] divisor)
    {
        var result = new byte[divisor.Length];
        foreach (var b in data)
        {
            int factor = b ^ result[0];
            Array.Copy(result, 1, result, 0, result.Length - 1);
            result[^1] = 0;
            for (int i = 0; i < result.Length; i++) result[i] ^= (byte)Multiply(divisor[i], factor);
        }
        return result;
    }

    private static int Multiply(int x, int y)
    {
        int z = 0;
        for (int i = 7; i >= 0; i--)
        {
            z = (z << 1) ^ ((z >> 7) * 0x11D);
            z ^= ((y >> i) & 1) * x;
        }
        return z;
    }

    private void DrawCodewords(byte[] data)
    {
        int i = 0;
        for (int right = Size - 1; right >= 1; right -= 2)
        {
            if (right == 6) right = 5;
            for (int vert = 0; vert < Size; vert++)
                for (int j = 0; j < 2; j++)
                {
                    int x = right - j;
                    bool upward = ((right + 1) & 2) == 0;
                    int y = upward ? Size - 1 - vert : vert;
                    if (!_function[y, x] && i < data.Length * 8)
                    {
                        _modules[y, x] = Bit(data[i >> 3], 7 - (i & 7));
                        i++;
                    }
                }
        }
    }

    private void ApplyMask(int mask)
    {
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                if (_function[y, x]) continue;
                bool invert = mask switch
                {
                    0 => (x + y) % 2 == 0,
                    1 => y % 2 == 0,
                    2 => x % 3 == 0,
                    3 => (x + y) % 3 == 0,
                    4 => (x / 3 + y / 2) % 2 == 0,
                    5 => x * y % 2 + x * y % 3 == 0,
                    6 => (x * y % 2 + x * y % 3) % 2 == 0,
                    _ => ((x + y) % 2 + x * y % 3) % 2 == 0,
                };
                if (invert) _modules[y, x] = !_modules[y, x];
            }
    }

    /// <summary>Штраф по правилам стандарта: длинные полосы, блоки 2×2, похожие на искатель узоры, перекос тёмного.</summary>
    private int Penalty()
    {
        int result = 0;
        for (int pass = 0; pass < 2; pass++)
            for (int a = 0; a < Size; a++)
            {
                int run = 0;
                bool color = false;
                for (int b = 0; b < Size; b++)
                {
                    bool m = pass == 0 ? _modules[a, b] : _modules[b, a];
                    if (b > 0 && m == color) run++;
                    else
                    {
                        if (run >= 5) result += 3 + run - 5;
                        color = m;
                        run = 1;
                    }
                }
                if (run >= 5) result += 3 + run - 5;

                for (int b = 0; b + 10 < Size; b++)
                {
                    bool M(int k) => pass == 0 ? _modules[a, b + k] : _modules[b + k, a];
                    bool core = M(0) && !M(1) && M(2) && M(3) && M(4) && !M(5) && M(6);
                    if (core && !M(7) && !M(8) && !M(9) && !M(10)) result += 40;
                    bool core2 = M(4) && !M(5) && M(6) && M(7) && M(8) && !M(9) && M(10);
                    if (core2 && !M(0) && !M(1) && !M(2) && !M(3)) result += 40;
                }
            }

        for (int y = 0; y < Size - 1; y++)
            for (int x = 0; x < Size - 1; x++)
            {
                bool c = _modules[y, x];
                if (c == _modules[y, x + 1] && c == _modules[y + 1, x] && c == _modules[y + 1, x + 1]) result += 3;
            }

        int dark = 0;
        foreach (bool m in _modules) if (m) dark++;
        int total = Size * Size;
        int k2 = (Math.Abs(dark * 20 - total * 10) + total - 1) / total - 1;
        return result + Math.Max(0, k2) * 10;
    }

    private static bool Bit(int x, int i) => ((x >> i) & 1) != 0;
}
