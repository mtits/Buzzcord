using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;

namespace Buzzcord
{
    // Minimal QR code encoder (byte mode, error correction level M), enough for login links.
    public class QrCode
    {
        public readonly int Version, Size;
        readonly bool[,] modules;    // [y, x], true = dark
        readonly bool[,] isFunction;

        static readonly int[] EccPerBlockM = { -1, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26, 30, 22, 22, 24, 24, 28, 28, 26, 26, 26, 26, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28 };
        static readonly int[] BlocksM = { -1, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5, 5, 8, 9, 9, 10, 10, 11, 13, 14, 16, 17, 17, 18, 20, 21, 23, 25, 26, 28, 29, 31, 33, 35, 37, 38, 40, 43, 45, 47, 49 };
        const int FormatBitsM = 0;

        public bool this[int x, int y] { get { return x >= 0 && y >= 0 && x < Size && y < Size && modules[y, x]; } }

        public static QrCode Encode(string text)
        {
            byte[] data = Encoding.UTF8.GetBytes(text);
            for (int ver = 1; ver <= 40; ver++)
            {
                int capacityBits = DataCodewords(ver) * 8;
                int used = 4 + (ver < 10 ? 8 : 16) + data.Length * 8;
                if (used <= capacityBits) return new QrCode(ver, data);
            }
            throw new ArgumentException("Text too long for a QR code");
        }

        QrCode(int ver, byte[] data)
        {
            Version = ver;
            Size = ver * 4 + 17;
            modules = new bool[Size, Size];
            isFunction = new bool[Size, Size];

            // Bit stream: byte mode, length, data, terminator, padding.
            var bits = new List<bool>();
            Append(bits, 4, 4);
            Append(bits, data.Length, ver < 10 ? 8 : 16);
            foreach (byte b in data) Append(bits, b, 8);
            int capacity = DataCodewords(ver) * 8;
            Append(bits, 0, Math.Min(4, capacity - bits.Count));
            Append(bits, 0, (8 - bits.Count % 8) % 8);
            for (int pad = 0xEC; bits.Count < capacity; pad ^= 0xEC ^ 0x11) Append(bits, pad, 8);
            var codewords = new byte[bits.Count / 8];
            for (int i = 0; i < bits.Count; i++) if (bits[i]) codewords[i >> 3] |= (byte)(1 << (7 - (i & 7)));

            DrawFunctionPatterns();
            DrawCodewords(AddEccAndInterleave(codewords));

            int best = 0, bestPenalty = int.MaxValue;
            for (int mask = 0; mask < 8; mask++)
            {
                ApplyMask(mask);
                DrawFormatBits(mask);
                int p = Penalty();
                if (p < bestPenalty) { best = mask; bestPenalty = p; }
                ApplyMask(mask); // undo (XOR)
            }
            ApplyMask(best);
            DrawFormatBits(best);
        }

        static void Append(List<bool> bits, int value, int len)
        {
            for (int i = len - 1; i >= 0; i--) bits.Add(((value >> i) & 1) != 0);
        }

        static int RawDataModules(int ver)
        {
            int result = (16 * ver + 128) * ver + 64;
            if (ver >= 2)
            {
                int numAlign = ver / 7 + 2;
                result -= (25 * numAlign - 10) * numAlign - 55;
                if (ver >= 7) result -= 36;
            }
            return result;
        }

        static int DataCodewords(int ver)
        {
            return RawDataModules(ver) / 8 - EccPerBlockM[ver] * BlocksM[ver];
        }

        byte[] AddEccAndInterleave(byte[] data)
        {
            int numBlocks = BlocksM[Version], eccLen = EccPerBlockM[Version];
            int raw = RawDataModules(Version) / 8;
            int numShort = numBlocks - raw % numBlocks;
            int shortLen = raw / numBlocks;
            var divisor = RsDivisor(eccLen);
            var blocks = new List<byte[]>();
            for (int i = 0, k = 0; i < numBlocks; i++)
            {
                int datLen = shortLen - eccLen + (i < numShort ? 0 : 1);
                var dat = new byte[datLen];
                Array.Copy(data, k, dat, 0, datLen);
                k += datLen;
                var ecc = RsRemainder(dat, divisor);
                // Short blocks get a placeholder byte so all blocks have the same length.
                var block = new byte[shortLen + 1];
                Array.Copy(dat, block, datLen);
                Array.Copy(ecc, 0, block, block.Length - eccLen, eccLen);
                blocks.Add(block);
            }
            var result = new byte[raw];
            int n = 0;
            for (int i = 0; i < shortLen + 1; i++)
                for (int j = 0; j < blocks.Count; j++)
                    if (i != shortLen - eccLen || j >= numShort)
                        result[n++] = blocks[j][i];
            return result;
        }

        static byte[] RsDivisor(int degree)
        {
            var result = new byte[degree];
            result[degree - 1] = 1;
            int root = 1;
            for (int i = 0; i < degree; i++)
            {
                for (int j = 0; j < degree; j++)
                {
                    result[j] = (byte)GfMul(result[j], root);
                    if (j + 1 < degree) result[j] ^= result[j + 1];
                }
                root = GfMul(root, 2);
            }
            return result;
        }

        static byte[] RsRemainder(byte[] data, byte[] divisor)
        {
            var result = new byte[divisor.Length];
            foreach (byte b in data)
            {
                int factor = b ^ result[0];
                Array.Copy(result, 1, result, 0, result.Length - 1);
                result[result.Length - 1] = 0;
                for (int i = 0; i < result.Length; i++) result[i] ^= (byte)GfMul(divisor[i], factor);
            }
            return result;
        }

        static int GfMul(int x, int y)
        {
            int z = 0;
            for (int i = 7; i >= 0; i--)
            {
                z = (z << 1) ^ ((z >> 7) * 0x11D);
                z ^= ((y >> i) & 1) * x;
            }
            return z & 0xFF;
        }

        void SetFunction(int x, int y, bool dark)
        {
            modules[y, x] = dark;
            isFunction[y, x] = true;
        }

        void DrawFunctionPatterns()
        {
            for (int i = 0; i < Size; i++)
            {
                SetFunction(6, i, i % 2 == 0);
                SetFunction(i, 6, i % 2 == 0);
            }
            DrawFinder(3, 3);
            DrawFinder(Size - 4, 3);
            DrawFinder(3, Size - 4);

            var align = AlignmentPositions();
            int na = align.Length;
            for (int i = 0; i < na; i++)
                for (int j = 0; j < na; j++)
                    if (!(i == 0 && j == 0 || i == 0 && j == na - 1 || i == na - 1 && j == 0))
                        for (int dy = -2; dy <= 2; dy++)
                            for (int dx = -2; dx <= 2; dx++)
                                SetFunction(align[i] + dx, align[j] + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);

            DrawFormatBits(0); // reserves the area; real bits drawn after masking
            if (Version >= 7)
            {
                int rem = Version;
                for (int i = 0; i < 12; i++) rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
                int bits = Version << 12 | rem;
                for (int i = 0; i < 18; i++)
                {
                    bool bit = ((bits >> i) & 1) != 0;
                    int a = Size - 11 + i % 3, b = i / 3;
                    SetFunction(a, b, bit);
                    SetFunction(b, a, bit);
                }
            }
        }

        void DrawFinder(int x, int y)
        {
            for (int dy = -4; dy <= 4; dy++)
                for (int dx = -4; dx <= 4; dx++)
                {
                    int dist = Math.Max(Math.Abs(dx), Math.Abs(dy));
                    int xx = x + dx, yy = y + dy;
                    if (xx >= 0 && xx < Size && yy >= 0 && yy < Size)
                        SetFunction(xx, yy, dist != 2 && dist != 4);
                }
        }

        int[] AlignmentPositions()
        {
            if (Version == 1) return new int[0];
            int num = Version / 7 + 2;
            int step = Version == 32 ? 26 : (Version * 4 + num * 2 + 1) / (num * 2 - 2) * 2;
            var result = new int[num];
            result[0] = 6;
            for (int i = num - 1, pos = Size - 7; i >= 1; i--, pos -= step) result[i] = pos;
            return result;
        }

        void DrawFormatBits(int mask)
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

        static bool Bit(int x, int i) { return ((x >> i) & 1) != 0; }

        void DrawCodewords(byte[] data)
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
                        if (!isFunction[y, x] && i < data.Length * 8)
                        {
                            modules[y, x] = Bit(data[i >> 3], 7 - (i & 7));
                            i++;
                        }
                    }
            }
        }

        void ApplyMask(int mask)
        {
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                {
                    if (isFunction[y, x]) continue;
                    bool invert;
                    switch (mask)
                    {
                        case 0: invert = (x + y) % 2 == 0; break;
                        case 1: invert = y % 2 == 0; break;
                        case 2: invert = x % 3 == 0; break;
                        case 3: invert = (x + y) % 3 == 0; break;
                        case 4: invert = (x / 3 + y / 2) % 2 == 0; break;
                        case 5: invert = x * y % 2 + x * y % 3 == 0; break;
                        case 6: invert = (x * y % 2 + x * y % 3) % 2 == 0; break;
                        default: invert = ((x + y) % 2 + x * y % 3) % 2 == 0; break;
                    }
                    if (invert) modules[y, x] = !modules[y, x];
                }
        }

        int Penalty()
        {
            int result = 0;
            // Runs of 5+ same-colour modules in rows and columns, plus finder-like patterns.
            for (int pass = 0; pass < 2; pass++)
                for (int a = 0; a < Size; a++)
                {
                    int run = 0;
                    bool prev = false;
                    for (int b = 0; b < Size; b++)
                    {
                        bool c = pass == 0 ? modules[a, b] : modules[b, a];
                        if (b > 0 && c == prev) run++;
                        else
                        {
                            if (run >= 5) result += run - 2;
                            run = 1;
                            prev = c;
                        }
                        if (b + 10 < Size && LooksLikeFinder(pass, a, b)) result += 40;
                    }
                    if (run >= 5) result += run - 2;
                }
            // 2x2 blocks of one colour.
            for (int y = 0; y < Size - 1; y++)
                for (int x = 0; x < Size - 1; x++)
                {
                    bool c = modules[y, x];
                    if (c == modules[y, x + 1] && c == modules[y + 1, x] && c == modules[y + 1, x + 1]) result += 3;
                }
            // Balance of dark and light.
            int dark = 0;
            foreach (bool m in modules) if (m) dark++;
            int total = Size * Size;
            int k = (Math.Abs(dark * 20 - total * 10) + total - 1) / total - 1;
            result += k * 10;
            return result;
        }

        static readonly bool[] FinderA = { true, false, true, true, true, false, true, false, false, false, false };
        static readonly bool[] FinderB = { false, false, false, false, true, false, true, true, true, false, true };

        bool LooksLikeFinder(int pass, int a, int start)
        {
            bool ma = true, mb = true;
            for (int i = 0; i < 11 && (ma || mb); i++)
            {
                bool c = pass == 0 ? modules[a, start + i] : modules[start + i, a];
                if (c != FinderA[i]) ma = false;
                if (c != FinderB[i]) mb = false;
            }
            return ma || mb;
        }

        public Bitmap ToBitmap(int scale, int border)
        {
            int dim = (Size + border * 2) * scale;
            var bmp = new Bitmap(dim, dim);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.White);
                for (int y = 0; y < Size; y++)
                    for (int x = 0; x < Size; x++)
                        if (modules[y, x])
                            g.FillRectangle(Brushes.Black, (x + border) * scale, (y + border) * scale, scale, scale);
            }
            return bmp;
        }
    }
}
