#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program; if not, write to the Free Software
    Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

    Optionally you can also view the license at <http://www.gnu.org/licenses/>.
*/

#endregion License Information (GPL v3)

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Threading.Tasks;

namespace ShareX.HelpersLib;

// FastPngWriter: a PNG writer for opaque 8-bit screenshots that uses several cores.
//
// A PNG's pixel data is one zlib stream: every row gets a filter byte, then everything is deflated. The usual
// writers do that on one core. Here the picture is cut into horizontal bands; each band filters its rows and
// deflates them on its own core, ending on a byte boundary without closing the stream (a sync flush), and the
// bands are then joined into one valid zlib stream (the checksum of each band is combined). The result is an
// ordinary PNG with the same pixels; only the bytes differ from another writer's.
//
// "Adaptive" is the "all filters" choice of libpng: for each row try the five filters and keep the one whose
// output has the smallest sum of absolute values. The five filters are computed 32 bytes at a time.
public enum FastPngFilter { None = 0, Sub = 1, Up = 2, Average = 3, Paeth = 4, Adaptive = 5 }

public static unsafe class FastPngWriter
{
    private const int Bpp = 3;                 // bytes per pixel in the file: RGB
    private const int Slack = 64;              // room behind a row buffer so 32-byte loads may run past its end
    private const int MinBandBytes = 192 * 1024;
    private static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
    private static readonly uint[] CrcTable = MakeCrcTable();

    /// <summary>
    /// Writes one small picture to nowhere, so the writer is compiled before the first capture needs it.
    /// Without it the first save after a start takes about ten times as long as the later ones.
    /// </summary>
    public static void WarmUp()
    {
        const int width = 256, height = 64;
        byte[] pixels = new byte[width * height * 4];
        fixed (byte* start = pixels)
        {
            WriteOpaque(Stream.Null, start, width * 4, width, height, true, FastPngFilter.Adaptive, 2, bands: 2);
        }
    }

    /// <summary>
    /// Writes 4-byte pixels (blue or red first, the fourth byte is ignored) as an RGB PNG.
    /// level is the zlib level 0-9 of .NET's own compressor. bands 0 = one per core, as far as the picture is big
    /// enough. Returns false, writing nothing, when the file would be larger than sizeLimit.
    /// </summary>
    public static bool WriteOpaque(Stream output, byte* pixels, int rowBytes, int width, int height, bool blueFirst,
        FastPngFilter filter, int level, int bands = 0, long sizeLimit = long.MaxValue)
    {
        const ZLibCompressionStrategy strategy = ZLibCompressionStrategy.Default;
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        int stride = checked(width * Bpp);
        long rawSize = (long)(stride + 1) * height;
        int wanted = bands > 0 ? bands : Environment.ProcessorCount;
        int bandCount = (int)Math.Clamp(rawSize / MinBandBytes, 1, Math.Min(wanted, height));
        if (bands > 0) bandCount = Math.Min(bands, height);

        Band[] results = new Band[bandCount];
        if (bandCount == 1)
        {
            results[0] = EncodeBand(pixels, rowBytes, width, 0, height, blueFirst, filter, level, strategy, true);
        }
        else
        {
            Parallel.For(0, bandCount, new ParallelOptions { MaxDegreeOfParallelism = bandCount }, index =>
            {
                int first = (int)((long)height * index / bandCount), end = (int)((long)height * (index + 1) / bandCount);
                results[index] = EncodeBand(pixels, rowBytes, width, first, end, blueFirst, filter, level, strategy, index == bandCount - 1);
            });
        }

        // signature 8, IHDR 25, zlib header 2 and checksum 4, IEND 12, and 12 around each band's IDAT
        long fileSize = 8 + 25 + 2 + 4 + 12;
        foreach (Band band in results) fileSize += 12 + band.Length;
        if (fileSize > sizeLimit)
        {
            foreach (Band band in results) ArrayPool<byte>.Shared.Return(band.Data);
            return false;
        }

        output.Write(Signature);
        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.Slice(4), height);
        header[8] = 8;   // bits per channel
        header[9] = 2;   // colour type: RGB
        WriteChunk(output, "IHDR"u8, header, default);

        uint adler = 1;
        Span<byte> zlibHeader = stackalloc byte[] { 0x78, 0x01 };
        Span<byte> trailer = stackalloc byte[4];
        for (int i = 0; i < bandCount; i++)
        {
            Band band = results[i];
            adler = i == 0 ? band.Adler : AdlerCombine(adler, band.Adler, band.RawLength);
            bool last = i == bandCount - 1;
            if (last) BinaryPrimitives.WriteUInt32BigEndian(trailer, adler);
            WriteChunk(output, "IDAT"u8, i == 0 ? zlibHeader : default, new ReadOnlySpan<byte>(band.Data, 0, band.Length), last ? trailer : default);
            ArrayPool<byte>.Shared.Return(band.Data);
        }

        WriteChunk(output, "IEND"u8, default, default);
        return true;
    }

    private struct Band
    {
        public byte[] Data;      // deflate blocks of this band (rented)
        public int Length;
        public uint Adler;       // checksum of this band's filtered bytes
        public long RawLength;   // how many filtered bytes that is
    }

    // Compiled fully optimised at the first call: a capture is rare, so the code would otherwise run unoptimised.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static Band EncodeBand(byte* pixels, int rowBytes, int width, int firstRow, int endRow, bool blueFirst,
        FastPngFilter filter, int level, ZLibCompressionStrategy strategy, bool last)
    {
        int stride = width * Bpp;
        int line = stride + 1;
        // Rows carry Bpp zero bytes in front, so "the pixel to the left" of the first pixel reads as zero.
        byte[] rowsBuffer = ArrayPool<byte>.Shared.Rent(2 * (Bpp + stride + Slack));
        byte[] workBuffer = ArrayPool<byte>.Shared.Rent(5 * (stride + Slack));
        const int chunkRows = 32;
        byte[] chunk = ArrayPool<byte>.Shared.Rent(chunkRows * line);
        MemoryStream compressed = new(Math.Max(4096, (int)Math.Min(int.MaxValue / 2, (long)line * (endRow - firstRow) / 4)));
        uint adlerA = 1, adlerB = 0;
        long rawLength = 0;
        int length;

        fixed (byte* rows = rowsBuffer, work = workBuffer, chunkStart = chunk)
        {
            int rowSize = Bpp + stride + Slack;
            byte* current = rows + Bpp, previous = rows + rowSize + Bpp;
            new Span<byte>(rows, 2 * rowSize).Clear();
            if (firstRow > 0) ToRgb(pixels + (long)(firstRow - 1) * rowBytes, previous, width, blueFirst);

            DeflateStream deflate = new(compressed, new ZLibCompressionOptions { CompressionLevel = level, CompressionStrategy = strategy }, leaveOpen: true);
            try
            {
                int inChunk = 0;
                for (int y = firstRow; y < endRow; y++)
                {
                    ToRgb(pixels + (long)y * rowBytes, current, width, blueFirst);
                    byte* target = chunkStart + (long)inChunk * line;
                    target[0] = (byte)FilterRow(current, previous, stride, filter, work, target + 1);
                    if (++inChunk == chunkRows || y == endRow - 1)
                    {
                        ReadOnlySpan<byte> filtered = new(chunkStart, inChunk * line);
                        Adler(filtered, ref adlerA, ref adlerB);
                        rawLength += filtered.Length;
                        deflate.Write(filtered);
                        inChunk = 0;
                    }
                    byte* swap = current; current = previous; previous = swap;
                }

                if (last)
                {
                    deflate.Dispose();           // closes the zlib stream: the final block
                    length = (int)compressed.Length;
                }
                else
                {
                    deflate.Flush();             // sync flush: all data out, on a byte boundary, stream still open
                    length = (int)compressed.Length;
                }
            }
            finally
            {
                deflate.Dispose();               // for a band that is not the last, what this adds is not used
            }
        }

        ArrayPool<byte>.Shared.Return(rowsBuffer);
        ArrayPool<byte>.Shared.Return(workBuffer);
        ArrayPool<byte>.Shared.Return(chunk);
        byte[] data = ArrayPool<byte>.Shared.Rent(length);
        compressed.GetBuffer().AsSpan(0, length).CopyTo(data);
        return new Band { Data = data, Length = length, Adler = (adlerB << 16) | adlerA, RawLength = rawLength };
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void ToRgb(byte* source, byte* target, int width, bool blueFirst)
    {
        int x = 0;
        if (Ssse3.IsSupported && width >= 6)
        {
            // 4 pixels in, 12 bytes out; the 16-byte store spills 4 bytes into the next pixels, rewritten right after.
            Vector128<byte> mask = blueFirst
                ? Vector128.Create((byte)2, 1, 0, 6, 5, 4, 10, 9, 8, 14, 13, 12, 0x80, 0x80, 0x80, 0x80)
                : Vector128.Create((byte)0, 1, 2, 4, 5, 6, 8, 9, 10, 12, 13, 14, 0x80, 0x80, 0x80, 0x80);
            for (; x <= width - 6; x += 4)
            {
                Ssse3.Shuffle(Sse2.LoadVector128(source + (x * 4)), mask).Store(target + (x * 3));
            }
        }

        int red = blueFirst ? 2 : 0, blue = 2 - red;
        for (; x < width; x++)
        {
            byte* s = source + (x * 4), t = target + (x * 3);
            t[0] = s[red]; t[1] = s[1]; t[2] = s[blue];
        }
    }

    // Writes the filtered row to target and returns the filter used.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static int FilterRow(byte* current, byte* previous, int stride, FastPngFilter filter, byte* work, byte* target)
    {
        if (filter != FastPngFilter.Adaptive)
        {
            Apply((int)filter, current, previous, stride, target, false);
            return (int)filter;
        }

        int workSize = stride + Slack, best = 0;
        long bestSum = long.MaxValue;
        for (int candidate = 0; candidate < 5; candidate++)
        {
            long sum = Apply(candidate, current, previous, stride, work + (long)candidate * workSize, true);
            if (sum < bestSum) { bestSum = sum; best = candidate; }
        }

        Buffer.MemoryCopy(work + (long)best * workSize, target, stride, stride);
        return best;
    }

    // One PNG filter over a row. With sum = true it returns the sum of the output bytes read as signed, absolute
    // (libpng's measure for choosing a filter). a = left, b = above, c = above left.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static long Apply(int filter, byte* current, byte* previous, int stride, byte* target, bool sum)
    {
        long total = 0;
        int i = 0;
        if (Avx2.IsSupported)
        {
            Vector256<ulong> totals = Vector256<ulong>.Zero;
            for (; i <= stride - 32; i += 32)
            {
                Vector256<byte> x = Avx.LoadVector256(current + i), result;
                switch (filter)
                {
                    case 0:
                        result = x;
                        break;
                    case 1:
                        result = Avx2.Subtract(x, Avx.LoadVector256(current + i - Bpp));
                        break;
                    case 2:
                        result = Avx2.Subtract(x, Avx.LoadVector256(previous + i));
                        break;
                    case 3:
                    {
                        // floor((a + b) / 2) without leaving bytes
                        Vector256<byte> a = Avx.LoadVector256(current + i - Bpp), b = Avx.LoadVector256(previous + i);
                        Vector256<byte> half = Avx2.And(Avx2.ShiftRightLogical(Avx2.Xor(a, b).AsUInt16(), 1).AsByte(), Vector256.Create((byte)0x7F));
                        result = Avx2.Subtract(x, Avx2.Add(Avx2.And(a, b), half));
                        break;
                    }
                    default:
                    {
                        Vector256<byte> a = Avx.LoadVector256(current + i - Bpp), b = Avx.LoadVector256(previous + i), c = Avx.LoadVector256(previous + i - Bpp);
                        // pa = |b - c|, pb = |a - c|, pc = |(b - c) + (a - c)|: the sum when both differences have the
                        // same sign (saturated: it only has to be no smaller than pa and pb), else their difference.
                        Vector256<byte> pa = Avx2.Subtract(Avx2.Max(b, c), Avx2.Min(b, c));
                        Vector256<byte> pb = Avx2.Subtract(Avx2.Max(a, c), Avx2.Min(a, c));
                        Vector256<byte> bNotBelow = Avx2.CompareEqual(Avx2.Max(b, c), b), aNotBelow = Avx2.CompareEqual(Avx2.Max(a, c), a);
                        Vector256<byte> sameSign = Avx2.CompareEqual(bNotBelow, aNotBelow);
                        Vector256<byte> pc = Avx2.BlendVariable(Avx2.Subtract(Avx2.Max(pa, pb), Avx2.Min(pa, pb)), Avx2.AddSaturate(pa, pb), sameSign);
                        Vector256<byte> useA = Avx2.And(Avx2.CompareEqual(Avx2.Min(pa, pb), pa), Avx2.CompareEqual(Avx2.Min(pa, pc), pa));
                        Vector256<byte> useB = Avx2.CompareEqual(Avx2.Min(pb, pc), pb);
                        Vector256<byte> predictor = Avx2.BlendVariable(Avx2.BlendVariable(c, b, useB), a, useA);
                        result = Avx2.Subtract(x, predictor);
                        break;
                    }
                }

                Avx.Store(target + i, result);
                if (sum)
                {
                    // |signed byte| stays a byte (128 for -128); psadbw against zero adds 8 of them at a time.
                    totals = Avx2.Add(totals, Avx2.SumAbsoluteDifferences(Avx2.Abs(result.AsSByte()), Vector256<byte>.Zero).AsUInt64());
                }
            }

            total = (long)(totals.GetElement(0) + totals.GetElement(1) + totals.GetElement(2) + totals.GetElement(3));
        }

        for (; i < stride; i++)
        {
            int x = current[i], a = current[i - Bpp], b = previous[i], c = previous[i - Bpp], predictor;
            switch (filter)
            {
                case 0: predictor = 0; break;
                case 1: predictor = a; break;
                case 2: predictor = b; break;
                case 3: predictor = (a + b) >> 1; break;
                default:
                {
                    int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
                    predictor = pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
                    break;
                }
            }

            byte value = (byte)(x - predictor);
            target[i] = value;
            if (sum) total += value < 128 ? value : 256 - value;
        }

        return total;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Adler(ReadOnlySpan<byte> data, ref uint a, ref uint b)
    {
        const uint modulus = 65521;
        int offset = 0;
        while (offset < data.Length)
        {
            int end = Math.Min(data.Length, offset + 5552); // the most bytes before the sums can overflow
            for (; offset < end; offset++) { a += data[offset]; b += a; }
            a %= modulus; b %= modulus;
        }
    }

    // The checksum of two byte runs joined, from the checksum of each and the length of the second (zlib's adler32_combine).
    private static uint AdlerCombine(uint first, uint second, long secondLength)
    {
        const uint modulus = 65521;
        uint remainder = (uint)(secondLength % modulus);
        uint sum1 = first & 0xFFFF;
        uint sum2 = (uint)((ulong)remainder * sum1 % modulus);
        sum1 += (second & 0xFFFF) + modulus - 1;
        sum2 += ((first >> 16) & 0xFFFF) + ((second >> 16) & 0xFFFF) + modulus - remainder;
        if (sum1 >= modulus) sum1 -= modulus;
        if (sum1 >= modulus) sum1 -= modulus;
        if (sum2 >= modulus << 1) sum2 -= modulus << 1;
        if (sum2 >= modulus) sum2 -= modulus;
        return sum1 | (sum2 << 16);
    }

    private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> first, ReadOnlySpan<byte> second, ReadOnlySpan<byte> third = default)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, first.Length + second.Length + third.Length);
        output.Write(number);
        output.Write(type);
        uint crc = Crc(Crc(Crc(Crc(0xFFFFFFFF, type), first), second), third) ^ 0xFFFFFFFF;
        output.Write(first);
        output.Write(second);
        output.Write(third);
        BinaryPrimitives.WriteUInt32BigEndian(number, crc);
        output.Write(number);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static uint Crc(uint crc, ReadOnlySpan<byte> data)
    {
        uint[] table = CrcTable;
        int i = 0;
        // Eight bytes a step, with eight tables.
        for (; i <= data.Length - 8; i += 8)
        {
            uint low = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(i)) ^ crc, high = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(i + 4));
            crc = table[(7 * 256) + (low & 0xFF)] ^ table[(6 * 256) + ((low >> 8) & 0xFF)] ^ table[(5 * 256) + ((low >> 16) & 0xFF)] ^ table[(4 * 256) + (low >> 24)] ^
                table[(3 * 256) + (high & 0xFF)] ^ table[(2 * 256) + ((high >> 8) & 0xFF)] ^ table[256 + ((high >> 16) & 0xFF)] ^ table[high >> 24];
        }

        for (; i < data.Length; i++) crc = table[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    private static uint[] MakeCrcTable()
    {
        uint[] table = new uint[8 * 256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[n] = c;
        }

        for (int n = 0; n < 256; n++)
        {
            for (int t = 1; t < 8; t++) table[(t * 256) + n] = table[((t - 1) * 256) + n] >> 8 ^ table[table[((t - 1) * 256) + n] & 0xFF];
        }

        return table;
    }
}
