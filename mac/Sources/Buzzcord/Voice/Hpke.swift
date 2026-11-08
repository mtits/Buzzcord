import Foundation

// HPKE (RFC 9180), base mode only, with the one suite DAVE's MLS ciphersuite uses:
// DHKEM(P-256, HKDF-SHA256), HKDF-SHA256, AES-128-GCM. MLS encrypts path secrets and Welcome
// messages with it. CryptoKit's own HPKE takes no fixed ephemeral key and doesn't expose the
// key schedule, which the RFC test vectors need, so this is a direct port of the Windows one.
enum Hpke {
    private static let kemId = 0x0010, kdfId = 0x0001, aeadId = 0x0001
    private static let version = Array("HPKE-v1".utf8)
    private static let kemSuite = Bytes.concat(Array("KEM".utf8), Bytes.u16(kemId))
    private static let hpkeSuite = Bytes.concat(Array("HPKE".utf8), Bytes.u16(kemId), Bytes.u16(kdfId), Bytes.u16(aeadId))

    final class Context {
        let key: [UInt8], baseNonce: [UInt8], exporterSecret: [UInt8]
        let sharedSecret: [UInt8], keyScheduleContext: [UInt8], secret: [UInt8]
        private var seq: UInt64 = 0

        fileprivate init(key: [UInt8], baseNonce: [UInt8], exporterSecret: [UInt8], sharedSecret: [UInt8],
                         keyScheduleContext: [UInt8], secret: [UInt8]) {
            self.key = key
            self.baseNonce = baseNonce
            self.exporterSecret = exporterSecret
            self.sharedSecret = sharedSecret
            self.keyScheduleContext = keyScheduleContext
            self.secret = secret
        }

        func seal(aad: [UInt8], plaintext: [UInt8]) throws -> [UInt8] {
            let n = nonce(seq)
            seq += 1
            return try AesGcm(key: key).seal(nonce: n, plaintext: plaintext, aad: aad, tagLength: 16)
        }

        func open(aad: [UInt8], ciphertext: [UInt8]) throws -> [UInt8]? {
            let r = try AesGcm(key: key).open(nonce: nonce(seq), sealed: ciphertext, aad: aad, tagLength: 16)
            if r != nil { seq += 1 }
            return r
        }

        func export(_ exporterContext: [UInt8], _ length: Int) -> [UInt8] {
            Hpke.labeledExpand(Hpke.hpkeSuite, exporterSecret, "sec", exporterContext, length)
        }

        private func nonce(_ n: UInt64) -> [UInt8] {
            var nonce = baseNonce
            for i in 0..<8 { nonce[11 - i] ^= UInt8(truncatingIfNeeded: n >> UInt64(8 * i)) }
            return nonce
        }
    }

    // ---- KEM ----

    static func deriveKeyPair(_ ikm: [UInt8]) throws -> (priv: [UInt8], pub: [UInt8]) {
        let prk = labeledExtract(kemSuite, [], "dkp_prk", ikm)
        for counter in 0..<256 {
            let candidate = labeledExpand(kemSuite, prk, "candidate", [UInt8(counter)], 32)
            if !NistP256.isValidPrivate(candidate) { continue }
            return (candidate, try NistP256.publicFromPrivate(candidate))
        }
        throw MlsError.invalid("HPKE DeriveKeyPair found no valid key")
    }

    private static func encap(_ pkR: [UInt8], _ ephemeralPriv: [UInt8]?) throws -> (sharedSecret: [UInt8], enc: [UInt8]) {
        let skE: [UInt8], pkE: [UInt8]
        if let ephemeralPriv { skE = ephemeralPriv; pkE = try NistP256.publicFromPrivate(skE) }
        else { (skE, pkE) = NistP256.generate() }
        return (extractAndExpand(try NistP256.dh(skE, pkR), Bytes.concat(pkE, pkR)), pkE)
    }

    private static func decap(_ enc: [UInt8], _ skR: [UInt8]) throws -> [UInt8] {
        let pkR = try NistP256.publicFromPrivate(skR)
        return extractAndExpand(try NistP256.dh(skR, enc), Bytes.concat(enc, pkR))
    }

    private static func extractAndExpand(_ dh: [UInt8], _ kemContext: [UInt8]) -> [UInt8] {
        let prk = labeledExtract(kemSuite, [], "eae_prk", dh)
        return labeledExpand(kemSuite, prk, "shared_secret", kemContext, 32)
    }

    // ---- key schedule (mode_base) ----

    // ephemeralPriv is only for test vectors; normally a fresh key pair is made.
    static func setupBaseS(pkR: [UInt8], info: [UInt8]?, ephemeralPriv: [UInt8]? = nil) throws -> (context: Context, enc: [UInt8]) {
        let (ss, enc) = try encap(pkR, ephemeralPriv)
        return (keySchedule(ss, info), enc)
    }

    static func setupBaseR(enc: [UInt8], skR: [UInt8], info: [UInt8]?) throws -> Context {
        keySchedule(try decap(enc, skR), info)
    }

    private static func keySchedule(_ sharedSecret: [UInt8], _ info: [UInt8]?) -> Context {
        let pskIdHash = labeledExtract(hpkeSuite, [], "psk_id_hash", [])
        let infoHash = labeledExtract(hpkeSuite, [], "info_hash", info ?? [])
        let ctx = Bytes.concat([0], pskIdHash, infoHash)
        let secret = labeledExtract(hpkeSuite, sharedSecret, "secret", [])
        return Context(key: labeledExpand(hpkeSuite, secret, "key", ctx, 16),
                       baseNonce: labeledExpand(hpkeSuite, secret, "base_nonce", ctx, 12),
                       exporterSecret: labeledExpand(hpkeSuite, secret, "exp", ctx, 32),
                       sharedSecret: sharedSecret, keyScheduleContext: ctx, secret: secret)
    }

    // Single-shot helpers, as MLS uses HPKE.
    static func seal(pkR: [UInt8], info: [UInt8]?, aad: [UInt8], plaintext: [UInt8]) throws -> (ciphertext: [UInt8], enc: [UInt8]) {
        let (ctx, enc) = try setupBaseS(pkR: pkR, info: info)
        return (try ctx.seal(aad: aad, plaintext: plaintext), enc)
    }

    static func open(enc: [UInt8], skR: [UInt8], info: [UInt8]?, aad: [UInt8], ciphertext: [UInt8]) throws -> [UInt8]? {
        try setupBaseR(enc: enc, skR: skR, info: info).open(aad: aad, ciphertext: ciphertext)
    }

    // ---- labeled HKDF ----

    private static func labeledExtract(_ suite: [UInt8], _ salt: [UInt8], _ label: String, _ ikm: [UInt8]) -> [UInt8] {
        Hkdf.extract(salt: salt, ikm: Bytes.concat(version, suite, Array(label.utf8), ikm))
    }

    fileprivate static func labeledExpand(_ suite: [UInt8], _ prk: [UInt8], _ label: String, _ info: [UInt8]?, _ length: Int) -> [UInt8] {
        let labeled = Bytes.concat(Bytes.u16(length), version, suite, Array(label.utf8), info ?? [])
        return Hkdf.expand(prk: prk, info: labeled, length: length)
    }
}
