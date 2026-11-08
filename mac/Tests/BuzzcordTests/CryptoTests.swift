import CryptoKit
import XCTest
@testable import Buzzcord

// Voice crypto checks against published test vectors (NIST GCM, RFC 5869, RFC 9000, RFC 9180).
final class CryptoTests: XCTestCase {
    func testAesGcm() throws { XCTAssertEqual(try CryptoChecks.gcm(), "6 NIST cases + 8-byte tags") }
    func testHkdfAndTls() throws { try CryptoChecks.hkdfAndTls() }
    func testP256() throws { XCTAssertEqual(try CryptoChecks.p256(), "public keys via CryptoKit") }
    func testHpke() throws { XCTAssertEqual(try CryptoChecks.hpke(), "RFC 9180 A.3.1") }
}

enum CryptoChecks {
    static func gcm() throws -> String {
        func seal(_ k: String, _ n: String, _ a: String, _ p: String) throws -> [UInt8] {
            try AesGcm(key: Hex.decode(k)).seal(nonce: Hex.decode(n), plaintext: Hex.decode(p), aad: a.isEmpty ? nil : Hex.decode(a), tagLength: 16)
        }
        let z16 = "00000000000000000000000000000000", z12 = "000000000000000000000000"
        // NIST GCM spec test cases 1, 2, 4, 13, 14, 16.
        try same(try seal(z16, z12, "", ""), "58e2fccefa7e3061367f1d57a4e7455a", "case 1")
        try same(try seal(z16, z12, "", z16), "0388dace60b6a392f328c2b971b2fe78" + "ab6e47d42cec13bdf53a67b21257bddf", "case 2")
        let iv = "cafebabefacedbaddecaf888", aad = "feedfacedeadbeeffeedfacedeadbeefabaddad2"
        let pt = "d9313225f88406e5a55909c5aff5269a86a7a9531534f7da2e4c303d8a318a721c3c0c95956809532fcf0e2449a6b525b16aedf5aa0de657ba637b39"
        try same(try seal("feffe9928665731c6d6a8f9467308308", iv, aad, pt),
                 "42831ec2217774244b7221b784d0d49ce3aa212f2c02a4e035c17e2329aca12e21d514b25466931c7d8f6a5aac84aa051ba30b396a0aac973d58e091"
                 + "5bc94fbc3221a5db94fae95ae7121a47", "case 4")
        try same(try seal(z16 + z16, z12, "", ""), "530f8afbc74536b9a963b4f1c4cb738b", "case 13 (AES-256)")
        try same(try seal(z16 + z16, z12, "", z16), "cea7403d4d606b6e074ec5d3baf39d18" + "d0d1c8a799996bf0265b98b5d48ab919", "case 14 (AES-256)")
        try same(try seal("feffe9928665731c6d6a8f9467308308feffe9928665731c6d6a8f9467308308", iv, aad, pt),
                 "522dc1f099567d07f47f37a32a84427d643a8cdcbfe5c0c97598a2bd2555d1aa8cb08e48590dbb3da7b08b1056828838c5f61e6393ba7a0abcc9f662"
                 + "76fc6ece0f4e1768cddf8853bb2d551b", "case 16 (AES-256)")

        // DAVE's 8-byte tags: the truncated tag must open, and any change must not.
        let g = try AesGcm(key: Hex.decode("feffe9928665731c6d6a8f9467308308"))
        var short8 = g.seal(nonce: Hex.decode(iv), plaintext: Hex.decode(pt), aad: nil, tagLength: 8)
        let opened = g.open(nonce: Hex.decode(iv), sealed: short8, aad: nil, tagLength: 8)
        try check(opened != nil && Hex.encode(opened) == pt, "8-byte tag round trip")
        short8[3] ^= 1
        try check(g.open(nonce: Hex.decode(iv), sealed: short8, aad: nil, tagLength: 8) == nil, "tampered ciphertext opened")
        short8[3] ^= 1
        short8[short8.count - 1] ^= 0x80
        try check(g.open(nonce: Hex.decode(iv), sealed: short8, aad: nil, tagLength: 8) == nil, "tampered tag opened")

        // Not in the Windows test: the 16-byte tag agrees with CryptoKit's own AES-GCM, and the
        // 8-byte tag is its prefix.
        let ck = try CryptoKitGcm.seal(key: Hex.decode("feffe9928665731c6d6a8f9467308308"), nonce: Hex.decode(iv), plaintext: Hex.decode(pt), aad: Hex.decode(aad))
        let ours = g.seal(nonce: Hex.decode(iv), plaintext: Hex.decode(pt), aad: Hex.decode(aad), tagLength: 16)
        try check(ck == ours, "disagrees with CryptoKit")
        try check(Array(ours.prefix(ours.count - 8)) == g.seal(nonce: Hex.decode(iv), plaintext: Hex.decode(pt), aad: Hex.decode(aad), tagLength: 8),
                  "8-byte tag isn't the prefix of the full tag")
        return "6 NIST cases + 8-byte tags"
    }

    static func hkdfAndTls() throws {
        // RFC 5869 test case 1.
        let prk = Hkdf.extract(salt: Hex.decode("000102030405060708090a0b0c"), ikm: Hex.decode("0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b"))
        try same(prk, "077709362c2e32df0ddc3f0dc47bba6390b6c73bb50f9c3122ec844ad7c2b3e5", "HKDF PRK")
        try same(Hkdf.expand(prk: prk, info: Hex.decode("f0f1f2f3f4f5f6f7f8f9"), length: 42),
                 "3cb25f25faacd57a90434f64d0362f2a2d2d0a90cf1a5a4c5db02d56ecc4c5bf34007208d5b887185865", "HKDF OKM")

        // Length prefixes, with the RFC 9000 example values.
        try same(TlsWriter().varInt(37).toArray(), "25", "1-byte length")
        try same(TlsWriter().varInt(15293).toArray(), "7bbd", "2-byte length")
        try same(TlsWriter().varInt(494878333).toArray(), "9d7f3e7d", "4-byte length")
        try check(try TlsReader(Hex.decode("9d7f3e7d")).varInt() == 494878333, "read 4-byte length")
        var rejected = false
        do { _ = try TlsReader(Hex.decode("4025")).varInt() } catch MlsError.format { rejected = true }
        try check(rejected, "non-minimal length accepted")

        let bytes = TlsWriter().u16(1).opaque(Array("hi".utf8))
            .vector { $0.u8(7).u64(0x0102030405060708) }.optional(false) { _ in }.optional(true) { $0.u32(9) }.toArray()
        let r = TlsReader(bytes)
        try check(try r.u16() == 1 && String(decoding: try r.opaque(), as: UTF8.self) == "hi", "opaque round trip")
        let v = try r.vector()
        try check(try v.u8() == 7 && (try v.u64()) == 0x0102030405060708 && v.atEnd, "vector round trip")
        try check(!(try r.optional()) && (try r.optional()) && (try r.u32()) == 9, "optional round trip")
        try r.expectEnd()
    }

    static func p256() throws -> String {
        let (a, A) = NistP256.generate()
        let (b, B) = NistP256.generate()
        try check(try NistP256.publicFromPrivate(a) == A, "public key from private")
        try check(try NistP256.dh(a, B) == (try NistP256.dh(b, A)), "ECDH sides disagree")

        var msg = Array("Buzzcord voice".utf8)
        let sig = try NistP256.sign(a, msg)
        try check(NistP256.verify(A, msg, sig), "signature doesn't verify")
        try check(!NistP256.verify(B, msg, sig), "verified with the wrong key")
        msg[0] ^= 1
        try check(!NistP256.verify(A, msg, sig), "verified a changed message")
        let der = NistP256.toDer(sig)
        try check(der[0] == 0x30 && NistP256.fromDer(der) == sig, "DER round trip")
        try check(NistP256.fromDer(Hex.decode("3006020100020100")) != nil && NistP256.fromDer(Hex.decode("300602")) == nil, "DER parsing edge cases")

        try check(!NistP256.isValidPrivate([UInt8](repeating: 0, count: 32)), "zero scalar accepted")
        try check(!NistP256.isValidPrivate(Hex.decode("ffffffff00000000ffffffffffffffffbce6faada7179e84f3b9cac2fc632551")), "group order accepted")
        return "public keys via CryptoKit"
    }

    static func hpke() throws -> String {
        // RFC 9180 appendix A.3.1: DHKEM(P-256, HKDF-SHA256), HKDF-SHA256, AES-128-GCM, base mode.
        let (skE, pkE) = try Hpke.deriveKeyPair(Hex.decode("4270e54ffd08d79d5928020af4686d8f6b7d35dbe470265f1f5aa22816ce860e"))
        try same(skE, "4995788ef4b9d6132b249ce59a77281493eb39af373d236a1fe415cb0c2d7beb", "skEm")
        try same(pkE, "04a92719c6195d5085104f469a8b9814d5838ff72b60501e2c4466e5e67b325ac98536d7b61a1af4b78e5b7f951c0900be863c403ce65c9bfcb9382657222d18c4", "pkEm")
        let (skR, pkR) = try Hpke.deriveKeyPair(Hex.decode("668b37171f1072f3cf12ea8a236a45df23fc13b82af3609ad1e354f6ef817550"))
        try same(skR, "f3ce7fdae57e1a310d87f1ebbde6f328be0a99cdbcadf4d6589cf29de4b8ffd2", "skRm")
        try same(pkR, "04fe8c19ce0905191ebc298a9245792531f26f0cece2460639e8bc39cb7f706a826a779b4cf969b8a0e539c7f62fb3d30ad6aa8f80e30f1d128aafd68a2ce72ea0", "pkRm")

        let info = Hex.decode("4f6465206f6e2061204772656369616e2055726e")
        let (s, enc) = try Hpke.setupBaseS(pkR: pkR, info: info, ephemeralPriv: skE)
        try check(enc == pkE, "enc")
        try same(s.sharedSecret, "c0d26aeab536609a572b07695d933b589dcf363ff9d93c93adea537aeabb8cb8", "shared_secret")
        try same(s.keyScheduleContext, "00b88d4e6d91759e65e87c470e8b9141113e9ad5f0c8ceefc1e088c82e6980500798e486f9c9c09c9b5c753ac72d6005de254c607d1b534ed11d493ae1c1d9ac85", "key_schedule_context")
        try same(s.secret, "2eb7b6bf138f6b5aff857414a058a3f1750054a9ba1f72c2cf0684a6f20b10e1", "secret")
        try same(s.key, "868c066ef58aae6dc589b6cfdd18f97e", "key")
        try same(s.baseNonce, "4e0bc5018beba4bf004cca59", "base_nonce")
        try same(s.exporterSecret, "14ad94af484a7ad3ef40e9f3be99ecc6fa9036df9d4920548424df127ee0d99f", "exporter_secret")

        let pt = Hex.decode("4265617574792069732074727574682c20747275746820626561757479")
        let aad = Hex.decode("436f756e742d30")
        let ct = try s.seal(aad: aad, plaintext: pt)
        try same(ct, "5ad590bb8baa577f8619db35a36311226a896e7342a6d836d8b7bcd2f20b6c7f9076ac232e3ab2523f39513434", "first ciphertext")
        try same(s.export([], 32), "5e9bc3d236e1911d95e65b576a8a86d478fb827e8bdfe77b741b289890490d4d", "first export")

        let r = try Hpke.setupBaseR(enc: enc, skR: skR, info: info)
        try check(try r.open(aad: aad, ciphertext: ct) == pt, "receiver couldn't open")

        // Single-shot with a fresh ephemeral key, as MLS uses it.
        var (sealed, enc2) = try Hpke.seal(pkR: pkR, info: info, aad: aad, plaintext: pt)
        try check(try Hpke.open(enc: enc2, skR: skR, info: info, aad: aad, ciphertext: sealed) == pt, "single-shot round trip")
        sealed[0] ^= 1
        try check(try Hpke.open(enc: enc2, skR: skR, info: info, aad: aad, ciphertext: sealed) == nil, "tampered HPKE opened")
        return "RFC 9180 A.3.1"
    }
}

// CryptoKit's AES-GCM (16-byte tags only), as a reference for ours.
enum CryptoKitGcm {
    static func seal(key: [UInt8], nonce: [UInt8], plaintext: [UInt8], aad: [UInt8]) throws -> [UInt8] {
        let box = try AES.GCM.seal(plaintext, using: SymmetricKey(data: key), nonce: AES.GCM.Nonce(data: nonce), authenticating: aad)
        return [UInt8](box.ciphertext) + [UInt8](box.tag)
    }
}
