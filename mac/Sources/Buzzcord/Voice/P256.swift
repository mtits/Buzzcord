import CryptoKit
import Foundation

// NIST P-256 through CryptoKit: key pairs, ECDH and ECDSA, the curve DAVE's MLS ciphersuite uses.
// Keys travel as plain bytes: private = 32-byte scalar, public = 65-byte uncompressed point
// (04 || X || Y), signatures = 64-byte r || s. Named NistP256 so it doesn't hide CryptoKit's P256.
enum NistP256 {
    private static let order = Hex.decode("ffffffff00000000ffffffffffffffffbce6faada7179e84f3b9cac2fc632551")

    static func generate() -> (priv: [UInt8], pub: [UInt8]) {
        let k = P256.KeyAgreement.PrivateKey()
        return ([UInt8](k.rawRepresentation), [UInt8](k.publicKey.x963Representation))
    }

    // 0 < d < n: what HPKE's DeriveKeyPair checks before accepting a candidate scalar.
    static func isValidPrivate(_ d: [UInt8]) -> Bool {
        if d.count != 32 || d.allSatisfy({ $0 == 0 }) { return false }
        for i in 0..<32 {
            if d[i] < order[i] { return true }
            if d[i] > order[i] { return false }
        }
        return false
    }

    static func publicFromPrivate(_ priv: [UInt8]) throws -> [UInt8] {
        [UInt8](try agreementKey(priv).publicKey.x963Representation)
    }

    // The x-coordinate of d * peer: P-256's Diffie-Hellman output.
    static func dh(_ priv: [UInt8], _ peerPub: [UInt8]) throws -> [UInt8] {
        let mine = try agreementKey(priv)
        guard peerPub.count == 65, peerPub[0] == 4,
              let theirs = try? P256.KeyAgreement.PublicKey(x963Representation: peerPub)
        else { throw MlsError.invalid("not an uncompressed P-256 point") }
        guard let secret = try? mine.sharedSecretFromKeyAgreement(with: theirs) else { throw MlsError.invalid("P-256 agreement failed") }
        return secret.withUnsafeBytes { [UInt8]($0) }
    }

    // ECDSA over SHA-256 of the message; returns r || s.
    static func sign(_ priv: [UInt8], _ message: [UInt8]) throws -> [UInt8] {
        guard let key = try? P256.Signing.PrivateKey(rawRepresentation: priv) else { throw MlsError.invalid("not a P-256 private key") }
        guard let sig = try? key.signature(for: message) else { throw MlsError.invalid("P-256 sign failed") }
        return [UInt8](sig.rawRepresentation)
    }

    static func verify(_ pub: [UInt8], _ message: [UInt8], _ sig: [UInt8]?) -> Bool {
        guard let sig, sig.count == 64, pub.count == 65, pub[0] == 4,
              let key = try? P256.Signing.PublicKey(x963Representation: pub),
              let s = try? P256.Signing.ECDSASignature(rawRepresentation: sig)
        else { return false }
        return key.isValidSignature(s, for: message)
    }

    private static func agreementKey(_ priv: [UInt8]) throws -> P256.KeyAgreement.PrivateKey {
        guard isValidPrivate(priv), let k = try? P256.KeyAgreement.PrivateKey(rawRepresentation: priv)
        else { throw MlsError.invalid("not a P-256 private key") }
        return k
    }

    // ---- DER (SEQUENCE of two INTEGERs), the signature form MLS carries ----

    static func toDer(_ rs: [UInt8]) -> [UInt8] {
        let r = derInt(Bytes.slice(rs, 0, 32))
        let s = derInt(Bytes.slice(rs, 32, 32))
        return Bytes.concat([0x30, UInt8(r.count + s.count)], r, s)
    }

    static func fromDer(_ der: [UInt8]) -> [UInt8]? {
        if der.count < 8 || der[0] != 0x30 || Int(der[1]) != der.count - 2 { return nil }
        var pos = 2
        guard let r = readDerInt(der, &pos), let s = readDerInt(der, &pos), pos == der.count else { return nil }
        return Bytes.concat(r, s)
    }

    private static func derInt(_ v: [UInt8]) -> [UInt8] {
        var i = 0
        while i < v.count - 1 && v[i] == 0 { i += 1 }
        var trimmed = Array(v[i...])
        if trimmed[0] & 0x80 != 0 { trimmed.insert(0, at: 0) }
        return Bytes.concat([0x02, UInt8(trimmed.count)], trimmed)
    }

    private static func readDerInt(_ der: [UInt8], _ pos: inout Int) -> [UInt8]? {
        if pos + 2 > der.count || der[pos] != 0x02 { return nil }
        var len = Int(der[pos + 1])
        pos += 2
        var start = pos
        pos += len
        if len == 0 || pos > der.count { return nil }
        while len > 0 && der[start] == 0 { start += 1; len -= 1 }
        if len > 32 { return nil }
        var r = [UInt8](repeating: 0, count: 32)
        for i in 0..<len { r[32 - len + i] = der[start + i] }
        return r
    }
}
