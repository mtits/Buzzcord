import CryptoKit
import Foundation
import Security

// MLS (RFC 9420) for ciphersuite 2, MLS_128_DHKEMP256_AES128GCM_SHA256_P256, the only one DAVE
// uses. This file: the suite's crypto helpers and the wire structs. MlsTree.swift has the ratchet
// tree, MlsGroup.swift the group state machine. Generic struct names carry an Mls prefix so they
// don't collide with the rest of the app.
enum Mls {
    static let cipherSuite = 2, version = 1
    static let nh = 32, nk = 16, nn = 12
    static let extRatchetTree = 2, extExternalSenders = 5
    static let empty: [UInt8] = []

    static func hash(_ data: [UInt8]) -> [UInt8] { Array(SHA256.hash(data: data)) }
    static func mac(_ key: [UInt8], _ data: [UInt8]) -> [UInt8] {
        Array(HMAC<SHA256>.authenticationCode(for: data, using: SymmetricKey(data: key)))
    }
    static func utf8(_ s: String) -> [UInt8] { Array(s.utf8) }
    private static let prefix = utf8("MLS 1.0 ")

    static func random(_ n: Int) -> [UInt8] {
        var b = [UInt8](repeating: 0, count: n)
        let status = SecRandomCopyBytes(kSecRandomDefault, n, &b)
        precondition(status == errSecSuccess, "no system randomness")
        return b
    }

    static func refHash(_ label: String, _ value: [UInt8]) -> [UInt8] {
        hash(TlsWriter().opaque(utf8(label)).opaque(value).toArray())
    }

    static func expandWithLabel(_ secret: [UInt8], _ label: [UInt8], _ context: [UInt8], _ length: Int) -> [UInt8] {
        let info = TlsWriter().u16(length).opaque(Bytes.concat(prefix, label)).opaque(context).toArray()
        return Hkdf.expand(prk: secret, info: info, length: length)
    }

    static func expandWithLabel(_ secret: [UInt8], _ label: String, _ context: [UInt8], _ length: Int) -> [UInt8] {
        expandWithLabel(secret, utf8(label), context, length)
    }

    static func deriveSecret(_ secret: [UInt8], _ label: String) -> [UInt8] { expandWithLabel(secret, label, empty, nh) }
    static func deriveSecret(_ secret: [UInt8], _ label: [UInt8]) -> [UInt8] { expandWithLabel(secret, label, empty, nh) }

    static func deriveTreeSecret(_ secret: [UInt8], _ label: String, _ generation: UInt32, _ length: Int) -> [UInt8] {
        expandWithLabel(secret, label, TlsWriter().u32(generation).toArray(), length)
    }

    private static func labeled(_ label: String, _ content: [UInt8]) -> [UInt8] {
        TlsWriter().opaque(Bytes.concat(prefix, utf8(label))).opaque(content).toArray()
    }

    // Signatures travel DER-encoded in MLS.
    static func signWithLabel(_ priv: [UInt8], _ label: String, _ content: [UInt8]) throws -> [UInt8] {
        NistP256.toDer(try NistP256.sign(priv, labeled(label, content)))
    }

    static func verifyWithLabel(_ pub: [UInt8], _ label: String, _ content: [UInt8], _ signature: [UInt8]?) -> Bool {
        guard let signature, let rs = NistP256.fromDer(signature) else { return false }
        return NistP256.verify(pub, labeled(label, content), rs)
    }

    static func encryptWithLabel(_ pub: [UInt8], _ label: String, _ context: [UInt8], _ plaintext: [UInt8]) throws -> HpkeCiphertext {
        let (ct, enc) = try Hpke.seal(pkR: pub, info: labeled(label, context), aad: empty, plaintext: plaintext)
        return HpkeCiphertext(kemOutput: enc, ciphertext: ct)
    }

    // nil when the ciphertext doesn't open.
    static func decryptWithLabel(_ priv: [UInt8], _ label: String, _ context: [UInt8], _ ct: HpkeCiphertext) throws -> [UInt8]? {
        try Hpke.open(enc: ct.kemOutput, skR: priv, info: labeled(label, context), aad: empty, ciphertext: ct.ciphertext)
    }

    static func deriveKeyPair(_ secret: [UInt8]) throws -> (priv: [UInt8], pub: [UInt8]) { try Hpke.deriveKeyPair(secret) }

    static func zeros() -> [UInt8] { [UInt8](repeating: 0, count: nh) }

    // Opaque vectors of structs.
    static func writeList<T>(_ w: TlsWriter, _ items: [T], _ write: (TlsWriter, T) -> Void) {
        w.vector { inner in for x in items { write(inner, x) } }
    }

    static func readList<T>(_ r: TlsReader, _ read: (TlsReader) throws -> T) throws -> [T] {
        let v = try r.vector()
        var list: [T] = []
        while !v.atEnd { list.append(try read(v)) }
        return list
    }

    static func readU16List(_ r: TlsReader) throws -> [Int] { try readList(r) { try $0.u16() } }
    static func writeU16List(_ w: TlsWriter, _ l: [Int]) { writeList(w, l) { $0.u16($1) } }

    static func serialize(_ write: (TlsWriter) throws -> Void) rethrows -> [UInt8] {
        let w = TlsWriter()
        try write(w)
        return w.toArray()
    }
}

final class HpkeCiphertext {
    var kemOutput: [UInt8], ciphertext: [UInt8]
    init(kemOutput: [UInt8], ciphertext: [UInt8]) { self.kemOutput = kemOutput; self.ciphertext = ciphertext }
    func write(_ w: TlsWriter) { w.opaque(kemOutput).opaque(ciphertext) }
    static func read(_ r: TlsReader) throws -> HpkeCiphertext { HpkeCiphertext(kemOutput: try r.opaque(), ciphertext: try r.opaque()) }
}

final class MlsCredential {
    var type = 1                 // 1 basic, 2 x509
    var identity: [UInt8] = []
    var certificates: [[UInt8]] = []

    static func basic(_ identity: [UInt8]) -> MlsCredential { let c = MlsCredential(); c.identity = identity; return c }

    func write(_ w: TlsWriter) {
        w.u16(type)
        if type == 2 { Mls.writeList(w, certificates) { $0.opaque($1) } } else { w.opaque(identity) }
    }

    static func read(_ r: TlsReader) throws -> MlsCredential {
        let c = MlsCredential()
        c.type = try r.u16()
        if c.type == 1 { c.identity = try r.opaque() }
        else if c.type == 2 { c.certificates = try Mls.readList(r) { try $0.opaque() } }
        else { throw MlsError.format("unknown credential type \(c.type)") }
        return c
    }

    func bytes() -> [UInt8] { Mls.serialize(write) }
}

final class MlsCapabilities {
    var versions = [1], cipherSuites = [Mls.cipherSuite], extensions: [Int] = [], proposals: [Int] = [], credentials = [1]

    func write(_ w: TlsWriter) {
        Mls.writeU16List(w, versions)
        Mls.writeU16List(w, cipherSuites)
        Mls.writeU16List(w, extensions)
        Mls.writeU16List(w, proposals)
        Mls.writeU16List(w, credentials)
    }

    static func read(_ r: TlsReader) throws -> MlsCapabilities {
        let c = MlsCapabilities()
        c.versions = try Mls.readU16List(r)
        c.cipherSuites = try Mls.readU16List(r)
        c.extensions = try Mls.readU16List(r)
        c.proposals = try Mls.readU16List(r)
        c.credentials = try Mls.readU16List(r)
        return c
    }
}

final class MlsExtension {
    var type: Int
    var data: [UInt8]
    init(type: Int, data: [UInt8]) { self.type = type; self.data = data }
    func write(_ w: TlsWriter) { w.u16(type).opaque(data) }
    static func read(_ r: TlsReader) throws -> MlsExtension { MlsExtension(type: try r.u16(), data: try r.opaque()) }

    static func writeAll(_ w: TlsWriter, _ list: [MlsExtension]) { Mls.writeList(w, list) { e, x in x.write(e) } }
    static func readAll(_ r: TlsReader) throws -> [MlsExtension] { try Mls.readList(r, read) }

    static func find(_ list: [MlsExtension], _ type: Int) -> [UInt8]? { list.first { $0.type == type }?.data }
}

final class ExternalSender {
    var signatureKey: [UInt8]
    var credential: MlsCredential
    init(signatureKey: [UInt8], credential: MlsCredential) { self.signatureKey = signatureKey; self.credential = credential }
    func write(_ w: TlsWriter) { w.opaque(signatureKey); credential.write(w) }
    static func read(_ r: TlsReader) throws -> ExternalSender {
        ExternalSender(signatureKey: try r.opaque(), credential: try MlsCredential.read(r))
    }
    func bytes() -> [UInt8] { Mls.serialize(write) }
}

final class LeafNode {
    static let fromKeyPackage = 1, fromUpdate = 2, fromCommit = 3
    var encryptionKey: [UInt8] = [], signatureKey: [UInt8] = []
    var credential = MlsCredential()
    var capabilities = MlsCapabilities()
    var source = LeafNode.fromKeyPackage
    var notBefore: UInt64 = 0, notAfter = UInt64.max
    var parentHash: [UInt8] = []
    var extensions: [MlsExtension] = []
    var signature: [UInt8] = []

    init() {}

    init(encryptionKey: [UInt8], signatureKey: [UInt8], credential: MlsCredential) {
        self.encryptionKey = encryptionKey
        self.signatureKey = signatureKey
        self.credential = credential
    }

    private func writeBody(_ w: TlsWriter) {
        w.opaque(encryptionKey).opaque(signatureKey)
        credential.write(w)
        capabilities.write(w)
        w.u8(source)
        if source == LeafNode.fromKeyPackage { w.u64(notBefore).u64(notAfter) }
        else if source == LeafNode.fromCommit { w.opaque(parentHash) }
        MlsExtension.writeAll(w, extensions)
    }

    func write(_ w: TlsWriter) { writeBody(w); w.opaque(signature) }
    func bytes() -> [UInt8] { Mls.serialize(write) }

    static func read(_ r: TlsReader) throws -> LeafNode {
        let n = LeafNode()
        n.encryptionKey = try r.opaque()
        n.signatureKey = try r.opaque()
        n.credential = try MlsCredential.read(r)
        n.capabilities = try MlsCapabilities.read(r)
        n.source = try r.u8()
        if n.source == fromKeyPackage { n.notBefore = try r.u64(); n.notAfter = try r.u64() }
        else if n.source == fromCommit { n.parentHash = try r.opaque() }
        else if n.source != fromUpdate { throw MlsError.format("unknown leaf node source") }
        n.extensions = try MlsExtension.readAll(r)
        n.signature = try r.opaque()
        return n
    }

    private func tbs(_ groupId: [UInt8]?, _ leafIndex: UInt32) -> [UInt8] {
        let w = TlsWriter()
        writeBody(w)
        if source != LeafNode.fromKeyPackage { w.opaque(groupId).u32(leafIndex) }
        return w.toArray()
    }

    func sign(_ sigPriv: [UInt8], _ groupId: [UInt8]?, _ leafIndex: UInt32) throws {
        signature = try Mls.signWithLabel(sigPriv, "LeafNodeTBS", tbs(groupId, leafIndex))
    }

    func verify(_ groupId: [UInt8]?, _ leafIndex: UInt32) -> Bool {
        Mls.verifyWithLabel(signatureKey, "LeafNodeTBS", tbs(groupId, leafIndex), signature)
    }

    func copy() -> LeafNode {
        // Our own serialization always parses back.
        try! LeafNode.read(TlsReader(bytes()))
    }
}

final class ParentNode {
    var encryptionKey: [UInt8] = [], parentHash: [UInt8] = []
    var unmergedLeaves: [UInt32] = []

    init() {}
    init(encryptionKey: [UInt8]) { self.encryptionKey = encryptionKey }

    func write(_ w: TlsWriter) {
        w.opaque(encryptionKey).opaque(parentHash)
        Mls.writeList(w, unmergedLeaves) { $0.u32($1) }
    }

    static func read(_ r: TlsReader) throws -> ParentNode {
        let p = ParentNode()
        p.encryptionKey = try r.opaque()
        p.parentHash = try r.opaque()
        p.unmergedLeaves = try Mls.readList(r) { try $0.u32() }
        return p
    }

    func copy() -> ParentNode {
        let p = ParentNode(encryptionKey: encryptionKey)
        p.parentHash = parentHash
        p.unmergedLeaves = unmergedLeaves
        return p
    }
}

final class KeyPackage {
    var version = Mls.version, cipherSuite = Mls.cipherSuite
    var initKey: [UInt8] = []
    var leafNode = LeafNode()
    var extensions: [MlsExtension] = []
    var signature: [UInt8] = []

    init() {}
    init(initKey: [UInt8], leafNode: LeafNode) { self.initKey = initKey; self.leafNode = leafNode }

    private func writeBody(_ w: TlsWriter) {
        w.u16(version).u16(cipherSuite).opaque(initKey)
        leafNode.write(w)
        MlsExtension.writeAll(w, extensions)
    }

    func write(_ w: TlsWriter) { writeBody(w); w.opaque(signature) }
    func bytes() -> [UInt8] { Mls.serialize(write) }

    static func read(_ r: TlsReader) throws -> KeyPackage {
        let k = KeyPackage()
        k.version = try r.u16()
        k.cipherSuite = try r.u16()
        k.initKey = try r.opaque()
        k.leafNode = try LeafNode.read(r)
        k.extensions = try MlsExtension.readAll(r)
        k.signature = try r.opaque()
        return k
    }

    func sign(_ sigPriv: [UInt8]) throws { signature = try Mls.signWithLabel(sigPriv, "KeyPackageTBS", Mls.serialize(writeBody)) }
    func verify() -> Bool { Mls.verifyWithLabel(leafNode.signatureKey, "KeyPackageTBS", Mls.serialize(writeBody), signature) }
    func ref() -> [UInt8] { Mls.refHash("MLS 1.0 KeyPackage Reference", bytes()) }
}

final class MlsProposal {
    static let add = 1, update = 2, remove = 3, psk = 4, reInit = 5, externalInit = 6, groupContextExtensions = 7
    var type: Int
    var keyPackage: KeyPackage?            // add
    var leafNode: LeafNode?                // update
    var removed: UInt32 = 0                // remove
    var extensions: [MlsExtension] = []    // group context extensions
    var raw: [UInt8] = []                  // psk, reinit, external init: kept as received

    init(type: Int) { self.type = type }
    static func adding(_ kp: KeyPackage) -> MlsProposal { let p = MlsProposal(type: add); p.keyPackage = kp; return p }
    static func removing(_ leaf: UInt32) -> MlsProposal { let p = MlsProposal(type: remove); p.removed = leaf; return p }

    func write(_ w: TlsWriter) {
        w.u16(type)
        writeBody(w)
    }

    func writeBody(_ w: TlsWriter) {
        switch type {
        case MlsProposal.add: keyPackage!.write(w)
        case MlsProposal.update: leafNode!.write(w)
        case MlsProposal.remove: w.u32(removed)
        case MlsProposal.groupContextExtensions: MlsExtension.writeAll(w, extensions)
        default: w.raw(raw)
        }
    }

    static func read(_ r: TlsReader) throws -> MlsProposal { try readBody(r, try r.u16()) }

    static func readBody(_ r: TlsReader, _ type: Int) throws -> MlsProposal {
        let p = MlsProposal(type: type)
        let start = r.position
        switch type {
        case add: p.keyPackage = try KeyPackage.read(r)
        case update: p.leafNode = try LeafNode.read(r)
        case remove: p.removed = try r.u32()
        case groupContextExtensions: p.extensions = try MlsExtension.readAll(r)
        case psk: _ = try readPskId(r); p.raw = r.since(start)
        case reInit:
            _ = try r.opaque(); _ = try r.u16(); _ = try r.u16(); _ = try MlsExtension.readAll(r)
            p.raw = r.since(start)
        case externalInit: _ = try r.opaque(); p.raw = r.since(start)
        default: throw MlsError.format("unknown proposal type \(type)")
        }
        return p
    }

    // PreSharedKeyID: external(1) psk_id, or resumption(2) usage, group_id, epoch; then a nonce.
    static func readPskId(_ r: TlsReader) throws -> PskId {
        let start = r.position
        let id = PskId()
        id.type = try r.u8()
        if id.type == 1 { id.id = try r.opaque() }
        else if id.type == 2 { _ = try r.u8(); _ = try r.opaque(); _ = try r.u64() }
        else { throw MlsError.format("unknown PSK type") }
        _ = try r.opaque()
        id.raw = r.since(start)
        return id
    }

    func pskId() throws -> PskId? { type == MlsProposal.psk ? try MlsProposal.readPskId(TlsReader(raw)) : nil }
}

final class PskId {
    var type = 0             // 1 external, 2 resumption
    var id: [UInt8] = []     // external PSK id
    var raw: [UInt8] = []    // the whole PreSharedKeyID, as PSKLabel needs it

    // RFC 9420 8.4: chain the PSKs into one psk_secret. Only external PSKs can be looked up.
    static func secret(_ ids: [PskId], _ externalPsks: [String: [UInt8]]?) throws -> [UInt8] {
        var secret = Mls.zeros()
        for (i, pid) in ids.enumerated() {
            guard pid.type == 1, let psk = externalPsks?[Hex.encode(pid.id)] else {
                throw MlsError.notSupported("needs a pre-shared key we don't have")
            }
            let label = TlsWriter().raw(pid.raw).u16(i).u16(ids.count).toArray()
            let input = Mls.expandWithLabel(Hkdf.extract(salt: Mls.zeros(), ikm: psk), "derived psk", label, Mls.nh)
            secret = Hkdf.extract(salt: input, ikm: secret)
        }
        return secret
    }
}

final class ProposalOrRef {
    var proposal: MlsProposal?   // by value
    var reference: [UInt8]?      // or by reference

    init(proposal: MlsProposal) { self.proposal = proposal }
    init(reference: [UInt8]) { self.reference = reference }

    func write(_ w: TlsWriter) {
        if let proposal { w.u8(1); proposal.write(w) } else { w.u8(2).opaque(reference) }
    }

    static func read(_ r: TlsReader) throws -> ProposalOrRef {
        let t = try r.u8()
        if t == 1 { return ProposalOrRef(proposal: try MlsProposal.read(r)) }
        if t == 2 { return ProposalOrRef(reference: try r.opaque()) }
        throw MlsError.format("bad ProposalOrRef")
    }
}

final class UpdatePathNode {
    var encryptionKey: [UInt8]
    var encryptedPathSecret: [HpkeCiphertext] = []
    init(encryptionKey: [UInt8]) { self.encryptionKey = encryptionKey }
    func write(_ w: TlsWriter) { w.opaque(encryptionKey); Mls.writeList(w, encryptedPathSecret) { $1.write($0) } }
    static func read(_ r: TlsReader) throws -> UpdatePathNode {
        let n = UpdatePathNode(encryptionKey: try r.opaque())
        n.encryptedPathSecret = try Mls.readList(r, HpkeCiphertext.read)
        return n
    }
}

final class UpdatePath {
    var leafNode: LeafNode
    var nodes: [UpdatePathNode] = []
    init(leafNode: LeafNode) { self.leafNode = leafNode }
    func write(_ w: TlsWriter) { leafNode.write(w); Mls.writeList(w, nodes) { $1.write($0) } }
    static func read(_ r: TlsReader) throws -> UpdatePath {
        let p = UpdatePath(leafNode: try LeafNode.read(r))
        p.nodes = try Mls.readList(r, UpdatePathNode.read)
        return p
    }
}

final class MlsCommit {
    var proposals: [ProposalOrRef] = []
    var path: UpdatePath?
    func write(_ w: TlsWriter) {
        Mls.writeList(w, proposals) { $1.write($0) }
        w.optional(path != nil) { path!.write($0) }
    }
    static func read(_ r: TlsReader) throws -> MlsCommit {
        let c = MlsCommit()
        c.proposals = try Mls.readList(r, ProposalOrRef.read)
        if try r.optional() { c.path = try UpdatePath.read(r) }
        return c
    }
}

final class MlsSender {
    static let member = 1, external = 2, newMemberProposal = 3, newMemberCommit = 4
    var type: Int
    var index: UInt32
    init(type: Int, index: UInt32 = 0) { self.type = type; self.index = index }
    func write(_ w: TlsWriter) {
        w.u8(type)
        if type == MlsSender.member || type == MlsSender.external { w.u32(index) }
    }
    static func read(_ r: TlsReader) throws -> MlsSender {
        let s = MlsSender(type: try r.u8())
        if s.type == member || s.type == external { s.index = try r.u32() }
        else if s.type < 1 || s.type > 4 { throw MlsError.format("bad sender") }
        return s
    }
}

final class FramedContent {
    static let applicationType = 1, proposalType = 2, commitType = 3
    var groupId: [UInt8] = []
    var epoch: UInt64 = 0
    var sender = MlsSender(type: MlsSender.member)
    var authenticatedData: [UInt8] = []
    var contentType = 0
    var applicationData: [UInt8] = []
    var proposal: MlsProposal?
    var commit: MlsCommit?

    func write(_ w: TlsWriter) {
        w.opaque(groupId).u64(epoch)
        sender.write(w)
        w.opaque(authenticatedData).u8(contentType)
        if contentType == FramedContent.applicationType { w.opaque(applicationData) }
        else if contentType == FramedContent.proposalType { proposal!.write(w) }
        else if contentType == FramedContent.commitType { commit!.write(w) }
    }

    static func read(_ r: TlsReader) throws -> FramedContent {
        let c = FramedContent()
        c.groupId = try r.opaque()
        c.epoch = try r.u64()
        c.sender = try MlsSender.read(r)
        c.authenticatedData = try r.opaque()
        c.contentType = try r.u8()
        if c.contentType == applicationType { c.applicationData = try r.opaque() }
        else if c.contentType == proposalType { c.proposal = try MlsProposal.read(r) }
        else if c.contentType == commitType { c.commit = try MlsCommit.read(r) }
        else { throw MlsError.format("bad content type") }
        return c
    }
}

// A PublicMessage: content, its signature (and confirmation tag for commits), and a membership tag
// from members.
final class PublicMessage {
    var content: FramedContent
    var signature: [UInt8] = [], confirmationTag: [UInt8] = [], membershipTag: [UInt8] = []

    init(content: FramedContent) { self.content = content }

    func writeAuth(_ w: TlsWriter) {
        w.opaque(signature)
        if content.contentType == FramedContent.commitType { w.opaque(confirmationTag) }
    }

    func write(_ w: TlsWriter) {
        content.write(w)
        writeAuth(w)
        if content.sender.type == MlsSender.member { w.opaque(membershipTag) }
    }

    static func read(_ r: TlsReader) throws -> PublicMessage {
        let m = PublicMessage(content: try FramedContent.read(r))
        m.signature = try r.opaque()
        if m.content.contentType == FramedContent.commitType { m.confirmationTag = try r.opaque() }
        if m.content.sender.type == MlsSender.member { m.membershipTag = try r.opaque() }
        return m
    }

    // FramedContentTBS; the group context is included for member and new-member-commit senders.
    func tbs(_ groupContext: [UInt8]?) -> [UInt8] {
        let w = TlsWriter().u16(Mls.version).u16(MlsMessage.publicWire)
        content.write(w)
        if content.sender.type == MlsSender.member || content.sender.type == MlsSender.newMemberCommit { w.raw(groupContext ?? []) }
        return w.toArray()
    }

    func authenticatedContent() -> [UInt8] {
        let w = TlsWriter().u16(MlsMessage.publicWire)
        content.write(w)
        writeAuth(w)
        return w.toArray()
    }

    func computeMembershipTag(_ membershipKey: [UInt8], _ groupContext: [UInt8]) -> [UInt8] {
        let w = TlsWriter().raw(tbs(groupContext))
        writeAuth(w)
        return Mls.mac(membershipKey, w.toArray())
    }

    func sign(_ sigPriv: [UInt8], _ groupContext: [UInt8]?) throws {
        signature = try Mls.signWithLabel(sigPriv, "FramedContentTBS", tbs(groupContext))
    }

    func verifySignature(_ sigPub: [UInt8], _ groupContext: [UInt8]?) -> Bool {
        Mls.verifyWithLabel(sigPub, "FramedContentTBS", tbs(groupContext), signature)
    }

    func proposalRef() -> [UInt8] { Mls.refHash("MLS 1.0 Proposal Reference", authenticatedContent()) }

    // ConfirmedTranscriptHashInput for a commit.
    func confirmedInput() -> [UInt8] {
        let w = TlsWriter().u16(MlsMessage.publicWire)
        content.write(w)
        return w.opaque(signature).toArray()
    }
}

final class GroupContext {
    var version = Mls.version, cipherSuite = Mls.cipherSuite
    var groupId: [UInt8] = [], treeHash: [UInt8] = [], confirmedTranscriptHash: [UInt8] = []
    var epoch: UInt64 = 0
    var extensions: [MlsExtension] = []

    init() {}

    init(groupId: [UInt8], epoch: UInt64, treeHash: [UInt8], confirmedTranscriptHash: [UInt8], extensions: [MlsExtension] = []) {
        self.groupId = groupId
        self.epoch = epoch
        self.treeHash = treeHash
        self.confirmedTranscriptHash = confirmedTranscriptHash
        self.extensions = extensions
    }

    func write(_ w: TlsWriter) {
        w.u16(version).u16(cipherSuite).opaque(groupId).u64(epoch).opaque(treeHash).opaque(confirmedTranscriptHash)
        MlsExtension.writeAll(w, extensions)
    }

    static func read(_ r: TlsReader) throws -> GroupContext {
        let g = GroupContext()
        g.version = try r.u16()
        g.cipherSuite = try r.u16()
        g.groupId = try r.opaque()
        g.epoch = try r.u64()
        g.treeHash = try r.opaque()
        g.confirmedTranscriptHash = try r.opaque()
        g.extensions = try MlsExtension.readAll(r)
        return g
    }

    func bytes() -> [UInt8] { Mls.serialize(write) }
}

final class GroupInfo {
    var context: GroupContext
    var extensions: [MlsExtension] = []
    var confirmationTag: [UInt8] = [], signature: [UInt8] = []
    var signer: UInt32 = 0

    init(context: GroupContext) { self.context = context }

    private func writeBody(_ w: TlsWriter) {
        context.write(w)
        MlsExtension.writeAll(w, extensions)
        w.opaque(confirmationTag).u32(signer)
    }

    func write(_ w: TlsWriter) { writeBody(w); w.opaque(signature) }
    func bytes() -> [UInt8] { Mls.serialize(write) }

    static func read(_ r: TlsReader) throws -> GroupInfo {
        let g = GroupInfo(context: try GroupContext.read(r))
        g.extensions = try MlsExtension.readAll(r)
        g.confirmationTag = try r.opaque()
        g.signer = try r.u32()
        g.signature = try r.opaque()
        return g
    }

    func sign(_ sigPriv: [UInt8]) throws { signature = try Mls.signWithLabel(sigPriv, "GroupInfoTBS", Mls.serialize(writeBody)) }
    func verify(_ sigPub: [UInt8]) -> Bool { Mls.verifyWithLabel(sigPub, "GroupInfoTBS", Mls.serialize(writeBody), signature) }
}

final class GroupSecrets {
    var joinerSecret: [UInt8] = []
    var pathSecret: [UInt8]?
    var psks: [PskId] = []

    func write(_ w: TlsWriter) {
        w.opaque(joinerSecret).optional(pathSecret != nil) { $0.opaque(pathSecret) }
        Mls.writeList(w, psks) { $0.raw($1.raw) }
    }

    static func read(_ r: TlsReader) throws -> GroupSecrets {
        let g = GroupSecrets()
        g.joinerSecret = try r.opaque()
        if try r.optional() { g.pathSecret = try r.opaque() }
        g.psks = try Mls.readList(r, MlsProposal.readPskId)
        return g
    }
}

final class EncryptedGroupSecrets {
    var newMember: [UInt8]
    var secrets: HpkeCiphertext
    init(newMember: [UInt8], secrets: HpkeCiphertext) { self.newMember = newMember; self.secrets = secrets }
    func write(_ w: TlsWriter) { w.opaque(newMember); secrets.write(w) }
    static func read(_ r: TlsReader) throws -> EncryptedGroupSecrets {
        EncryptedGroupSecrets(newMember: try r.opaque(), secrets: try HpkeCiphertext.read(r))
    }
}

final class MlsWelcome {
    var cipherSuite = Mls.cipherSuite
    var secrets: [EncryptedGroupSecrets] = []
    var encryptedGroupInfo: [UInt8] = []
    func write(_ w: TlsWriter) {
        w.u16(cipherSuite)
        Mls.writeList(w, secrets) { $1.write($0) }
        w.opaque(encryptedGroupInfo)
    }
    static func read(_ r: TlsReader) throws -> MlsWelcome {
        let m = MlsWelcome()
        m.cipherSuite = try r.u16()
        m.secrets = try Mls.readList(r, EncryptedGroupSecrets.read)
        m.encryptedGroupInfo = try r.opaque()
        return m
    }
    func bytes() -> [UInt8] { Mls.serialize(write) }
}

// MLSMessage: version, wire format, then the message itself.
final class MlsMessage {
    static let publicWire = 1, privateWire = 2, welcomeWire = 3, groupInfoWire = 4, keyPackageWire = 5
    var wireFormat: Int
    var publicMessage: PublicMessage?
    var welcome: MlsWelcome?
    var groupInfo: GroupInfo?
    var keyPackage: KeyPackage?
    var privateRaw: [UInt8] = []

    init(wireFormat: Int) { self.wireFormat = wireFormat }
    init(_ pm: PublicMessage) { wireFormat = MlsMessage.publicWire; publicMessage = pm }

    func write(_ w: TlsWriter) {
        w.u16(Mls.version).u16(wireFormat)
        switch wireFormat {
        case MlsMessage.publicWire: publicMessage?.write(w)
        case MlsMessage.privateWire: w.raw(privateRaw)
        case MlsMessage.welcomeWire: welcome?.write(w)
        case MlsMessage.groupInfoWire: groupInfo?.write(w)
        case MlsMessage.keyPackageWire: keyPackage?.write(w)
        default: break
        }
    }

    func bytes() -> [UInt8] { Mls.serialize(write) }

    static func read(_ r: TlsReader) throws -> MlsMessage {
        if try r.u16() != Mls.version { throw MlsError.format("not MLS 1.0") }
        let m = MlsMessage(wireFormat: try r.u16())
        let start = r.position
        switch m.wireFormat {
        case publicWire: m.publicMessage = try PublicMessage.read(r)
        case privateWire:
            _ = try r.opaque(); _ = try r.u64(); _ = try r.u8(); _ = try r.opaque(); _ = try r.opaque(); _ = try r.opaque()
            m.privateRaw = r.since(start)
        case welcomeWire: m.welcome = try MlsWelcome.read(r)
        case groupInfoWire: m.groupInfo = try GroupInfo.read(r)
        case keyPackageWire: m.keyPackage = try KeyPackage.read(r)
        default: throw MlsError.format("unknown wire format \(m.wireFormat)")
        }
        return m
    }

    static func parse(_ data: [UInt8]) throws -> MlsMessage {
        let r = TlsReader(data)
        let m = try read(r)
        try r.expectEnd()
        return m
    }
}
