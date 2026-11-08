using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Buzzcord
{
    // NIST P-256 through Windows CNG (bcrypt.dll): key pairs, ECDH and ECDSA, the curve DAVE's MLS
    // ciphersuite uses. Keys travel as plain bytes: private = 32-byte scalar, public = 65-byte
    // uncompressed point (04 || X || Y), signatures = 64-byte r || s.
    public static class P256
    {
        const int EcdhPublicMagic = 0x314B4345, EcdhPrivateMagic = 0x324B4345;
        const int EcdsaPublicMagic = 0x31534345, EcdsaPrivateMagic = 0x32534345;

        static readonly IntPtr ecdh = Open("ECDH_P256"), ecdsa = Open("ECDSA_P256");

        static readonly byte[] Order = Hex.Decode("ffffffff00000000ffffffffffffffffbce6faada7179e84f3b9cac2fc632551");

        public static void Generate(out byte[] priv, out byte[] pub)
        {
            IntPtr key;
            Check(BCryptGenerateKeyPair(ecdh, out key, 256, 0), "generate");
            try
            {
                Check(BCryptFinalizeKeyPair(key, 0), "finalize");
                var blob = Export(key, "ECCPRIVATEBLOB", 8 + 96);
                pub = Bytes.Concat(new byte[] { 4 }, Bytes.Slice(blob, 8, 64));
                priv = Bytes.Slice(blob, 72, 32);
            }
            finally { BCryptDestroyKey(key); }
        }

        // 0 < d < n: what HPKE's DeriveKeyPair checks before accepting a candidate scalar.
        public static bool IsValidPrivate(byte[] d)
        {
            if (d.Length != 32) return false;
            bool zero = true;
            foreach (var x in d) if (x != 0) { zero = false; break; }
            if (zero) return false;
            for (int i = 0; i < 32; i++)
            {
                if (d[i] < Order[i]) return true;
                if (d[i] > Order[i]) return false;
            }
            return false;
        }

        public static byte[] PublicFromPrivate(byte[] priv)
        {
            // CNG fills in the public point when it's left as zeros on import.
            try
            {
                IntPtr key = Import(ecdh, "ECCPRIVATEBLOB", Blob(EcdhPrivateMagic, new byte[64], priv));
                try { return Bytes.Concat(new byte[] { 4 }, Bytes.Slice(Export(key, "ECCPUBLICBLOB", 8 + 64), 8, 64)); }
                finally { BCryptDestroyKey(key); }
            }
            catch (CryptographicException) { UsedManagedMath = true; return Managed.Multiply(priv); }
        }

        public static bool UsedManagedMath;   // CNG wouldn't derive a public key; reported by the self-test

        // The x-coordinate of d * peer: P-256's Diffie-Hellman output.
        public static byte[] Dh(byte[] priv, byte[] peerPub)
        {
            IntPtr mine = ImportPrivate(ecdh, EcdhPrivateMagic, priv), theirs = IntPtr.Zero, secret = IntPtr.Zero;
            try
            {
                theirs = ImportPublic(ecdh, EcdhPublicMagic, peerPub);
                Check(BCryptSecretAgreement(mine, theirs, out secret, 0), "agreement");
                var raw = new byte[32];
                int n;
                Check(BCryptDeriveKey(secret, "TRUNCATE", IntPtr.Zero, raw, raw.Length, out n, 0), "raw secret");
                Array.Reverse(raw); // CNG hands the raw secret back little-endian
                return raw;
            }
            finally
            {
                if (secret != IntPtr.Zero) BCryptDestroySecret(secret);
                if (theirs != IntPtr.Zero) BCryptDestroyKey(theirs);
                BCryptDestroyKey(mine);
            }
        }

        // ECDSA over SHA-256 of the message; returns r || s.
        public static byte[] Sign(byte[] priv, byte[] message)
        {
            byte[] hash;
            using (var sha = SHA256.Create()) hash = sha.ComputeHash(message);
            IntPtr key = ImportPrivate(ecdsa, EcdsaPrivateMagic, priv);
            try
            {
                var sig = new byte[64];
                int n;
                Check(BCryptSignHash(key, IntPtr.Zero, hash, hash.Length, sig, sig.Length, out n, 0), "sign");
                return sig;
            }
            finally { BCryptDestroyKey(key); }
        }

        public static bool Verify(byte[] pub, byte[] message, byte[] sig)
        {
            if (sig == null || sig.Length != 64) return false;
            byte[] hash;
            using (var sha = SHA256.Create()) hash = sha.ComputeHash(message);
            IntPtr key;
            try { key = ImportPublic(ecdsa, EcdsaPublicMagic, pub); }
            catch (CryptographicException) { return false; }
            try { return BCryptVerifySignature(key, IntPtr.Zero, hash, hash.Length, sig, sig.Length, 0) == 0; }
            finally { BCryptDestroyKey(key); }
        }

        // ---- DER (SEQUENCE of two INTEGERs), the signature form MLS carries ----

        public static byte[] ToDer(byte[] rs)
        {
            var r = DerInt(Bytes.Slice(rs, 0, 32));
            var s = DerInt(Bytes.Slice(rs, 32, 32));
            return Bytes.Concat(new byte[] { 0x30, (byte)(r.Length + s.Length) }, r, s);
        }

        public static byte[] FromDer(byte[] der)
        {
            try
            {
                if (der.Length < 8 || der[0] != 0x30 || der[1] != der.Length - 2) return null;
                int pos = 2;
                var r = ReadDerInt(der, ref pos);
                var s = ReadDerInt(der, ref pos);
                if (r == null || s == null || pos != der.Length) return null;
                return Bytes.Concat(r, s);
            }
            catch (IndexOutOfRangeException) { return null; }
        }

        static byte[] DerInt(byte[] v)
        {
            int i = 0;
            while (i < v.Length - 1 && v[i] == 0) i++;
            var trimmed = Bytes.Slice(v, i, v.Length - i);
            if ((trimmed[0] & 0x80) != 0) trimmed = Bytes.Concat(new byte[] { 0 }, trimmed);
            return Bytes.Concat(new byte[] { 0x02, (byte)trimmed.Length }, trimmed);
        }

        static byte[] ReadDerInt(byte[] der, ref int pos)
        {
            if (der[pos] != 0x02) return null;
            int len = der[pos + 1];
            pos += 2;
            int start = pos;
            pos += len;
            if (len == 0 || pos > der.Length) return null;
            while (len > 0 && der[start] == 0) { start++; len--; }
            if (len > 32) return null;
            var r = new byte[32];
            Buffer.BlockCopy(der, start, r, 32 - len, len);
            return r;
        }

        // ---- CNG plumbing ----

        static byte[] Blob(int magic, byte[] xy, byte[] d)
        {
            var header = new byte[8];
            BitConverter.GetBytes(magic).CopyTo(header, 0);
            BitConverter.GetBytes(32).CopyTo(header, 4);
            return d == null ? Bytes.Concat(header, xy) : Bytes.Concat(header, xy, d);
        }

        static IntPtr ImportPublic(IntPtr alg, int magic, byte[] pub)
        {
            if (pub == null || pub.Length != 65 || pub[0] != 4) throw new CryptographicException("not an uncompressed P-256 point");
            return Import(alg, "ECCPUBLICBLOB", Blob(magic, Bytes.Slice(pub, 1, 64), null));
        }

        static IntPtr ImportPrivate(IntPtr alg, int magic, byte[] priv)
        {
            var pub = PublicFromPrivate(priv);
            return Import(alg, "ECCPRIVATEBLOB", Blob(magic, Bytes.Slice(pub, 1, 64), priv));
        }

        static IntPtr Import(IntPtr alg, string type, byte[] blob)
        {
            IntPtr key;
            Check(BCryptImportKeyPair(alg, IntPtr.Zero, type, out key, blob, blob.Length, 0), "import key");
            return key;
        }

        static byte[] Export(IntPtr key, string type, int size)
        {
            var blob = new byte[size];
            int n;
            Check(BCryptExportKey(key, IntPtr.Zero, type, blob, blob.Length, out n, 0), "export key");
            return blob;
        }

        static IntPtr Open(string name)
        {
            IntPtr h;
            Check(BCryptOpenAlgorithmProvider(out h, name, null, 0), "open " + name);
            return h;
        }

        static void Check(int status, string what)
        {
            if (status != 0) throw new CryptographicException("P-256 " + what + " failed (0x" + status.ToString("X8") + ")");
        }

        [DllImport("bcrypt.dll", CharSet = CharSet.Unicode)]
        static extern int BCryptOpenAlgorithmProvider(out IntPtr alg, string algId, string implementation, int flags);
        [DllImport("bcrypt.dll")]
        static extern int BCryptGenerateKeyPair(IntPtr alg, out IntPtr key, int bits, int flags);
        [DllImport("bcrypt.dll")]
        static extern int BCryptFinalizeKeyPair(IntPtr key, int flags);
        [DllImport("bcrypt.dll", CharSet = CharSet.Unicode)]
        static extern int BCryptExportKey(IntPtr key, IntPtr exportKey, string blobType, byte[] output, int size, out int written, int flags);
        [DllImport("bcrypt.dll", CharSet = CharSet.Unicode)]
        static extern int BCryptImportKeyPair(IntPtr alg, IntPtr importKey, string blobType, out IntPtr key, byte[] input, int size, int flags);
        [DllImport("bcrypt.dll")]
        static extern int BCryptDestroyKey(IntPtr key);
        [DllImport("bcrypt.dll")]
        static extern int BCryptSecretAgreement(IntPtr priv, IntPtr pub, out IntPtr secret, int flags);
        [DllImport("bcrypt.dll", CharSet = CharSet.Unicode)]
        static extern int BCryptDeriveKey(IntPtr secret, string kdf, IntPtr parameters, byte[] output, int size, out int written, int flags);
        [DllImport("bcrypt.dll")]
        static extern int BCryptDestroySecret(IntPtr secret);
        [DllImport("bcrypt.dll")]
        static extern int BCryptSignHash(IntPtr key, IntPtr padding, byte[] hash, int hashSize, byte[] sig, int sigSize, out int written, int flags);
        [DllImport("bcrypt.dll")]
        static extern int BCryptVerifySignature(IntPtr key, IntPtr padding, byte[] hash, int hashSize, byte[] sig, int sigSize, int flags);

        // d * G in plain managed code: the fallback for PublicFromPrivate, and a cross-check of CNG
        // in the self-test. Not constant-time, so it isn't used when CNG works.
        public static class Managed
        {
            static readonly BigInteger P = Num("ffffffff00000001000000000000000000000000ffffffffffffffffffffffff");
            static readonly BigInteger Gx = Num("6b17d1f2e12c4247f8bce6e563a440f277037d812deb33a0f4a13945d898c296");
            static readonly BigInteger Gy = Num("4fe342e2fe1a7f9b8ee7eb4a7c0f9e162bce33576b315ececbb6406837bf51f5");

            public static byte[] Multiply(byte[] scalar)
            {
                var k = Num(Hex.Encode(scalar));
                BigInteger rx = 0, ry = 0;
                bool inf = true;
                BigInteger ax = Gx, ay = Gy;
                for (int i = 0; i < 256; i++)
                {
                    if (!(k >> i).IsEven)
                    {
                        if (inf) { rx = ax; ry = ay; inf = false; }
                        else Add(ref rx, ref ry, ax, ay);
                    }
                    Add(ref ax, ref ay, ax, ay);
                }
                if (inf) throw new CryptographicException("scalar is zero");
                return Bytes.Concat(new byte[] { 4 }, Fixed(rx), Fixed(ry));
            }

            static void Add(ref BigInteger x1, ref BigInteger y1, BigInteger x2, BigInteger y2)
            {
                BigInteger l;
                if (x1 == x2 && y1 == y2) l = Mod(3 * x1 * x1 - 3) * Inv(2 * y1);
                else l = Mod(y2 - y1) * Inv(x2 - x1);
                l = Mod(l);
                var x3 = Mod(l * l - x1 - x2);
                var y3 = Mod(l * (x1 - x3) - y1);
                x1 = x3;
                y1 = y3;
            }

            static BigInteger Mod(BigInteger v) { v %= P; return v.Sign < 0 ? v + P : v; }
            static BigInteger Inv(BigInteger v) { return BigInteger.ModPow(Mod(v), P - 2, P); }

            static BigInteger Num(string hex) { return BigInteger.Parse("0" + hex, System.Globalization.NumberStyles.HexNumber); }

            static byte[] Fixed(BigInteger v)
            {
                var le = v.ToByteArray(); // little-endian, maybe with a sign byte
                var r = new byte[32];
                for (int i = 0; i < 32 && i < le.Length; i++) r[31 - i] = le[i];
                return r;
            }
        }
    }
}
