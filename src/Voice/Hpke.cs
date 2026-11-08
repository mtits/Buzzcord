using System;
using System.Text;

namespace Buzzcord
{
    // HPKE (RFC 9180), base mode only, with the one suite DAVE's MLS ciphersuite uses:
    // DHKEM(P-256, HKDF-SHA256), HKDF-SHA256, AES-128-GCM. MLS encrypts path secrets and Welcome
    // messages with it.
    public static class Hpke
    {
        const int KemId = 0x0010, KdfId = 0x0001, AeadId = 0x0001;
        static readonly byte[] Version = Encoding.ASCII.GetBytes("HPKE-v1");
        static readonly byte[] KemSuite = Bytes.Concat(Encoding.ASCII.GetBytes("KEM"), Bytes.U16(KemId));
        static readonly byte[] HpkeSuite = Bytes.Concat(Encoding.ASCII.GetBytes("HPKE"), Bytes.U16(KemId), Bytes.U16(KdfId), Bytes.U16(AeadId));

        public sealed class Context
        {
            public byte[] Key, BaseNonce, ExporterSecret, SharedSecret, KeyScheduleContext, Secret;
            ulong seq;

            public byte[] Seal(byte[] aad, byte[] plaintext)
            {
                using (var gcm = new AesGcm(Key)) return gcm.Seal(NextNonce(), plaintext, aad, 16);
            }

            public byte[] Open(byte[] aad, byte[] ciphertext)
            {
                using (var gcm = new AesGcm(Key))
                {
                    var r = gcm.Open(Nonce(seq), ciphertext, aad, 16);
                    if (r != null) seq++;
                    return r;
                }
            }

            public byte[] Export(byte[] exporterContext, int length)
            {
                return LabeledExpand(HpkeSuite, ExporterSecret, "sec", exporterContext, length);
            }

            byte[] NextNonce() { return Nonce(seq++); }

            byte[] Nonce(ulong n)
            {
                var nonce = (byte[])BaseNonce.Clone();
                for (int i = 0; i < 8; i++) nonce[11 - i] ^= (byte)(n >> (8 * i));
                return nonce;
            }
        }

        // ---- KEM ----

        public static void DeriveKeyPair(byte[] ikm, out byte[] priv, out byte[] pub)
        {
            var prk = LabeledExtract(KemSuite, new byte[0], "dkp_prk", ikm);
            for (int counter = 0; counter < 256; counter++)
            {
                var candidate = LabeledExpand(KemSuite, prk, "candidate", new[] { (byte)counter }, 32);
                if (!P256.IsValidPrivate(candidate)) continue;
                priv = candidate;
                pub = P256.PublicFromPrivate(candidate);
                return;
            }
            throw new InvalidOperationException("HPKE DeriveKeyPair found no valid key");
        }

        static byte[] Encap(byte[] pkR, byte[] ephemeralPriv, out byte[] enc)
        {
            byte[] skE, pkE;
            if (ephemeralPriv != null) { skE = ephemeralPriv; pkE = P256.PublicFromPrivate(skE); }
            else P256.Generate(out skE, out pkE);
            enc = pkE;
            return ExtractAndExpand(P256.Dh(skE, pkR), Bytes.Concat(enc, pkR));
        }

        static byte[] Decap(byte[] enc, byte[] skR)
        {
            var pkR = P256.PublicFromPrivate(skR);
            return ExtractAndExpand(P256.Dh(skR, enc), Bytes.Concat(enc, pkR));
        }

        static byte[] ExtractAndExpand(byte[] dh, byte[] kemContext)
        {
            var prk = LabeledExtract(KemSuite, new byte[0], "eae_prk", dh);
            return LabeledExpand(KemSuite, prk, "shared_secret", kemContext, 32);
        }

        // ---- key schedule (mode_base) ----

        // ephemeralPriv is only for test vectors; normally a fresh key pair is made.
        public static Context SetupBaseS(byte[] pkR, byte[] info, out byte[] enc, byte[] ephemeralPriv = null)
        {
            return KeySchedule(Encap(pkR, ephemeralPriv, out enc), info);
        }

        public static Context SetupBaseR(byte[] enc, byte[] skR, byte[] info)
        {
            return KeySchedule(Decap(enc, skR), info);
        }

        static Context KeySchedule(byte[] sharedSecret, byte[] info)
        {
            var pskIdHash = LabeledExtract(HpkeSuite, new byte[0], "psk_id_hash", new byte[0]);
            var infoHash = LabeledExtract(HpkeSuite, new byte[0], "info_hash", info ?? new byte[0]);
            var ctx = Bytes.Concat(new byte[] { 0 }, pskIdHash, infoHash);
            var secret = LabeledExtract(HpkeSuite, sharedSecret, "secret", new byte[0]);
            return new Context
            {
                SharedSecret = sharedSecret,
                KeyScheduleContext = ctx,
                Secret = secret,
                Key = LabeledExpand(HpkeSuite, secret, "key", ctx, 16),
                BaseNonce = LabeledExpand(HpkeSuite, secret, "base_nonce", ctx, 12),
                ExporterSecret = LabeledExpand(HpkeSuite, secret, "exp", ctx, 32),
            };
        }

        // Single-shot helpers, as MLS uses HPKE.
        public static byte[] Seal(byte[] pkR, byte[] info, byte[] aad, byte[] plaintext, out byte[] enc)
        {
            return SetupBaseS(pkR, info, out enc).Seal(aad, plaintext);
        }

        public static byte[] Open(byte[] enc, byte[] skR, byte[] info, byte[] aad, byte[] ciphertext)
        {
            return SetupBaseR(enc, skR, info).Open(aad, ciphertext);
        }

        // ---- labeled HKDF ----

        static byte[] LabeledExtract(byte[] suite, byte[] salt, string label, byte[] ikm)
        {
            return Hkdf.Extract(salt, Bytes.Concat(Version, suite, Encoding.ASCII.GetBytes(label), ikm));
        }

        static byte[] LabeledExpand(byte[] suite, byte[] prk, string label, byte[] info, int length)
        {
            var labeled = Bytes.Concat(Bytes.U16(length), Version, suite, Encoding.ASCII.GetBytes(label), info ?? new byte[0]);
            return Hkdf.Expand(prk, labeled, length);
        }
    }
}
