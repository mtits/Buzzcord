import XCTest
@testable import Buzzcord

// DAVE end to end with a fake voice gateway: two people form a group, a third joins, then
// leaves; a broken commit makes a client reset; frames flow between everyone at each step.
final class DaveTests: XCTestCase {
    func testFrameFormat() throws { try DaveChecks.frame() }

    func testSimulatedCall() throws {
        let detail = try DaveChecks.call()
        print("DAVE: simulated call -- " + detail)
        let prefix = "3 members, 4 epochs, last privacy code "
        XCTAssertTrue(detail.hasPrefix(prefix), detail)
        let code = String(detail.dropFirst(prefix.count))
        XCTAssertNotNil(code.range(of: #"^\d{5}( \d{5}){5}$"#, options: .regularExpression), code)
    }

    // Voice needs about 50 frames a second per speaker; even this unoptimized debug build should
    // manage far more than that.
    func testFrameThroughput() throws {
        let enc = FrameEncryptor(HashRatchet([UInt8](repeating: 7, count: 16)))
        let dec = FrameDecryptor(HashRatchet([UInt8](repeating: 7, count: 16)), Date())
        let opus = [UInt8](repeating: 0x5A, count: 160)
        let frames = 2000
        let start = Date()
        for _ in 0..<frames {
            let f = enc.encrypt(opus)
            XCTAssertEqual(dec.decrypt(DaveFrame.parse(f)!, f, Date()), opus)
        }
        let perSecond = Double(frames) / Date().timeIntervalSince(start)
        print("DAVE: \(Int(perSecond)) frames/s sealed and opened (160-byte Opus frames)")
        XCTAssertGreaterThan(perSecond, 250)
    }
}

final class FakeDaveClient {
    let id: UInt64
    let session: DaveSession
    var binary: [(op: Int, payload: [UInt8])] = []
    var json: [(op: Int, data: JObj)] = []
    var logs: [String] = []

    init(_ id: UInt64, channel: UInt64, sharedLog: SharedLog) {
        self.id = id
        session = DaveSession(selfUserId: id, channelId: channel)
        session.onSendBinary = { [unowned self] op, p in binary.append((op, p)) }
        session.onSendJson = { [unowned self] op, d in json.append((op, d)) }
        session.onLog = { [unowned self] s in logs.append(s); sharedLog.lines.append("\(id) \(s)") }
    }

    // The last payload sent with this opcode; forgets everything sent so far.
    func take(_ op: Int) throws -> [UInt8] {
        guard let p = binary.last(where: { $0.op == op })?.payload else {
            throw CheckFailed(description: "client \(id) didn't send op \(op); its log: " + logs.joined(separator: " | "))
        }
        binary.removeAll()
        return p
    }

    func sentJson(_ op: Int, _ transition: Int) -> Bool {
        json.contains { $0.op == op && $0.data.long("transition_id") == transition }
    }
}

final class SharedLog {
    var lines: [String] = []
    var text: String { lines.joined(separator: " | ") }
}

enum DaveChecks {
    static func frame() throws {
        // Generations wrap with the nonce's top byte but keep counting.
        try check(DaveFrame.wrappedGeneration(0, 5) == 5 && DaveFrame.wrappedGeneration(250, 3) == 259 && DaveFrame.wrappedGeneration(300, 50) == 306, "generation wrap")
        try check(Hex.encode(DaveFrame.leb128(300)) == "ac02" && Hex.encode(DaveFrame.leb128(1)) == "01", "LEB128")
        let r1 = HashRatchet([UInt8](repeating: 0, count: 16))
        let r2 = HashRatchet([UInt8](repeating: 0, count: 16))
        try check(r1.get(3) == r2.get(3) && r1.get(0) != r1.get(1), "ratchet")
        let enc = FrameEncryptor(HashRatchet([UInt8](repeating: 0, count: 16)))
        let frame = enc.encrypt(Array("opus!".utf8))
        let n = frame.count
        try check(frame[n - 1] == 0xFA && frame[n - 2] == 0xFA && frame[n - 3] == 8 + 1 + 3 && n == 5 + 8 + 1 + 3, "frame layout")
        let parsed = DaveFrame.parse(frame)
        try check(parsed != nil && parsed!.nonce == 1 && parsed!.ciphertextLength == 5, "frame parse")
        let dec = FrameDecryptor(HashRatchet([UInt8](repeating: 0, count: 16)), Date())
        try check(dec.decrypt(parsed!, frame, Date()).map { String(decoding: $0, as: UTF8.self) } == "opus!", "frame decrypt")
        try check(dec.decrypt(parsed!, frame, Date()) == nil, "replayed frame accepted")
    }

    static func call() throws -> String {
        let channel: UInt64 = 1234567890123456789
        let gateway = NistP256.generate()
        let external = ExternalSender(signatureKey: gateway.pub, credential: MlsCredential.basic(Array("voice".utf8)))
        let groupId = DaveSession.bigEndian(channel)
        let log = SharedLog()

        func join(_ id: UInt64) -> FakeDaveClient {
            let c = FakeDaveClient(id, channel: channel, sharedLog: log)
            c.session.start(protocolVersion: 1)
            c.session.onBinary(op: 25, payload: external.bytes())
            return c
        }
        func members(_ cs: [FakeDaveClient], _ ids: [UInt64]) { for c in cs { c.session.members = Set(ids) } }
        // The gateway's proposals, as op 27 (append).
        func proposals(_ epoch: UInt64, _ ps: [MlsProposal]) throws -> [UInt8] {
            try TlsWriter().u8(0).vector { x in
                for p in ps { x.raw(try MlsGroup.externalProposal(groupId: groupId, epoch: epoch, senderIndex: 0, proposal: p, sigPriv: gateway.priv)) }
            }.toArray()
        }
        // Op 26 carries a bare KeyPackage (what Discord's own client sends), not an MLSMessage.
        func keyPackage(_ op26: [UInt8]) throws -> KeyPackage {
            try check(op26.count > 4 && !(op26[2] == 0 && op26[3] == 5), "key package was wrapped in an MLSMessage")
            let r = TlsReader(op26)
            let kp = try KeyPackage.read(r)
            try r.expectEnd()
            return kp
        }
        // Splits a client's op 28 into its commit and welcome.
        func split(_ op28: [UInt8]) throws -> (commit: [UInt8], welcome: [UInt8]?) {
            let r = TlsReader(op28)
            let start = r.position
            _ = try MlsMessage.read(r)
            let commit = r.since(start)
            return (commit, r.remaining > 0 ? try r.raw(r.remaining) : nil)
        }
        func withTransition(_ t: Int, _ body: [UInt8]) -> [UInt8] { TlsWriter().u16(t).raw(body).toArray() }
        func execute(_ cs: [FakeDaveClient], _ t: Int) throws {
            for c in cs { try check(c.sentJson(23, t), "client \(c.id) isn't ready for transition \(t): " + log.text) }
            for c in cs { c.session.onExecuteTransition(transitionId: t); c.json.removeAll() }
        }
        func talk(_ from: FakeDaveClient, _ to: FakeDaveClient, _ text: String) throws {
            let sent = from.session.encrypt(Array(text.utf8))
            try check(DaveFrame.parse(sent) != nil, "\(from.id) sent an unencrypted frame")
            let got = to.session.decrypt(sent, from: from.id)
            try check(got.map { String(decoding: $0, as: UTF8.self) } == text, "\(to.id) couldn't decrypt \(from.id): " + log.text)
        }

        // 1. Alice and Bob create the call. Each makes a group of one; the gateway asks each to
        // add the other, and Alice's commit wins.
        let alice = join(1001)
        let bob = join(1002)
        members([alice, bob], [1001, 1002])
        let kpAlice = try keyPackage(try alice.take(26))
        let kpBob = try keyPackage(try bob.take(26))
        alice.session.onBinary(op: 27, payload: try proposals(0, [MlsProposal.adding(kpBob)]))
        bob.session.onBinary(op: 27, payload: try proposals(0, [MlsProposal.adding(kpAlice)]))
        let aliceCommit = try split(try alice.take(28))
        _ = try bob.take(28)
        try check(aliceCommit.welcome != nil, "commit adding Bob has no welcome")
        alice.session.onBinary(op: 29, payload: withTransition(5, aliceCommit.commit))
        bob.session.onBinary(op: 30, payload: withTransition(5, aliceCommit.welcome!))
        try execute([alice, bob], 5)
        try check(alice.session.established && bob.session.established, "group not formed: " + log.text)
        try check(alice.session.privacyCode == bob.session.privacyCode && alice.session.privacyCode?.count == 35, "privacy codes differ")
        try talk(alice, bob, "hi bob")
        try talk(bob, alice, "hi alice")
        let replay = alice.session.encrypt(Array("once".utf8))
        try check(bob.session.decrypt(replay, from: 1001) != nil && bob.session.decrypt(replay, from: 1001) == nil, "replay accepted")
        var tampered = alice.session.encrypt(Array("tamper".utf8))
        tampered[2] ^= 1
        try check(bob.session.decrypt(tampered, from: 1001) == nil, "tampered frame accepted")
        try check(bob.session.decrypt(Array("plain".utf8), from: 1001) == nil, "unencrypted frame accepted in an encrypted call")

        // 2. Carol joins; both members commit, Bob's wins.
        let carol = join(1003)
        members([alice, bob, carol], [1001, 1002, 1003])
        let kpCarol = try keyPackage(try carol.take(26))
        let addCarol = try proposals(1, [MlsProposal.adding(kpCarol)])
        alice.session.onBinary(op: 27, payload: addCarol)
        bob.session.onBinary(op: 27, payload: addCarol)
        _ = try alice.take(28)
        let bobCommit = try split(try bob.take(28))
        let beforeCarol = alice.session.encrypt(Array("still epoch 1".utf8))
        alice.session.onBinary(op: 29, payload: withTransition(6, bobCommit.commit))
        bob.session.onBinary(op: 29, payload: withTransition(6, bobCommit.commit))
        carol.session.onBinary(op: 30, payload: withTransition(6, bobCommit.welcome!))
        try check(bob.session.decrypt(beforeCarol, from: 1001) != nil, "frame from just before the transition was dropped")
        try execute([alice, bob, carol], 6)
        try check(alice.session.epoch == 2 && carol.session.epoch == 2, "not at epoch 2")
        try check(alice.session.privacyCode == carol.session.privacyCode && bob.session.privacyCode == carol.session.privacyCode,
                  "privacy codes differ after Carol joined")
        try talk(carol, alice, "hey all")
        try talk(alice, carol, "welcome carol")
        try talk(bob, carol, "hello")

        // 3. Carol leaves; Alice's commit (with a path, since it removes) wins.
        members([alice, bob], [1001, 1002])
        let carolLeaf: UInt32 = 2
        let removeCarol = try proposals(2, [MlsProposal.removing(carolLeaf)])
        alice.session.onBinary(op: 27, payload: removeCarol)
        bob.session.onBinary(op: 27, payload: removeCarol)
        let removal = try split(try alice.take(28))
        _ = try bob.take(28)
        try check(removal.welcome == nil, "a removal shouldn't carry a welcome")
        try check(try MlsMessage.parse(removal.commit).publicMessage?.content.commit?.path != nil, "a removal must carry an update path")
        alice.session.onBinary(op: 29, payload: withTransition(7, removal.commit))
        bob.session.onBinary(op: 29, payload: withTransition(7, removal.commit))
        try execute([alice, bob], 7)
        try talk(alice, bob, "just us")
        try talk(bob, alice, "yep")
        // Not in the Windows test: both sides agree on the epoch and code after the removal too.
        try check(alice.session.epoch == 3 && bob.session.epoch == 3, "not at epoch 3")
        try check(alice.session.privacyCode == bob.session.privacyCode && alice.session.privacyCode != carol.session.privacyCode,
                  "privacy codes after Carol left")
        let code = alice.session.privacyCode ?? ""
        try check(carol.session.decrypt(alice.session.encrypt(Array("secret".utf8)), from: 1001) == nil, "Carol can still hear after leaving")

        // An add for someone who isn't in the call is refused (no commit comes back).
        alice.binary.removeAll()
        alice.session.onBinary(op: 27, payload: try proposals(3, [MlsProposal.adding(kpCarol)]))
        try check(alice.binary.isEmpty, "committed an add for someone not in the call")
        alice.session.start(protocolVersion: 1)   // drop the half-queued state for the rest of the test
        try check(!alice.session.established, "restart kept the old group")
        members([alice, bob], [1001, 1002, 1003])

        // 4. A broken commit: Bob reports it (op 31) and starts over with a new key package.
        var broken = removal.commit
        broken[broken.count - 5] ^= 0x55
        bob.session.onBinary(op: 27, payload: try proposals(3, [MlsProposal.adding(kpCarol)]))
        bob.binary.removeAll()
        bob.session.onBinary(op: 29, payload: withTransition(8, broken))
        try check(bob.sentJson(31, 8), "no invalid-commit report")
        _ = try bob.take(26)
        try check(!bob.session.established, "Bob kept a group after a broken commit")

        // 5. Revoking a queued proposal still produces a commit for what's left. Alice restarted,
        // so she's in her own new group (epoch 0) waiting to be added.
        _ = try alice.take(26)
        alice.binary.removeAll()
        let queuedBytes = try MlsGroup.externalProposal(groupId: groupId, epoch: 0, senderIndex: 0, proposal: MlsProposal.adding(kpCarol), sigPriv: gateway.priv)
        alice.session.onBinary(op: 27, payload: TlsWriter().u8(0).vector { $0.raw(queuedBytes) }.toArray())
        _ = try alice.take(28)
        let ref = try MlsMessage.parse(queuedBytes).publicMessage!.proposalRef()
        alice.session.onBinary(op: 27, payload: TlsWriter().u8(1).vector { $0.opaque(ref) }.toArray())
        let empty = try split(try alice.take(28))
        try check(try MlsMessage.parse(empty.commit).publicMessage?.content.commit?.proposals.isEmpty == true, "revoked proposal still committed")
        return "3 members, 4 epochs, last privacy code " + code
    }
}
