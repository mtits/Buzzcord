using System;
using System.Text;

namespace Buzzcord
{
    // Voice crypto checks against published test vectors (NIST GCM, RFC 5869, RFC 9000, RFC 9180).
    static partial class SelfTest
    {
        static void Same(byte[] actual, string expectedHex, string what)
        {
            string got = Hex.Encode(actual);
            Assert(got == expectedHex.Replace(" ", ""), what + ": got " + got);
        }

        static string GcmTest()
        {
            Func<string, string, string, string, byte[]> seal = (k, n, a, p) =>
            {
                using (var g = new AesGcm(Hex.Decode(k))) return g.Seal(Hex.Decode(n), Hex.Decode(p), a == "" ? null : Hex.Decode(a), 16);
            };
            string z16 = "00000000000000000000000000000000", z12 = "000000000000000000000000";
            // NIST GCM spec test cases 1, 2, 4, 13, 14, 16.
            Same(seal(z16, z12, "", ""), "58e2fccefa7e3061367f1d57a4e7455a", "case 1");
            Same(seal(z16, z12, "", z16), "0388dace60b6a392f328c2b971b2fe78" + "ab6e47d42cec13bdf53a67b21257bddf", "case 2");
            string iv = "cafebabefacedbaddecaf888", aad = "feedfacedeadbeeffeedfacedeadbeefabaddad2";
            string pt = "d9313225f88406e5a55909c5aff5269a86a7a9531534f7da2e4c303d8a318a721c3c0c95956809532fcf0e2449a6b525b16aedf5aa0de657ba637b39";
            Same(seal("feffe9928665731c6d6a8f9467308308", iv, aad, pt),
                "42831ec2217774244b7221b784d0d49ce3aa212f2c02a4e035c17e2329aca12e21d514b25466931c7d8f6a5aac84aa051ba30b396a0aac973d58e091"
                + "5bc94fbc3221a5db94fae95ae7121a47", "case 4");
            Same(seal(z16 + z16, z12, "", ""), "530f8afbc74536b9a963b4f1c4cb738b", "case 13 (AES-256)");
            Same(seal(z16 + z16, z12, "", z16), "cea7403d4d606b6e074ec5d3baf39d18" + "d0d1c8a799996bf0265b98b5d48ab919", "case 14 (AES-256)");
            Same(seal("feffe9928665731c6d6a8f9467308308feffe9928665731c6d6a8f9467308308", iv, aad, pt),
                "522dc1f099567d07f47f37a32a84427d643a8cdcbfe5c0c97598a2bd2555d1aa8cb08e48590dbb3da7b08b1056828838c5f61e6393ba7a0abcc9f662"
                + "76fc6ece0f4e1768cddf8853bb2d551b", "case 16 (AES-256)");

            // DAVE's 8-byte tags: the truncated tag must open, and any change must not.
            using (var g = new AesGcm(Hex.Decode("feffe9928665731c6d6a8f9467308308")))
            {
                var short8 = g.Seal(Hex.Decode(iv), Hex.Decode(pt), null, 8);
                var opened = g.Open(Hex.Decode(iv), short8, null, 8);
                Assert(opened != null && Hex.Encode(opened) == pt, "8-byte tag round trip");
                short8[3] ^= 1;
                Assert(g.Open(Hex.Decode(iv), short8, null, 8) == null, "tampered ciphertext opened");
                short8[3] ^= 1;
                short8[short8.Length - 1] ^= 0x80;
                Assert(g.Open(Hex.Decode(iv), short8, null, 8) == null, "tampered tag opened");
            }
            return "6 NIST cases + 8-byte tags";
        }

        static string HkdfAndTlsTest()
        {
            // RFC 5869 test case 1.
            var prk = Hkdf.Extract(Hex.Decode("000102030405060708090a0b0c"), Hex.Decode("0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b"));
            Same(prk, "077709362c2e32df0ddc3f0dc47bba6390b6c73bb50f9c3122ec844ad7c2b3e5", "HKDF PRK");
            Same(Hkdf.Expand(prk, Hex.Decode("f0f1f2f3f4f5f6f7f8f9"), 42),
                "3cb25f25faacd57a90434f64d0362f2a2d2d0a90cf1a5a4c5db02d56ecc4c5bf34007208d5b887185865", "HKDF OKM");

            // Length prefixes, with the RFC 9000 example values.
            Same(new TlsWriter().VarInt(37).ToArray(), "25", "1-byte length");
            Same(new TlsWriter().VarInt(15293).ToArray(), "7bbd", "2-byte length");
            Same(new TlsWriter().VarInt(494878333).ToArray(), "9d7f3e7d", "4-byte length");
            Assert(new TlsReader(Hex.Decode("9d7f3e7d")).VarInt() == 494878333, "read 4-byte length");
            bool rejected = false;
            try { new TlsReader(Hex.Decode("4025")).VarInt(); } catch (FormatException) { rejected = true; }
            Assert(rejected, "non-minimal length accepted");

            var bytes = new TlsWriter().U16(1).Opaque(Encoding.ASCII.GetBytes("hi"))
                .Vector(w => w.U8(7).U64(0x0102030405060708UL)).Optional(false, null).Optional(true, w => w.U32(9)).ToArray();
            var r = new TlsReader(bytes);
            Assert(r.U16() == 1 && Encoding.ASCII.GetString(r.Opaque()) == "hi", "opaque round trip");
            var v = r.Vector();
            Assert(v.U8() == 7 && v.U64() == 0x0102030405060708UL && v.AtEnd, "vector round trip");
            Assert(!r.Optional() && r.Optional() && r.U32() == 9, "optional round trip");
            r.ExpectEnd();
            return null;
        }

        static string P256Test()
        {
            byte[] a, A, b, B;
            P256.Generate(out a, out A);
            P256.Generate(out b, out B);
            Assert(Bytes.Equal(P256.PublicFromPrivate(a), A), "public key from private (CNG)");
            Assert(Bytes.Equal(P256.Managed.Multiply(a), A), "CNG and managed math disagree");
            Assert(Bytes.Equal(P256.Dh(a, B), P256.Dh(b, A)), "ECDH sides disagree");

            var msg = Encoding.ASCII.GetBytes("Buzzcord voice");
            var sig = P256.Sign(a, msg);
            Assert(P256.Verify(A, msg, sig), "signature doesn't verify");
            Assert(!P256.Verify(B, msg, sig), "verified with the wrong key");
            msg[0] ^= 1;
            Assert(!P256.Verify(A, msg, sig), "verified a changed message");
            var der = P256.ToDer(sig);
            Assert(der[0] == 0x30 && Bytes.Equal(P256.FromDer(der), sig), "DER round trip");
            Assert(P256.FromDer(Hex.Decode("3006020100020100")) != null && P256.FromDer(Hex.Decode("300602")) == null, "DER parsing edge cases");

            Assert(!P256.IsValidPrivate(new byte[32]), "zero scalar accepted");
            Assert(!P256.IsValidPrivate(Hex.Decode("ffffffff00000000ffffffffffffffffbce6faada7179e84f3b9cac2fc632551")), "group order accepted");
            return P256.UsedManagedMath ? "public keys via managed fallback" : "public keys via Windows CNG";
        }

        static string HpkeTest()
        {
            // RFC 9180 appendix A.3.1: DHKEM(P-256, HKDF-SHA256), HKDF-SHA256, AES-128-GCM, base mode.
            byte[] skE, pkE, skR, pkR;
            Hpke.DeriveKeyPair(Hex.Decode("4270e54ffd08d79d5928020af4686d8f6b7d35dbe470265f1f5aa22816ce860e"), out skE, out pkE);
            Same(skE, "4995788ef4b9d6132b249ce59a77281493eb39af373d236a1fe415cb0c2d7beb", "skEm");
            Same(pkE, "04a92719c6195d5085104f469a8b9814d5838ff72b60501e2c4466e5e67b325ac98536d7b61a1af4b78e5b7f951c0900be863c403ce65c9bfcb9382657222d18c4", "pkEm");
            Hpke.DeriveKeyPair(Hex.Decode("668b37171f1072f3cf12ea8a236a45df23fc13b82af3609ad1e354f6ef817550"), out skR, out pkR);
            Same(skR, "f3ce7fdae57e1a310d87f1ebbde6f328be0a99cdbcadf4d6589cf29de4b8ffd2", "skRm");
            Same(pkR, "04fe8c19ce0905191ebc298a9245792531f26f0cece2460639e8bc39cb7f706a826a779b4cf969b8a0e539c7f62fb3d30ad6aa8f80e30f1d128aafd68a2ce72ea0", "pkRm");

            var info = Hex.Decode("4f6465206f6e2061204772656369616e2055726e");
            byte[] enc;
            var s = Hpke.SetupBaseS(pkR, info, out enc, skE);
            Assert(Bytes.Equal(enc, pkE), "enc");
            Same(s.SharedSecret, "c0d26aeab536609a572b07695d933b589dcf363ff9d93c93adea537aeabb8cb8", "shared_secret");
            Same(s.KeyScheduleContext, "00b88d4e6d91759e65e87c470e8b9141113e9ad5f0c8ceefc1e088c82e6980500798e486f9c9c09c9b5c753ac72d6005de254c607d1b534ed11d493ae1c1d9ac85", "key_schedule_context");
            Same(s.Secret, "2eb7b6bf138f6b5aff857414a058a3f1750054a9ba1f72c2cf0684a6f20b10e1", "secret");
            Same(s.Key, "868c066ef58aae6dc589b6cfdd18f97e", "key");
            Same(s.BaseNonce, "4e0bc5018beba4bf004cca59", "base_nonce");
            Same(s.ExporterSecret, "14ad94af484a7ad3ef40e9f3be99ecc6fa9036df9d4920548424df127ee0d99f", "exporter_secret");

            var pt = Hex.Decode("4265617574792069732074727574682c20747275746820626561757479");
            var aad = Hex.Decode("436f756e742d30");
            var ct = s.Seal(aad, pt);
            Same(ct, "5ad590bb8baa577f8619db35a36311226a896e7342a6d836d8b7bcd2f20b6c7f9076ac232e3ab2523f39513434", "first ciphertext");
            Same(s.Export(new byte[0], 32), "5e9bc3d236e1911d95e65b576a8a86d478fb827e8bdfe77b741b289890490d4d", "first export");

            var r = Hpke.SetupBaseR(enc, skR, info);
            Assert(Bytes.Equal(r.Open(aad, ct), pt), "receiver couldn't open");

            // Single-shot with a fresh ephemeral key, as MLS uses it.
            byte[] enc2;
            var sealedMsg = Hpke.Seal(pkR, info, aad, pt, out enc2);
            Assert(Bytes.Equal(Hpke.Open(enc2, skR, info, aad, sealedMsg), pt), "single-shot round trip");
            sealedMsg[0] ^= 1;
            Assert(Hpke.Open(enc2, skR, info, aad, sealedMsg) == null, "tampered HPKE opened");
            return "RFC 9180 A.3.1";
        }
    }
}
