import CommonCrypto
import CryptoKit
import Foundation

// Crypto building blocks for voice (transport encryption and DAVE). CryptoKit's AES.GCM only
// does 16-byte tags and DAVE uses 8-byte ones, so GCM is done here on top of plain AES (ECB from
// CommonCrypto). Everything else comes from CryptoKit.

// What the voice crypto and MLS code throws. The cases mirror the Windows version's exceptions:
// bytes that don't parse, a message that parses but is wrong, and features DAVE never uses.
enum MlsError: Error, LocalizedError, CustomStringConvertible {
    case format(String)
    case invalid(String)
    case notSupported(String)

    var description: String {
        switch self {
        case .format(let s), .invalid(let s), .notSupported(let s): return s
        }
    }

    var errorDescription: String? { description }
}

enum Hex {
    static func decode(_ hex: String) -> [UInt8] {
        var out: [UInt8] = []
        out.reserveCapacity(hex.utf8.count / 2)
        var hi: UInt8? = nil
        for c in hex.utf8 {
            let v: UInt8
            switch c {
            case 48...57: v = c - 48
            case 97...102: v = c - 87
            case 65...70: v = c - 55
            default: continue   // spaces
            }
            if let h = hi { out.append(h << 4 | v); hi = nil } else { hi = v }
        }
        return out
    }

    static func encode(_ b: [UInt8]?) -> String {
        guard let b else { return "" }
        let digits = Array("0123456789abcdef".utf8)
        var s = [UInt8]()
        s.reserveCapacity(b.count * 2)
        for x in b { s.append(digits[Int(x >> 4)]); s.append(digits[Int(x & 15)]) }
        return String(decoding: s, as: UTF8.self)
    }
}

enum Bytes {
    static func concat(_ parts: [UInt8]...) -> [UInt8] {
        var r = [UInt8]()
        r.reserveCapacity(parts.reduce(0) { $0 + $1.count })
        for p in parts { r.append(contentsOf: p) }
        return r
    }

    static func slice(_ b: [UInt8], _ offset: Int, _ count: Int) -> [UInt8] {
        Array(b[offset..<offset + count])
    }

    // Compares without stopping at the first difference, so timing says nothing about where.
    static func equal(_ a: [UInt8], _ aOff: Int, _ b: [UInt8], _ bOff: Int, _ count: Int) -> Bool {
        var diff: UInt8 = 0
        for i in 0..<count { diff |= a[aOff + i] ^ b[bOff + i] }
        return diff == 0
    }

    static func equal(_ a: [UInt8]?, _ b: [UInt8]?) -> Bool {
        guard let a, let b, a.count == b.count else { return false }
        return equal(a, 0, b, 0, a.count)
    }

    static func u16(_ v: Int) -> [UInt8] { [UInt8(truncatingIfNeeded: v >> 8), UInt8(truncatingIfNeeded: v)] }
}

// AES-GCM (NIST SP 800-38D) with 96-bit nonces and any tag length from 4 to 16 bytes. Holds no
// mutable state (each call does one CCCrypt), so one instance can seal and open on several
// threads at once.
final class AesGcm: Sendable {
    private let key: [UInt8]
    private let hHi: UInt64, hLo: UInt64

    init(key: [UInt8]) throws {
        guard key.count == 16 || key.count == 32 else { throw MlsError.invalid("AES key must be 16 or 32 bytes") }
        self.key = key
        let h = AesGcm.ecb(key, [UInt8](repeating: 0, count: 16))
        hHi = AesGcm.readU64(h, 0)
        hLo = AesGcm.readU64(h, 8)
    }

    // Returns ciphertext followed by the tag.
    func seal(nonce: [UInt8], plaintext: [UInt8], aad: [UInt8]?, tagLength: Int) -> [UInt8] {
        seal(nonce: nonce, data: plaintext, offset: 0, count: plaintext.count, aad: aad, tagLength: tagLength)
    }

    func seal(nonce: [UInt8], data: [UInt8], offset: Int, count: Int, aad: [UInt8]?, tagLength: Int) -> [UInt8] {
        precondition(tagLength >= 4 && tagLength <= 16, "GCM tag must be 4 to 16 bytes")
        let ks = keystream(nonce, count)
        var output = [UInt8](repeating: 0, count: count + tagLength)
        for i in 0..<count { output[i] = data[offset + i] ^ ks[16 + i] }
        let tag = self.tag(ks, aad, output, 0, count)
        for i in 0..<tagLength { output[count + i] = tag[i] }
        return output
    }

    // Ciphertext followed by the tag in; plaintext out, or nil if it was tampered with.
    func open(nonce: [UInt8], sealed: [UInt8], aad: [UInt8]?, tagLength: Int) -> [UInt8]? {
        open(nonce: nonce, data: sealed, offset: 0, count: sealed.count, aad: aad, tagLength: tagLength)
    }

    func open(nonce: [UInt8], data: [UInt8], offset: Int, count: Int, aad: [UInt8]?, tagLength: Int) -> [UInt8]? {
        precondition(tagLength >= 4 && tagLength <= 16, "GCM tag must be 4 to 16 bytes")
        let n = count - tagLength
        if n < 0 { return nil }
        let ks = keystream(nonce, n)
        let tag = self.tag(ks, aad, data, offset, n)
        if !Bytes.equal(tag, 0, data, offset + n, tagLength) { return nil }
        var output = [UInt8](repeating: 0, count: n)
        for i in 0..<n { output[i] = data[offset + i] ^ ks[16 + i] }
        return output
    }

    // E(J0) (counter 1, which masks the tag), then the keystream from counter 2 on, in one call.
    private func keystream(_ nonce: [UInt8], _ count: Int) -> [UInt8] {
        precondition(nonce.count == 12, "GCM nonce must be 12 bytes")
        let blocks = 1 + (count + 15) / 16
        var counters = [UInt8](repeating: 0, count: blocks * 16)
        for i in 0..<blocks {
            let o = i * 16
            for j in 0..<12 { counters[o + j] = nonce[j] }
            let c = UInt32(i + 1)
            counters[o + 12] = UInt8(truncatingIfNeeded: c >> 24)
            counters[o + 13] = UInt8(truncatingIfNeeded: c >> 16)
            counters[o + 14] = UInt8(truncatingIfNeeded: c >> 8)
            counters[o + 15] = UInt8(truncatingIfNeeded: c)
        }
        return AesGcm.ecb(key, counters)
    }

    private static func ecb(_ key: [UInt8], _ input: [UInt8]) -> [UInt8] {
        var output = [UInt8](repeating: 0, count: input.count)
        var moved = 0
        let status = CCCrypt(CCOperation(kCCEncrypt), CCAlgorithm(kCCAlgorithmAES), CCOptions(kCCOptionECBMode),
                             key, key.count, nil, input, input.count, &output, output.count, &moved)
        precondition(status == kCCSuccess && moved == input.count, "AES-ECB failed (\(status))")
        return output
    }

    private func tag(_ ks: [UInt8], _ aad: [UInt8]?, _ ct: [UInt8], _ ctOff: Int, _ ctLen: Int) -> [UInt8] {
        var zHi: UInt64 = 0, zLo: UInt64 = 0
        if let aad { absorb(&zHi, &zLo, aad, 0, aad.count) }
        absorb(&zHi, &zLo, ct, ctOff, ctLen)
        zHi ^= UInt64(aad?.count ?? 0) &* 8
        zLo ^= UInt64(ctLen) &* 8
        multiply(&zHi, &zLo)
        var tag = [UInt8](repeating: 0, count: 16)
        AesGcm.writeU64(&tag, 0, zHi ^ AesGcm.readU64(ks, 0))
        AesGcm.writeU64(&tag, 8, zLo ^ AesGcm.readU64(ks, 8))
        return tag
    }

    private func absorb(_ zHi: inout UInt64, _ zLo: inout UInt64, _ data: [UInt8], _ off: Int, _ len: Int) {
        var i = 0
        while i < len {
            let n = min(16, len - i)
            var hi: UInt64 = 0, lo: UInt64 = 0
            for j in 0..<16 {
                let b = j < n ? UInt64(data[off + i + j]) : 0
                if j < 8 { hi = hi << 8 | b } else { lo = lo << 8 | b }
            }
            zHi ^= hi
            zLo ^= lo
            multiply(&zHi, &zLo)
            i += 16
        }
    }

    // Z = Z * H in GF(2^128), bit by bit (SP 800-38D algorithm 1): constant-time, and plenty
    // fast for voice.
    private func multiply(_ xHi: inout UInt64, _ xLo: inout UInt64) {
        var zHi: UInt64 = 0, zLo: UInt64 = 0, vHi = hHi, vLo = hLo
        for i in 0..<128 {
            let bit = i < 64 ? (xHi >> UInt64(63 - i)) & 1 : (xLo >> UInt64(127 - i)) & 1
            let mask = 0 &- bit
            zHi ^= vHi & mask
            zLo ^= vLo & mask
            let carry = vLo & 1
            vLo = (vLo >> 1) | (vHi << 63)
            vHi = (vHi >> 1) ^ (0xE100000000000000 & (0 &- carry))
        }
        xHi = zHi
        xLo = zLo
    }

    private static func readU64(_ b: [UInt8], _ o: Int) -> UInt64 {
        var v: UInt64 = 0
        for i in 0..<8 { v = v << 8 | UInt64(b[o + i]) }
        return v
    }

    private static func writeU64(_ b: inout [UInt8], _ o: Int, _ v: UInt64) {
        var v = v
        for i in stride(from: 7, through: 0, by: -1) { b[o + i] = UInt8(truncatingIfNeeded: v); v >>= 8 }
    }
}

// HKDF with SHA-256 (RFC 5869).
enum Hkdf {
    static func extract(salt: [UInt8]?, ikm: [UInt8]) -> [UInt8] {
        Array(HMAC<SHA256>.authenticationCode(for: ikm, using: SymmetricKey(data: salt ?? [])))
    }

    static func expand(prk: [UInt8], info: [UInt8]?, length: Int) -> [UInt8] {
        precondition(length <= 255 * 32, "HKDF output too long")
        let key = SymmetricKey(data: prk)
        var output = [UInt8]()
        output.reserveCapacity(length)
        var t = [UInt8]()
        var i: UInt8 = 1
        while output.count < length {
            var h = HMAC<SHA256>(key: key)
            h.update(data: t)
            if let info { h.update(data: info) }
            h.update(data: [i])
            t = Array(h.finalize())
            output.append(contentsOf: t.prefix(length - output.count))
            i &+= 1
        }
        return output
    }
}
