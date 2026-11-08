import Foundation

// The TLS presentation language as MLS uses it (RFC 9420 section 2.1): big-endian integers and
// vectors prefixed with a variable-length (1, 2 or 4 byte) length.
final class TlsWriter {
    private(set) var bytes: [UInt8] = []

    init() {}

    @discardableResult func u8(_ v: Int) -> TlsWriter { bytes.append(UInt8(truncatingIfNeeded: v)); return self }

    @discardableResult func u16(_ v: Int) -> TlsWriter {
        bytes.append(UInt8(truncatingIfNeeded: v >> 8))
        bytes.append(UInt8(truncatingIfNeeded: v))
        return self
    }

    @discardableResult func u32(_ v: UInt32) -> TlsWriter {
        for i in stride(from: 3, through: 0, by: -1) { bytes.append(UInt8(truncatingIfNeeded: v >> UInt32(i * 8))) }
        return self
    }

    @discardableResult func u64(_ v: UInt64) -> TlsWriter {
        for i in stride(from: 7, through: 0, by: -1) { bytes.append(UInt8(truncatingIfNeeded: v >> UInt64(i * 8))) }
        return self
    }

    @discardableResult func raw(_ b: [UInt8]) -> TlsWriter { bytes.append(contentsOf: b); return self }

    @discardableResult func varInt(_ v: UInt32) -> TlsWriter {
        if v < 0x40 { return u8(Int(v)) }
        if v < 0x4000 { return u16(Int(v | 0x4000)) }
        precondition(v < 0x4000_0000, "vector too long for MLS")
        return u32(v | 0x8000_0000)
    }

    // opaque<V>: length then bytes.
    @discardableResult func opaque(_ b: [UInt8]?) -> TlsWriter {
        let b = b ?? []
        varInt(UInt32(b.count))
        return raw(b)
    }

    @discardableResult func opaque(_ utf8: String) -> TlsWriter { opaque(Array(utf8.utf8)) }

    // A vector of structs: written into its own buffer, then length-prefixed.
    @discardableResult func vector(_ body: (TlsWriter) throws -> Void) rethrows -> TlsWriter {
        let inner = TlsWriter()
        try body(inner)
        return opaque(inner.bytes)
    }

    // optional<T>: a presence byte, then the value.
    @discardableResult func optional(_ present: Bool, _ body: (TlsWriter) throws -> Void) rethrows -> TlsWriter {
        u8(present ? 1 : 0)
        if present { try body(self) }
        return self
    }

    func toArray() -> [UInt8] { bytes }
    var length: Int { bytes.count }
}

final class TlsReader {
    private let b: [UInt8]
    private(set) var position: Int
    private let end: Int

    convenience init(_ data: [UInt8]) { self.init(data, 0, data.count) }

    init(_ data: [UInt8], _ offset: Int, _ count: Int) {
        b = data
        position = offset
        end = offset + count
    }

    var remaining: Int { end - position }
    var atEnd: Bool { position == end }

    private func need(_ n: Int) throws {
        if n < 0 || end - position < n { throw MlsError.format("MLS message ends early") }
    }

    func u8() throws -> Int {
        try need(1)
        defer { position += 1 }
        return Int(b[position])
    }

    func u16() throws -> Int {
        try need(2)
        defer { position += 2 }
        return Int(b[position]) << 8 | Int(b[position + 1])
    }

    func u32() throws -> UInt32 {
        try need(4)
        var v: UInt32 = 0
        for _ in 0..<4 { v = v << 8 | UInt32(b[position]); position += 1 }
        return v
    }

    func u64() throws -> UInt64 {
        try need(8)
        var v: UInt64 = 0
        for _ in 0..<8 { v = v << 8 | UInt64(b[position]); position += 1 }
        return v
    }

    func raw(_ n: Int) throws -> [UInt8] {
        try need(n)
        defer { position += n }
        return Array(b[position..<position + n])
    }

    // Rejects non-minimal encodings, as RFC 9420 requires.
    func varInt() throws -> UInt32 {
        try need(1)
        switch b[position] >> 6 {
        case 0:
            return UInt32(try u8())
        case 1:
            let v = UInt32(try u16()) & 0x3FFF
            if v < 0x40 { throw MlsError.format("non-minimal length") }
            return v
        case 2:
            let v = try u32() & 0x3FFF_FFFF
            if v < 0x4000 { throw MlsError.format("non-minimal length") }
            return v
        default:
            throw MlsError.format("invalid length prefix")
        }
    }

    func opaque() throws -> [UInt8] { try raw(Int(try varInt())) }

    // A length-prefixed vector, read by its own reader.
    func vector() throws -> TlsReader {
        let n = Int(try varInt())
        try need(n)
        let r = TlsReader(b, position, n)
        position += n
        return r
    }

    func optional() throws -> Bool {
        let p = try u8()
        if p > 1 { throw MlsError.format("invalid optional") }
        return p == 1
    }

    func expectEnd() throws {
        if position != end { throw MlsError.format("unexpected bytes after MLS struct") }
    }

    // The bytes read since `start` (a position taken earlier), for structs kept as they came.
    func since(_ start: Int) -> [UInt8] { Array(b[start..<position]) }
}
