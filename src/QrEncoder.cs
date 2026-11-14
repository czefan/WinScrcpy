using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;

/// <summary>
/// Minimal self-contained QR Code matrix generator (Versions 1-6, ECC Level M, Byte mode).
/// Used for Android wireless debugging QR pairing: WIFI:T:ADB;S:&lt;name&gt;;P:&lt;pass&gt;;;
/// </summary>
internal static class QrEncoder
{
    sealed class VersionSpec
    {
        public int Version;
        public int TotalDataCodewords;
        public int EcPerBlock;
        public int BlockCount;
    }

    static readonly VersionSpec[] Specs = new VersionSpec[]
    {
        new VersionSpec { Version = 1, TotalDataCodewords = 16,  EcPerBlock = 10, BlockCount = 1 },
        new VersionSpec { Version = 2, TotalDataCodewords = 28,  EcPerBlock = 16, BlockCount = 1 },
        new VersionSpec { Version = 3, TotalDataCodewords = 44,  EcPerBlock = 26, BlockCount = 1 },
        new VersionSpec { Version = 4, TotalDataCodewords = 64,  EcPerBlock = 18, BlockCount = 2 },
        new VersionSpec { Version = 5, TotalDataCodewords = 86,  EcPerBlock = 24, BlockCount = 2 },
        new VersionSpec { Version = 6, TotalDataCodewords = 108, EcPerBlock = 16, BlockCount = 4 }
    };

    public static bool[,] Encode(string text)
    {
        if (text == null) text = "";
        byte[] utf8 = Encoding.UTF8.GetBytes(text);

        VersionSpec spec = null;
        for (int i = 0; i < Specs.Length; i++)
        {
            // Byte mode overhead in V1-V6 is 4 (mode) + 8 (count) = 12 bits, plus up to 4 terminator bits.
            int usableBytes = Specs[i].TotalDataCodewords - 2;
            if (utf8.Length <= usableBytes)
            {
                spec = Specs[i];
                break;
            }
        }
        if (spec == null)
        {
            throw new ArgumentException("QR payload too long for Versions 1-6.");
        }

        byte[] dataCodewords = BuildDataCodewords(utf8, spec.TotalDataCodewords);
        byte[] finalStream = BuildErrorCorrectionAndInterleave(dataCodewords, spec);

        int size = 17 + spec.Version * 4;
        bool[,] modules = new bool[size, size];
        bool[,] isFunction = new bool[size, size];

        DrawFunctionPatterns(modules, isFunction, spec.Version, size);
        PlaceCodewords(modules, isFunction, finalStream, size);

        int bestMask = 0;
        int bestPenalty = int.MaxValue;
        for (int mask = 0; mask < 8; mask++)
        {
            bool[,] candidate = (bool[,])modules.Clone();
            ApplyMask(candidate, isFunction, mask, size);
            DrawFormatBits(candidate, mask, size);
            int penalty = EvaluatePenalty(candidate, size);
            if (penalty < bestPenalty)
            {
                bestPenalty = penalty;
                bestMask = mask;
            }
        }

        ApplyMask(modules, isFunction, bestMask, size);
        DrawFormatBits(modules, bestMask, size);
        return modules;
    }

    public static Bitmap RenderBitmap(bool[,] matrix, int pixelsPerModule, int quietModules)
    {
        int n = matrix.GetLength(0);
        int totalModules = n + quietModules * 2;
        int pxSize = totalModules * pixelsPerModule;
        Bitmap bmp = new Bitmap(pxSize, pxSize);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            for (int r = 0; r < n; r++)
            {
                for (int c = 0; c < n; c++)
                {
                    if (matrix[r, c])
                    {
                        g.FillRectangle(
                            Brushes.Black,
                            (c + quietModules) * pixelsPerModule,
                            (r + quietModules) * pixelsPerModule,
                            pixelsPerModule,
                            pixelsPerModule);
                    }
                }
            }
        }
        return bmp;
    }

    static byte[] BuildDataCodewords(byte[] payload, int totalDataCodewords)
    {
        List<int> bits = new List<int>(totalDataCodewords * 8);
        AppendBits(bits, 0x4, 4); // Byte mode
        AppendBits(bits, payload.Length, 8);
        for (int i = 0; i < payload.Length; i++)
        {
            AppendBits(bits, payload[i], 8);
        }

        int capacityBits = totalDataCodewords * 8;
        int terminator = Math.Min(4, capacityBits - bits.Count);
        AppendBits(bits, 0, terminator);

        while (bits.Count % 8 != 0)
        {
            bits.Add(0);
        }

        byte[] codewords = new byte[totalDataCodewords];
        int cwCount = bits.Count / 8;
        for (int i = 0; i < cwCount; i++)
        {
            int b = 0;
            for (int j = 0; j < 8; j++)
            {
                b = (b << 1) | bits[i * 8 + j];
            }
            codewords[i] = (byte)b;
        }

        bool alt = true;
        for (int i = cwCount; i < totalDataCodewords; i++)
        {
            codewords[i] = (byte)(alt ? 0xEC : 0x11);
            alt = !alt;
        }
        return codewords;
    }

    static void AppendBits(List<int> bits, int value, int count)
    {
        for (int i = count - 1; i >= 0; i--)
        {
            bits.Add((value >> i) & 1);
        }
    }

    static byte[] BuildErrorCorrectionAndInterleave(byte[] data, VersionSpec spec)
    {
        int blocks = spec.BlockCount;
        int dataPerBlock = spec.TotalDataCodewords / blocks;
        int ecCount = spec.EcPerBlock;
        byte[] gen = ReedSolomonGenerator(ecCount);

        byte[][] dataBlocks = new byte[blocks][];
        byte[][] ecBlocks = new byte[blocks][];
        for (int b = 0; b < blocks; b++)
        {
            dataBlocks[b] = new byte[dataPerBlock];
            Array.Copy(data, b * dataPerBlock, dataBlocks[b], 0, dataPerBlock);
            ecBlocks[b] = ReedSolomonRemainder(dataBlocks[b], gen);
        }

        byte[] result = new byte[spec.TotalDataCodewords + blocks * ecCount];
        int idx = 0;
        for (int i = 0; i < dataPerBlock; i++)
        {
            for (int b = 0; b < blocks; b++)
            {
                result[idx++] = dataBlocks[b][i];
            }
        }
        for (int i = 0; i < ecCount; i++)
        {
            for (int b = 0; b < blocks; b++)
            {
                result[idx++] = ecBlocks[b][i];
            }
        }
        return result;
    }

    static byte[] ReedSolomonGenerator(int degree)
    {
        byte[] gen = new byte[degree];
        gen[degree - 1] = 1;
        int root = 1;
        for (int i = 0; i < degree; i++)
        {
            for (int j = 0; j < degree; j++)
            {
                gen[j] = (byte)GfMultiply(gen[j], root);
                if (j + 1 < degree)
                {
                    gen[j] ^= gen[j + 1];
                }
            }
            root = GfMultiply(root, 0x02);
        }
        return gen;
    }

    static byte[] ReedSolomonRemainder(byte[] data, byte[] gen)
    {
        int degree = gen.Length;
        byte[] rem = new byte[degree];
        for (int i = 0; i < data.Length; i++)
        {
            int factor = data[i] ^ rem[0];
            Array.Copy(rem, 1, rem, 0, degree - 1);
            rem[degree - 1] = 0;
            for (int j = 0; j < degree; j++)
            {
                rem[j] ^= (byte)GfMultiply(gen[j], factor);
            }
        }
        return rem;
    }

    static int GfMultiply(int x, int y)
    {
        int z = 0;
        for (int i = 7; i >= 0; i--)
        {
            z = (z << 1) ^ ((z >> 7) * 0x11D);
            z ^= ((y >> i) & 1) * x;
        }
        return z;
    }

    static void SetFunctionModule(bool[,] modules, bool[,] isFunction, int r, int c, bool dark)
    {
        modules[r, c] = dark;
        isFunction[r, c] = true;
    }

    static void DrawFunctionPatterns(bool[,] modules, bool[,] isFunction, int version, int size)
    {
        // Timing patterns
        for (int i = 0; i < size; i++)
        {
            SetFunctionModule(modules, isFunction, 6, i, i % 2 == 0);
            SetFunctionModule(modules, isFunction, i, 6, i % 2 == 0);
        }

        // Finder patterns + 1-module white separators
        DrawFinder(modules, isFunction, 0, 0, size);
        DrawFinder(modules, isFunction, 0, size - 7, size);
        DrawFinder(modules, isFunction, size - 7, 0, size);

        // Single bottom-right alignment pattern for Versions 2-6
        if (version >= 2)
        {
            int center = size - 7;
            for (int dr = -2; dr <= 2; dr++)
            {
                for (int dc = -2; dc <= 2; dc++)
                {
                    int dist = Math.Max(Math.Abs(dr), Math.Abs(dc));
                    SetFunctionModule(modules, isFunction, center + dr, center + dc, dist != 1);
                }
            }
        }

        // Reserve format info strips + mandatory dark module
        for (int i = 0; i <= 8; i++)
        {
            if (i != 6)
            {
                SetFunctionModule(modules, isFunction, 8, i, false);
                SetFunctionModule(modules, isFunction, i, 8, false);
            }
        }
        for (int i = size - 8; i < size; i++)
        {
            SetFunctionModule(modules, isFunction, 8, i, false);
            SetFunctionModule(modules, isFunction, i, 8, false);
        }
        SetFunctionModule(modules, isFunction, size - 8, 8, true);
    }

    static void DrawFinder(bool[,] modules, bool[,] isFunction, int top, int left, int size)
    {
        for (int dr = -1; dr <= 7; dr++)
        {
            for (int dc = -1; dc <= 7; dc++)
            {
                int r = top + dr;
                int c = left + dc;
                if (r < 0 || r >= size || c < 0 || c >= size) continue;
                int dist = Math.Max(Math.Abs(dr - 3), Math.Abs(dc - 3));
                bool dark = (dr >= 0 && dr <= 6 && dc >= 0 && dc <= 6) && (dist != 2);
                SetFunctionModule(modules, isFunction, r, c, dark);
            }
        }
    }

    static void PlaceCodewords(bool[,] modules, bool[,] isFunction, byte[] data, int size)
    {
        int totalBits = data.Length * 8;
        int bitIdx = 0;
        bool upward = true;

        for (int right = size - 1; right >= 1; right -= 2)
        {
            if (right == 6) right = 5;
            for (int vert = 0; vert < size; vert++)
            {
                int r = upward ? (size - 1 - vert) : vert;
                for (int j = 0; j < 2; j++)
                {
                    int c = right - j;
                    if (!isFunction[r, c])
                    {
                        bool bit = false;
                        if (bitIdx < totalBits)
                        {
                            bit = ((data[bitIdx >> 3] >> (7 - (bitIdx & 7))) & 1) != 0;
                            bitIdx++;
                        }
                        modules[r, c] = bit;
                    }
                }
            }
            upward = !upward;
        }
    }

    static void ApplyMask(bool[,] modules, bool[,] isFunction, int mask, int size)
    {
        for (int r = 0; r < size; r++)
        {
            for (int c = 0; c < size; c++)
            {
                if (isFunction[r, c]) continue;
                bool invert;
                switch (mask)
                {
                    case 0: invert = (r + c) % 2 == 0; break;
                    case 1: invert = r % 2 == 0; break;
                    case 2: invert = c % 3 == 0; break;
                    case 3: invert = (r + c) % 3 == 0; break;
                    case 4: invert = ((r / 2) + (c / 3)) % 2 == 0; break;
                    case 5: invert = ((r * c) % 2) + ((r * c) % 3) == 0; break;
                    case 6: invert = (((r * c) % 2) + ((r * c) % 3)) % 2 == 0; break;
                    default: invert = (((r + c) % 2) + ((r * c) % 3)) % 2 == 0; break;
                }
                if (invert) modules[r, c] = !modules[r, c];
            }
        }
    }

    static void DrawFormatBits(bool[,] modules, int mask, int size)
    {
        // ECC Level M = 00
        int data = (0 << 3) | mask;
        int rem = data;
        for (int i = 0; i < 10; i++)
        {
            rem = (rem << 1) ^ (((rem >> 9) & 1) * 0x537);
        }
        int bits = ((data << 10) | rem) ^ 0x5412;

        // Top-left copy
        for (int i = 0; i <= 5; i++) modules[8, i] = ((bits >> i) & 1) != 0;
        modules[8, 7] = ((bits >> 6) & 1) != 0;
        modules[8, 8] = ((bits >> 7) & 1) != 0;
        modules[7, 8] = ((bits >> 8) & 1) != 0;
        for (int i = 9; i < 15; i++) modules[14 - i, 8] = ((bits >> i) & 1) != 0;

        // Bottom-left + top-right copy
        for (int i = 0; i <= 6; i++) modules[size - 1 - i, 8] = ((bits >> i) & 1) != 0;
        for (int i = 7; i < 15; i++) modules[8, size - 15 + i] = ((bits >> i) & 1) != 0;
        modules[size - 8, 8] = true;
    }

    static int EvaluatePenalty(bool[,] m, int size)
    {
        int penalty = 0;
        int darkCount = 0;

        for (int r = 0; r < size; r++)
        {
            int runRow = 1;
            int runCol = 1;
            if (m[r, 0]) darkCount++;
            for (int i = 1; i < size; i++)
            {
                if (m[r, i]) darkCount++;
                if (m[r, i] == m[r, i - 1])
                {
                    runRow++;
                    if (runRow == 5) penalty += 3;
                    else if (runRow > 5) penalty += 1;
                }
                else runRow = 1;

                if (m[i, r] == m[i - 1, r])
                {
                    runCol++;
                    if (runCol == 5) penalty += 3;
                    else if (runCol > 5) penalty += 1;
                }
                else runCol = 1;
            }
        }

        for (int r = 0; r < size - 1; r++)
        {
            for (int c = 0; c < size - 1; c++)
            {
                bool color = m[r, c];
                if (m[r, c + 1] == color && m[r + 1, c] == color && m[r + 1, c + 1] == color)
                {
                    penalty += 3;
                }
            }
        }

        for (int r = 0; r < size; r++)
        {
            for (int c = 0; c <= size - 11; c++)
            {
                if (MatchesFinderLikeRow(m, r, c)) penalty += 40;
                if (MatchesFinderLikeCol(m, c, r)) penalty += 40;
            }
        }

        int total = size * size;
        int pct = (darkCount * 100) / total;
        int dev = Math.Abs(pct - 50) / 5;
        penalty += dev * 10;
        return penalty;
    }

    static bool MatchesFinderLikeRow(bool[,] m, int r, int c)
    {
        // Pattern: 1 0 1 1 1 0 1 with 4 zeros on left OR 4 zeros on right
        bool left =
            !m[r, c] && !m[r, c + 1] && !m[r, c + 2] && !m[r, c + 3] &&
            m[r, c + 4] && !m[r, c + 5] && m[r, c + 6] && m[r, c + 7] && m[r, c + 8] && !m[r, c + 9] && m[r, c + 10];
        bool right =
            m[r, c] && !m[r, c + 1] && m[r, c + 2] && m[r, c + 3] && m[r, c + 4] && !m[r, c + 5] && m[r, c + 6] &&
            !m[r, c + 7] && !m[r, c + 8] && !m[r, c + 9] && !m[r, c + 10];
        return left || right;
    }

    static bool MatchesFinderLikeCol(bool[,] m, int r, int c)
    {
        bool top =
            !m[r, c] && !m[r + 1, c] && !m[r + 2, c] && !m[r + 3, c] &&
            m[r + 4, c] && !m[r + 5, c] && m[r + 6, c] && m[r + 7, c] && m[r + 8, c] && !m[r + 9, c] && m[r + 10, c];
        bool bottom =
            m[r, c] && !m[r + 1, c] && m[r + 2, c] && m[r + 3, c] && m[r + 4, c] && !m[r + 5, c] && m[r + 6, c] &&
            !m[r + 7, c] && !m[r + 8, c] && !m[r + 9, c] && !m[r + 10, c];
        return top || bottom;
    }
}
