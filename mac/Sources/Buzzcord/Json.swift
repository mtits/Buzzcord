import Foundation

// Discord's JSON, as plain dictionaries: the same loose access the Windows version's JObj gives.
typealias JObj = [String: Any]

extension Dictionary where Key == String, Value == Any {
    func has(_ k: String) -> Bool { self[k] != nil }

    func str(_ k: String) -> String? {
        if let s = self[k] as? String { return s }
        if let n = self[k] as? NSNumber, CFGetTypeID(n) != CFBooleanGetTypeID() { return n.stringValue }
        return nil
    }

    func long(_ k: String, _ def: Int64 = 0) -> Int64 {
        if let n = self[k] as? NSNumber { return n.int64Value }
        if let s = self[k] as? String {
            if let v = Int64(s) { return v }
            if let v = UInt64(s) { return Int64(bitPattern: v) }
        }
        return def
    }

    func bool(_ k: String) -> Bool { (self[k] as? NSNumber)?.boolValue ?? false }
    func obj(_ k: String) -> JObj? { self[k] as? JObj }
    func arr(_ k: String) -> [Any]? { self[k] as? [Any] }
    func objs(_ k: String) -> [JObj] { (self[k] as? [Any])?.compactMap { $0 as? JObj } ?? [] }
    func strs(_ k: String) -> [String] {
        (self[k] as? [Any])?.compactMap { v -> String? in
            if let s = v as? String { return s }
            if let n = v as? NSNumber { return n.stringValue }
            return nil
        } ?? []
    }
}

enum Json {
    static func parse(_ data: Data) -> Any? {
        try? JSONSerialization.jsonObject(with: data, options: [.fragmentsAllowed])
    }

    static func data(_ value: Any) -> Data {
        (try? JSONSerialization.data(withJSONObject: value, options: [.fragmentsAllowed])) ?? Data("null".utf8)
    }

    static func string(_ value: Any) -> String { String(decoding: data(value), as: UTF8.self) }

    // A JavaScript string literal, safe to put inside a <script>.
    static func js(_ s: String?) -> String {
        guard let s else { return "null" }
        return string(s).replacingOccurrences(of: "</", with: "<\\/")
            .replacingOccurrences(of: "\u{2028}", with: "\\u2028").replacingOccurrences(of: "\u{2029}", with: "\\u2029")
    }
}

let JNull = NSNull()
