using System;
using System.Security.Cryptography;

namespace Buzzcord
{
    // Crypto building blocks for voice (transport encryption and DAVE). .NET Framework 4.8 has no
    // AES-GCM, and Windows' own GCM refuses the 8-byte tags DAVE uses, so GCM is done here on
    // top of plain AES. Everything else comes from Windows (CNG) or HMAC.

    public static class Hex
    {
        public static byte[] Decode(string hex)
        {
            hex = hex.Replace(" ", "");
            var b = new byte[hex.Length / 2];
            for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return b;
        }

        public static string Encode(byte[] b)
        {
            return b == null ? "" : BitConverter.ToString(b).Replace("-", "").ToLowerInvariant();
        }
    }

    public static class Bytes
    {
        public static byte[] Concat(params byte[][] parts)
        {
            int n = 0;
            foreach (var p in parts) n += p.Length;
            var r = new byte[n];
            int o = 0;
            foreach (var p in parts) { Buffer.BlockCopy(p, 0, r, o, p.Length); o += p.Length; }
            return r;
        }

        public static byte[] Slice(byte[] b, int offset, int count)
        {
            var r = new byte[count];
            Buffer.BlockCopy(b, offset, r, 0, count);
            return r;
        }

        // Compares without stopping at the first difference, so timing says nothing about where.
        public static bool Equal(byte[] a, int aOff, byte[] b, int bOff, int count)
        {
            int diff = 0;
            for (int i = 0; i < count; i++) diff |= a[aOff + i] ^ b[bOff + i];
            return diff == 0;
        }

        public static bool Equal(byte[] a, byte[] b)
        {
            return a != null && b != null && a.Length == b.Length && Equal(a, 0, b, 0, a.Length);
        }

        public static byte[] U16(int v) { return new[] { (byte)(v >> 8), (byte)v }; }
    }

    // AES-GCM (NIST SP 800-38D) with 96-bit nonces and any tag length from 4 to 16 bytes.
    public sealed class AesGcm : IDisposable
    {
        readonly ICryptoTransform aes;
        readonly ulong hHi, hLo;

        public AesGcm(byte[] key)
        {
            if (key.Length != 16 && key.Length != 32) throw new ArgumentException("AES key must be 16 or 32 bytes");
            using (var alg = new AesCryptoServiceProvider { Mode = CipherMode.ECB, Padding = PaddingMode.None, Key = key })
                aes = alg.CreateEncryptor();
            var h = new byte[16];
            aes.TransformBlock(h, 0, 16, h, 0);
            hHi = ReadU64(h, 0);
            hLo = ReadU64(h, 8);
        }

        public void Dispose() { aes.Dispose(); }

        // Returns ciphertext followed by the tag.
        public byte[] Seal(byte[] nonce, byte[] plaintext, byte[] aad, int tagLength)
        {
            return Seal(nonce, plaintext, 0, plaintext.Length, aad, tagLength);
        }

        public byte[] Seal(byte[] nonce, byte[] data, int offset, int count, byte[] aad, int tagLength)
        {
            var output = new byte[count + tagLength];
            Ctr(nonce, data, offset, count, output, 0);
            var tag = Tag(nonce, aad, output, 0, count);
            Buffer.BlockCopy(tag, 0, output, count, tagLength);
            return output;
        }

        // Ciphertext followed by the tag in; plaintext out, or null if it was tampered with.
        public byte[] Open(byte[] nonce, byte[] sealedData, byte[] aad, int tagLength)
        {
            return Open(nonce, sealedData, 0, sealedData.Length, aad, tagLength);
        }

        public byte[] Open(byte[] nonce, byte[] data, int offset, int count, byte[] aad, int tagLength)
        {
            int n = count - tagLength;
            if (n < 0) return null;
            var tag = Tag(nonce, aad, data, offset, n);
            if (!Bytes.Equal(tag, 0, data, offset + n, tagLength)) return null;
            var output = new byte[n];
            Ctr(nonce, data, offset, n, output, 0);
            return output;
        }

        // Keystream starts at counter 2; counter 1 masks the tag.
        void Ctr(byte[] nonce, byte[] input, int inOff, int count, byte[] output, int outOff)
        {
            if (nonce.Length != 12) throw new ArgumentException("GCM nonce must be 12 bytes");
            int blocks = (count + 15) / 16;
            if (blocks == 0) return;
            var counters = new byte[blocks * 16];
            for (int i = 0; i < blocks; i++)
            {
                Buffer.BlockCopy(nonce, 0, counters, i * 16, 12);
                uint c = (uint)(i + 2);
                counters[i * 16 + 12] = (byte)(c >> 24);
                counters[i * 16 + 13] = (byte)(c >> 16);
                counters[i * 16 + 14] = (byte)(c >> 8);
                counters[i * 16 + 15] = (byte)c;
            }
            aes.TransformBlock(counters, 0, counters.Length, counters, 0);
            for (int i = 0; i < count; i++) output[outOff + i] = (byte)(input[inOff + i] ^ counters[i]);
        }

        byte[] Tag(byte[] nonce, byte[] aad, byte[] ct, int ctOff, int ctLen)
        {
            ulong zHi = 0, zLo = 0;
            if (aad != null) Absorb(ref zHi, ref zLo, aad, 0, aad.Length);
            Absorb(ref zHi, ref zLo, ct, ctOff, ctLen);
            zHi ^= (ulong)(aad != null ? aad.Length : 0) * 8;
            zLo ^= (ulong)ctLen * 8;
            Multiply(ref zHi, ref zLo);

            var j0 = new byte[16];
            Buffer.BlockCopy(nonce, 0, j0, 0, 12);
            j0[15] = 1;
            aes.TransformBlock(j0, 0, 16, j0, 0);
            var tag = new byte[16];
            WriteU64(tag, 0, zHi ^ ReadU64(j0, 0));
            WriteU64(tag, 8, zLo ^ ReadU64(j0, 8));
            return tag;
        }

        void Absorb(ref ulong zHi, ref ulong zLo, byte[] data, int off, int len)
        {
            var block = new byte[16];
            for (int i = 0; i < len; i += 16)
            {
                int n = Math.Min(16, len - i);
                Array.Clear(block, 0, 16);
                Buffer.BlockCopy(data, off + i, block, 0, n);
                zHi ^= ReadU64(block, 0);
                zLo ^= ReadU64(block, 8);
                Multiply(ref zHi, ref zLo);
            }
        }

        // Z = Z * H in GF(2^128), bit by bit (SP 800-38D algorithm 1). Plenty fast for voice.
        void Multiply(ref ulong xHi, ref ulong xLo)
        {
            ulong zHi = 0, zLo = 0, vHi = hHi, vLo = hLo;
            for (int i = 0; i < 128; i++)
            {
                ulong bit = i < 64 ? (xHi >> (63 - i)) & 1 : (xLo >> (127 - i)) & 1;
                ulong mask = 0 - bit;
                zHi ^= vHi & mask;
                zLo ^= vLo & mask;
                ulong carry = vLo & 1;
                vLo = (vLo >> 1) | (vHi << 63);
                vHi = (vHi >> 1) ^ (0xE100000000000000UL & (0 - carry));
            }
            xHi = zHi;
            xLo = zLo;
        }

        static ulong ReadU64(byte[] b, int o)
        {
            ulong v = 0;
            for (int i = 0; i < 8; i++) v = (v << 8) | b[o + i];
            return v;
        }

        static void WriteU64(byte[] b, int o, ulong v)
        {
            for (int i = 7; i >= 0; i--) { b[o + i] = (byte)v; v >>= 8; }
        }
    }

    // HKDF with SHA-256 (RFC 5869).
    public static class Hkdf
    {
        public static byte[] Extract(byte[] salt, byte[] ikm)
        {
            using (var h = new HMACSHA256(salt ?? new byte[0])) return h.ComputeHash(ikm);
        }

        public static byte[] Expand(byte[] prk, byte[] info, int length)
        {
            if (length > 255 * 32) throw new ArgumentException("HKDF output too long");
            var output = new byte[length];
            var t = new byte[0];
            using (var h = new HMACSHA256(prk))
            {
                int done = 0;
                for (byte i = 1; done < length; i++)
                {
                    t = h.ComputeHash(Bytes.Concat(t, info ?? new byte[0], new[] { i }));
                    int n = Math.Min(32, length - done);
                    Buffer.BlockCopy(t, 0, output, done, n);
                    done += n;
                }
            }
            return output;
        }
    }
}
