import Foundation

// DAVE, Discord's end-to-end encryption for calls (protocol version 1), for one voice
// connection: the MLS group the voice gateway coordinates (opcodes 21-31) and the per-sender
// media keys that come from it. It doesn't touch the network: the voice connection feeds it
// gateway messages and frames, and sends what it raises. Mirrors Discord's libdave.
//
// Thread-safe: every entry point takes one recursive lock, and the callbacks run while it's held
// (so a callback may call back in, e.g. read privacyCode from onStateChanged).
final class DaveSession: @unchecked Sendable {
    static let maxProtocolVersion = 1
    private static let mediaKeyLabel = "Discord Secure Frames v0"
    static let silence: [UInt8] = [0xF8, 0xFF, 0xFE]

    private let lock = NSRecursiveLock()
    private let selfId: UInt64
    private let groupId: [UInt8]
    private let sigPriv: [UInt8], sigPub: [UInt8]

    private var memberIds: Set<UInt64> = []
    var now: () -> Date = { Date() }

    var onSendBinary: ((Int, [UInt8]) -> Void)?   // opcode 26 or 28, payload
    var onSendJson: ((Int, JObj) -> Void)?        // opcode 23 or 31, data
    var onLog: ((String) -> Void)?
    var onStateChanged: (() -> Void)?             // new epoch or protocol version

    private var _protocolVersion = 0

    private var externalSender: ExternalSender?
    private var selfLeaf: LeafNode?
    private var selfLeafPriv: [UInt8] = []
    private var joinKeyPackage: KeyPackage?
    private var joinInitPriv: [UInt8] = []
    private var pendingGroup: MlsGroup?, current: MlsGroup?, withProposals: MlsGroup?, outboundNext: MlsGroup?
    private var outboundCommit: [UInt8]?

    private var encryptor: FrameEncryptor?, pendingEncryptor: FrameEncryptor?
    private var decryptors: [UInt64: [FrameDecryptor]] = [:]
    private var pendingTransitionId: Int?
    private var pendingTransitionVersion = 0
    private var passthroughUntil = Date.distantFuture

    init(selfUserId: UInt64, channelId: UInt64) {
        selfId = selfUserId
        groupId = DaveSession.bigEndian(channelId)
        (sigPriv, sigPub) = NistP256.generate()
        memberIds.insert(selfUserId)
    }

    static func bigEndian(_ v: UInt64) -> [UInt8] {
        (0..<8).map { UInt8(truncatingIfNeeded: v >> UInt64(56 - 8 * $0)) }
    }

    static func fromBigEndian(_ b: [UInt8]?) -> UInt64 {
        guard let b, b.count <= 8 else { return 0 }
        return b.reduce(0) { $0 << 8 | UInt64($1) }
    }

    private static func littleEndian(_ v: UInt64) -> [UInt8] {
        (0..<8).map { UInt8(truncatingIfNeeded: v >> UInt64(8 * $0)) }
    }

    private func locked<T>(_ body: () throws -> T) rethrows -> T {
        lock.lock()
        defer { lock.unlock() }
        return try body()
    }

    private func say(_ s: String) { onLog?("dave: " + s) }

    private static func describe(_ error: Error) -> String {
        (error as? MlsError)?.description ?? "\(error)"
    }

    // Who's in the call, from the voice gateway.
    var members: Set<UInt64> {
        get { locked { memberIds } }
        set { locked { memberIds = newValue } }
    }

    var protocolVersion: Int { locked { _protocolVersion } }

    // Epoch authenticator as Discord's 30-digit "voice privacy code" (6 groups of 5 digits).
    var privacyCode: String? {
        locked {
            guard let a = current?.epochAuthenticator else { return nil }
            return (0..<6).map { g -> String in
                var v: UInt64 = 0
                for i in 0..<5 { v = v << 8 | UInt64(a[g * 5 + i]) }
                return String(format: "%05llu", v % 100000)
            }.joined(separator: " ")
        }
    }

    func setMember(_ id: UInt64, inCall: Bool) {
        locked {
            if inCall { memberIds.insert(id) } else if id != selfId { memberIds.remove(id) }
        }
    }

    var established: Bool { locked { current != nil } }
    var epoch: UInt64 { locked { current?.epoch ?? 0 } }

    // ---- gateway messages ----

    // From Session Description (op 4): the DAVE version the call will use.
    func start(protocolVersion: Int) {
        locked {
            _protocolVersion = protocolVersion
            say("call uses DAVE protocol version \(protocolVersion)")
            reinit()
        }
    }

    private func reinit() {
        pendingGroup = nil; current = nil; withProposals = nil; outboundNext = nil
        outboundCommit = nil
        joinKeyPackage = nil
        encryptor = nil; pendingEncryptor = nil
        pendingTransitionId = nil
        if _protocolVersion == 0 {
            passthroughUntil = .distantFuture
            say("transport encryption only")
            return
        }
        do {
            let (leafPriv, leafPub) = NistP256.generate()
            selfLeafPriv = leafPriv
            let leaf = LeafNode(encryptionKey: leafPub, signatureKey: sigPub, credential: MlsCredential.basic(DaveSession.bigEndian(selfId)))
            try leaf.sign(sigPriv, nil, 0)
            selfLeaf = leaf
            if externalSender != nil { createPendingGroup() }
            say("sending a new key package" + (externalSender != nil ? "" : " (no voice gateway identity yet)"))
            try sendKeyPackage()
        } catch {
            say("couldn't make a key package: " + DaveSession.describe(error))
        }
    }

    private func createPendingGroup() {
        guard let externalSender, let selfLeaf else { return }
        let ext = [MlsExtension(type: Mls.extExternalSenders, data: Mls.serialize { Mls.writeList($0, [externalSender]) { $1.write($0) } })]
        pendingGroup = MlsGroup.create(groupId: groupId, leaf: selfLeaf, leafPriv: selfLeafPriv, sigPriv: sigPriv, extensions: ext)
    }

    // Key packages are single-use: a new one every time we wait to be added.
    private func sendKeyPackage() throws {
        guard let selfLeaf else { return }
        let (initPriv, initPub) = NistP256.generate()
        joinInitPriv = initPriv
        let kp = KeyPackage(initKey: initPub, leafNode: selfLeaf)
        try kp.sign(sigPriv)
        joinKeyPackage = kp
        // A bare KeyPackage, as libdave sends it. The protocol write-up says MLSMessage, but the
        // voice gateway ignores a wrapped one: it then never asks anyone to add us.
        raise(26, kp.bytes())
    }

    private func raise(_ op: Int, _ payload: [UInt8]) { onSendBinary?(op, payload) }
    private func raise(_ op: Int, _ data: JObj) { onSendJson?(op, data) }

    // Binary opcodes 25, 27, 29, 30 (payload without the sequence number and opcode).
    func onBinary(op: Int, payload: [UInt8]) {
        locked {
            switch op {
            case 25: onExternalSender(payload)
            case 27: onProposals(payload)
            case 29: onCommit(payload)
            case 30: onWelcome(payload)
            default: break
            }
        }
    }

    private func onExternalSender(_ payload: [UInt8]) {
        do {
            let r = TlsReader(payload)
            externalSender = try ExternalSender.read(r)
            try r.expectEnd()
        } catch {
            say("bad external sender: " + DaveSession.describe(error))
            return
        }
        let makeGroup = current == nil && selfLeaf != nil && _protocolVersion > 0
        say("got the voice gateway identity" + (makeGroup ? "; made our own pending group" : ""))
        if makeGroup { createPendingGroup() }
    }

    private func onProposals(_ payload: [UInt8]) {
        guard let baseState = pendingGroup ?? current else { say("proposals before we have a group"); return }
        do {
            if withProposals == nil { withProposals = baseState.fork() }
            let g = withProposals!
            let r = TlsReader(payload)
            let revoke = try r.u8() == 1
            if revoke {
                for reference in try Mls.readList(r, { try $0.opaque() }) {
                    if !g.revokeProposal(reference) { throw MlsError.invalid("revoking an unknown proposal") }
                }
            } else {
                for m in try Mls.readList(r, MlsMessage.read) {
                    try checkProposal(m, g)
                    try g.handleProposal(m)
                }
            }
            try r.expectEnd()

            say((revoke ? "proposals revoked" : "proposals received") + ", \(g.proposals.count) queued at epoch \(g.epoch); members in call: "
                + memberIds.sorted().map(String.init).joined(separator: ","))
            let result = try g.commitPending()
            outboundCommit = result.message
            outboundNext = result.next
            let w = TlsWriter().raw(result.message)
            if let welcome = result.welcome { w.raw(welcome.bytes()) }
            say("sending our commit" + (result.welcome != nil ? " with a welcome" : ""))
            raise(28, w.toArray())
        } catch {
            say("proposals failed: " + DaveSession.describe(error))
        }
    }

    // DAVE's rules on top of MLS: only the gateway proposes, only adds and removes, and only
    // adds of people actually in the call.
    private func checkProposal(_ m: MlsMessage, _ g: MlsGroup) throws {
        guard m.wireFormat == MlsMessage.publicWire, let c = m.publicMessage?.content else {
            throw MlsError.invalid("proposal must be a PublicMessage")
        }
        if c.epoch != g.epoch { throw MlsError.invalid("proposal for epoch \(c.epoch), we're at \(g.epoch)") }
        guard c.contentType == FramedContent.proposalType, c.sender.type == MlsSender.external, let p = c.proposal else {
            throw MlsError.invalid("proposal must come from the voice gateway")
        }
        if p.type == MlsProposal.add {
            let cred = p.keyPackage?.leafNode.credential
            if cred?.type != 1 || !memberIds.contains(DaveSession.fromBigEndian(cred?.identity)) {
                throw MlsError.invalid("add proposal for someone not in the call")
            }
        } else if p.type != MlsProposal.remove {
            throw MlsError.invalid("unexpected proposal type")
        }
    }

    private func onCommit(_ payload: [UInt8]) {
        var transitionId = -1
        do {
            let r = TlsReader(payload)
            transitionId = try r.u16()
            let commitBytes = try r.raw(r.remaining)
            guard let withProposals else {
                if current == nil { say("commit while resetting, ignored"); return }
                throw MlsError.invalid("commit without queued proposals")
            }
            let ours = outboundCommit != nil && Bytes.equal(commitBytes, outboundCommit)
            say("commit announced for transition \(transitionId)" + (ours ? " (ours)" : " (someone else's)"))
            if current == nil && !ours { throw MlsError.invalid("someone else's commit before our welcome") }
            let msg = try MlsMessage.parse(commitBytes)
            guard msg.wireFormat == MlsMessage.publicWire, let content = msg.publicMessage?.content,
                  Bytes.equal(content.groupId, groupId), let commit = content.commit
            else { throw MlsError.invalid("commit for another group") }
            if commit.proposals.contains(where: { $0.reference == nil }) { throw MlsError.invalid("commit with an inline proposal") }
            let next = ours ? outboundNext! : try withProposals.handleCommit(msg)
            if next.removed { throw MlsError.invalid("the commit removed us") }
            try adopt(next, transitionId)
        } catch {
            say("commit failed: " + DaveSession.describe(error))
            recoverFromInvalid(transitionId)
        }
    }

    private func onWelcome(_ payload: [UInt8]) {
        var transitionId = -1
        do {
            let r = TlsReader(payload)
            transitionId = try r.u16()
            let welcome = try MlsWelcome.read(r)
            try r.expectEnd()
            say("welcome received for transition \(transitionId)")
            if current != nil { throw MlsError.invalid("welcome after we already have a group") }
            guard let joinKeyPackage, let externalSender else { throw MlsError.invalid("welcome before we're ready to join") }
            let g = try MlsGroup.join(welcome: welcome, keyPackage: joinKeyPackage, initPriv: joinInitPriv, leafPriv: selfLeafPriv,
                                      sigPriv: sigPriv, externalTree: nil)
            if !Bytes.equal(g.groupId, groupId) { throw MlsError.invalid("welcome to another group") }
            let senders = try g.externalSenders()
            if senders.count != 1 || !Bytes.equal(senders[0].bytes(), externalSender.bytes()) {
                throw MlsError.invalid("welcome names another voice gateway")
            }
            for m in g.members() where m.leaf.credential.type != 1 || !memberIds.contains(DaveSession.fromBigEndian(m.leaf.credential.identity)) {
                throw MlsError.invalid("welcome lists someone not in the call")
            }
            try adopt(g, transitionId)
        } catch {
            say("welcome failed: " + DaveSession.describe(error))
            recoverFromInvalid(transitionId)
        }
    }

    // A new epoch: receivers take its keys at once (keeping the old ones for 10 s), our own
    // sending switches when the gateway executes the transition.
    private func adopt(_ next: MlsGroup, _ transitionId: Int) throws {
        current = next
        pendingGroup = nil; withProposals = nil; outboundNext = nil
        outboundCommit = nil
        joinKeyPackage = nil
        let now = self.now()
        let keep = now.addingTimeInterval(10)
        var seen = Set<UInt64>()
        for m in next.members() {
            let uid = DaveSession.fromBigEndian(m.leaf.credential.identity)
            if !seen.insert(uid).inserted { throw MlsError.invalid("duplicate identity in the group") }
            let ratchet = HashRatchet(next.export(DaveSession.mediaKeyLabel, DaveSession.littleEndian(uid), 16))
            if uid == selfId { pendingEncryptor = FrameEncryptor(ratchet); continue }
            var list = decryptors[uid] ?? []
            for old in list where old.expiry > keep { old.expiry = keep }
            list.insert(FrameDecryptor(ratchet, now), at: 0)
            decryptors[uid] = list
        }
        say("epoch \(next.epoch) with \(seen.count) members; privacy code " + (privacyCode ?? "none"))
        if transitionId == 0 {
            execute(_protocolVersion)
        } else {
            pendingTransitionId = transitionId
            pendingTransitionVersion = _protocolVersion
            raise(23, ["transition_id": Int64(transitionId)])
        }
        onStateChanged?()
    }

    private func recoverFromInvalid(_ transitionId: Int) {
        if transitionId >= 0 { raise(31, ["transition_id": Int64(transitionId)]) }
        reinit()
    }

    // Op 21.
    func onPrepareTransition(transitionId: Int, protocolVersion: Int) {
        locked {
            pendingTransitionId = transitionId
            pendingTransitionVersion = protocolVersion
            say("prepare transition \(transitionId) to version \(protocolVersion)")
            if transitionId == 0 { execute(protocolVersion); return }
            if protocolVersion == 0 { passthroughUntil = .distantFuture }   // others may stop encrypting first
            raise(23, ["transition_id": Int64(transitionId)])
        }
    }

    // Op 22.
    func onExecuteTransition(transitionId: Int) {
        locked {
            if pendingTransitionId != transitionId { say("execute for unknown transition \(transitionId)"); return }
            say("executing transition \(transitionId)")
            execute(pendingTransitionVersion)
        }
    }

    private func execute(_ version: Int) {
        let before = _protocolVersion
        _protocolVersion = version
        pendingTransitionId = nil
        if version == 0 {
            encryptor = nil
            passthroughUntil = .distantFuture
        } else {
            if let p = pendingEncryptor { encryptor = p; pendingEncryptor = nil }
            // People switching on encryption may still send a few plain frames.
            passthroughUntil = before == 0 ? now().addingTimeInterval(10) : .distantPast
        }
        onStateChanged?()
    }

    // Op 24: epoch 1 means start over with a brand new group.
    func onPrepareEpoch(epoch: Int, protocolVersion: Int) {
        locked {
            say("prepare epoch \(epoch), version \(protocolVersion)")
            if epoch != 1 { return }
            _protocolVersion = protocolVersion
            reinit()
        }
    }

    // ---- media ----

    // An Opus frame from us: DAVE-encrypted once we're in a group, else sent as it is.
    func encrypt(_ frame: [UInt8]) -> [UInt8] {
        locked {
            guard _protocolVersion != 0, let encryptor else { return frame }
            return encryptor.encrypt(frame)
        }
    }

    // A frame from someone else; nil if it can't be trusted.
    func decrypt(_ frame: [UInt8], from userId: UInt64) -> [UInt8]? {
        locked {
            if frame == DaveSession.silence { return frame }
            let now = self.now()
            guard let parsed = DaveFrame.parse(frame) else {
                return _protocolVersion == 0 || now < passthroughUntil ? frame : nil
            }
            guard var list = decryptors[userId] else { return nil }
            list.removeAll { $0.expiry < now }
            decryptors[userId] = list
            for d in list {
                if let plain = d.decrypt(parsed, frame, now) { return plain }
            }
            return nil
        }
    }
}

// MLS-style sender ratchet (RFC 9420 9.1): one key per generation, derived on demand.
final class HashRatchet {
    private var nextSecret: [UInt8]
    private var nextGeneration: UInt32 = 0
    private var keys: [UInt32: [UInt8]] = [:]

    init(_ baseSecret: [UInt8]) { nextSecret = baseSecret }

    func get(_ generation: UInt32) -> [UInt8]? {
        if let key = keys[generation] { return key }
        if generation < nextGeneration { return nil }   // erased
        while nextGeneration <= generation {
            keys[nextGeneration] = Mls.deriveTreeSecret(nextSecret, "key", nextGeneration, 16)
            nextSecret = Mls.deriveTreeSecret(nextSecret, "secret", nextGeneration, Mls.nh)
            if nextGeneration == UInt32.max { break }
            nextGeneration += 1
        }
        return keys[generation]
    }

    func erase(_ generation: UInt32) { keys.removeValue(forKey: generation) }
}

// The DAVE frame trailer: 8-byte tag, ULEB128 nonce, unencrypted ranges (none for Opus),
// a size byte and the 0xFAFA marker.
struct DaveFrame {
    var ciphertextLength: Int
    var nonce: UInt32

    static func parse(_ f: [UInt8]) -> DaveFrame? {
        let n = f.count
        if n < 11 || f[n - 1] != 0xFA || f[n - 2] != 0xFA { return nil }
        let supplemental = Int(f[n - 3])
        if supplemental < 11 || supplemental > n { return nil }
        let tagAt = n - supplemental
        var pos = tagAt + 8
        let end = n - 3
        var nonce: UInt64 = 0
        var shift: UInt64 = 0
        while true {
            if pos >= end || shift > 28 { return nil }
            let b = f[pos]
            pos += 1
            nonce |= UInt64(b & 0x7F) << shift
            shift += 7
            if b & 0x80 == 0 { break }
        }
        if pos != end || nonce > UInt64(UInt32.max) { return nil }   // unencrypted ranges aren't used for audio
        return DaveFrame(ciphertextLength: tagAt, nonce: UInt32(nonce))
    }

    static func leb128(_ v: UInt32) -> [UInt8] {
        var v = v
        var out: [UInt8] = []
        while v >= 0x80 { out.append(UInt8(0x80 | (v & 0x7F))); v >>= 7 }
        out.append(UInt8(v))
        return out
    }

    static func fullNonce(_ truncated: UInt32) -> [UInt8] {
        var n = [UInt8](repeating: 0, count: 12)
        for i in 0..<4 { n[8 + i] = UInt8(truncatingIfNeeded: truncated >> UInt32(8 * i)) }   // little-endian, as libdave copies it
        return n
    }

    // Generations are the nonce's top byte; keep counting past 255 when the nonce wraps.
    static func wrappedGeneration(_ oldest: UInt32, _ generation: UInt32) -> UInt32 {
        let remainder = oldest % 256
        let factor = oldest / 256 + (generation < remainder ? 1 : 0)
        return factor &* 256 &+ generation
    }
}

final class FrameEncryptor {
    private let ratchet: HashRatchet
    private var nonce: UInt32 = 0, generation: UInt32 = 0
    private var cipher: AesGcm?

    init(_ ratchet: HashRatchet) { self.ratchet = ratchet }

    func encrypt(_ frame: [UInt8]) -> [UInt8] {
        nonce &+= 1
        let gen = DaveFrame.wrappedGeneration(generation, nonce >> 24)
        if gen != generation || cipher == nil {
            generation = gen
            // The ratchet only ever hands out 16-byte keys, and never erases ours.
            cipher = try! AesGcm(key: ratchet.get(generation)!)
        }
        let sealed = cipher!.seal(nonce: DaveFrame.fullNonce(nonce), plaintext: frame, aad: nil, tagLength: 8)
        let leb = DaveFrame.leb128(nonce)
        return TlsWriter().raw(sealed).raw(leb).u8(8 + leb.count + 3).u8(0xFA).u8(0xFA).toArray()
    }
}

// One sender's keys for one epoch, with libdave's limits on generations and replays.
final class FrameDecryptor {
    private static let maxGenerationGap: Int64 = 250, maxMissingNonces: UInt64 = 1000, maxFramesPerSecond: Int64 = 50 + 2 * 60
    private let ratchet: HashRatchet
    private let created: Date
    var expiry = Date.distantFuture
    private var ciphers: [UInt32: AesGcm] = [:]
    // Generations are never retired here: each lasts 2^24 frames (about 93 hours of audio).
    private let oldestGeneration: UInt32 = 0
    private var newestGeneration: UInt32 = 0
    private var newestNonce: UInt64?
    private var missing: [UInt64] = []

    init(_ ratchet: HashRatchet, _ now: Date) {
        self.ratchet = ratchet
        created = now
    }

    func decrypt(_ f: DaveFrame, _ frame: [UInt8], _ now: Date) -> [UInt8]? {
        let gen = DaveFrame.wrappedGeneration(oldestGeneration, f.nonce >> 24)
        if gen < oldestGeneration || Int64(gen) > Int64(newestGeneration) + FrameDecryptor.maxGenerationGap { return nil }
        let maxGenerations = (FrameDecryptor.maxFramesPerSecond * Int64(max(1, now.timeIntervalSince(created)))) >> 24
        if Int64(gen) > maxGenerations { return nil }
        let big = UInt64(gen) << 24 | UInt64(f.nonce & 0xFFFFFF)
        let expected = newestNonce.map { $0 + 1 } ?? 0
        if big < expected && !missing.contains(big) { return nil }   // a replay, or too old

        let cipher: AesGcm
        if let c = ciphers[gen] { cipher = c } else {
            guard let key = ratchet.get(gen), let c = try? AesGcm(key: key) else { return nil }
            ciphers[gen] = c
            cipher = c
        }
        guard let plain = cipher.open(nonce: DaveFrame.fullNonce(f.nonce), data: frame, offset: 0, count: f.ciphertextLength + 8,
                                      aad: nil, tagLength: 8) else { return nil }

        if big >= expected {
            let gap = min(big - expected, FrameDecryptor.maxMissingNonces)
            while !missing.isEmpty && UInt64(missing.count) + gap > FrameDecryptor.maxMissingNonces { missing.removeFirst() }
            var i = big - gap
            while i < big { missing.append(i); i += 1 }
            newestNonce = big
        } else if let at = missing.firstIndex(of: big) {
            missing.remove(at: at)
        }
        if gen > newestGeneration { newestGeneration = gen }
        return plain
    }
}
