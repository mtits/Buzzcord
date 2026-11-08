import Foundation
@testable import Buzzcord

// Shared helpers for the ports of the Windows self-tests (src/SelfTest.cs and src/Voice/*Tests.cs).
// Each check returns the same detail string the Windows build prints in bin/selftest.txt, so the
// XCTest cases can compare counts exactly.

struct CheckFailed: Error, CustomStringConvertible {
    let description: String
}

func check(_ ok: Bool, _ what: @autoclosure () -> String) throws {
    if !ok { throw CheckFailed(description: what()) }
}

func same(_ actual: [UInt8]?, _ expectedHex: String, _ what: String) throws {
    let got = Hex.encode(actual)
    try check(got == expectedHex.replacingOccurrences(of: " ", with: ""), "\(what): got \(got)")
}

enum Vectors {
    // ../testdata/mls next to the mac folder, found from this file's own path.
    static let dir = URL(fileURLWithPath: #filePath)
        .deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
        .appendingPathComponent("testdata/mls")

    // The vectors in one file, optionally only those for the given suites.
    static func load(_ name: String, _ suites: Int...) throws -> [JObj] {
        let url = dir.appendingPathComponent(name + ".json")
        guard let data = try? Data(contentsOf: url), let all = Json.parse(data) as? [Any] else {
            throw CheckFailed(description: "missing test vectors: \(url.path)")
        }
        let list = all.compactMap { $0 as? JObj }.filter { suites.isEmpty || suites.contains(Int($0.long("cipher_suite"))) }
        try check(!list.isEmpty, "no vectors for the wanted suites in \(name)")
        return list
    }
}

func h(_ o: JObj, _ key: String) -> [UInt8] { Hex.decode(o.str(key) ?? "") }
func sameHex(_ actual: [UInt8]?, _ o: JObj, _ key: String, _ what: String) throws { try same(actual, o.str(key) ?? "", what) }

// A JSON array element as an Int, or nil for null.
func int(_ v: Any) -> Int? { (v as? NSNumber)?.intValue }
