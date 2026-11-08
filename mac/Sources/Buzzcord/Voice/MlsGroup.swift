import Foundation

// The secrets of one epoch (RFC 9420 section 8).
final class KeySchedule {
    var joinerSecret: [UInt8] = [], welcomeSecret: [UInt8] = [], epochSecret: [UInt8] = [], senderDataSecret: [UInt8] = []
    var encryptionSecret: [UInt8] = [], exporterSecret: [UInt8] = [], externalSecret: [UInt8] = [], confirmationKey: [UInt8] = []
    var membershipKey: [UInt8] = [], resumptionPsk: [UInt8] = [], epochAuthenticator: [UInt8] = [], initSecret: [UInt8] = []

    // Extract takes the previous init secret as salt and the commit secret as input.
    static func fromInit(_ initSecret: [UInt8], _ commitSecret: [UInt8], _ pskSecret: [UInt8]?, _ groupContext: [UInt8]) -> KeySchedule {
        let joiner = Mls.expandWithLabel(Hkdf.extract(salt: initSecret, ikm: commitSecret), "joiner", groupContext, Mls.nh)
        return fromJoiner(joiner, pskSecret, groupContext)
    }

    static func fromJoiner(_ joinerSecret: [UInt8], _ pskSecret: [UInt8]?, _ groupContext: [UInt8]) -> KeySchedule {
        let member = Hkdf.extract(salt: joinerSecret, ikm: pskSecret ?? Mls.zeros())
        let ks = fromEpochSecret(Mls.expandWithLabel(member, "epoch", groupContext, Mls.nh))
        ks.joinerSecret = joinerSecret
        ks.welcomeSecret = Mls.deriveSecret(member, "welcome")
        return ks
    }

    static func fromEpochSecret(_ epochSecret: [UInt8]) -> KeySchedule {
        let ks = KeySchedule()
        ks.epochSecret = epochSecret
        ks.senderDataSecret = Mls.deriveSecret(epochSecret, "sender data")
        ks.encryptionSecret = Mls.deriveSecret(epochSecret, "encryption")
        ks.exporterSecret = Mls.deriveSecret(epochSecret, "exporter")
        ks.externalSecret = Mls.deriveSecret(epochSecret, "external")
        ks.confirmationKey = Mls.deriveSecret(epochSecret, "confirm")
        ks.membershipKey = Mls.deriveSecret(epochSecret, "membership")
        ks.resumptionPsk = Mls.deriveSecret(epochSecret, "resumption")
        ks.epochAuthenticator = Mls.deriveSecret(epochSecret, "authentication")
        ks.initSecret = Mls.deriveSecret(epochSecret, "init")
        return ks
    }

    static func export(_ exporterSecret: [UInt8], _ label: [UInt8], _ context: [UInt8], _ length: Int) -> [UInt8] {
        Mls.expandWithLabel(Mls.deriveSecret(exporterSecret, label), "exported", Mls.hash(context), length)
    }
}

final class PendingProposal {
    var ref: [UInt8]
    var proposal: MlsProposal
    var sender: MlsSender
    init(ref: [UInt8], proposal: MlsProposal, sender: MlsSender) { self.ref = ref; self.proposal = proposal; self.sender = sender }
}

// One member's view of an MLS group in one epoch. Handshake messages are PublicMessages only
// (DAVE never uses PrivateMessage), and PSKs, ReInit and external commits aren't supported.
// Operations that move to a new epoch return a new MlsGroup and leave this one unchanged, so a
// bad commit can't corrupt the state.
final class MlsGroup {
    var groupId: [UInt8] = []
    var epoch: UInt64 = 0
    var tree = RatchetTree(leaves: 0)
    var extensions: [MlsExtension] = []
    var treeHash: [UInt8] = [], confirmedTranscriptHash: [UInt8] = [], interimTranscriptHash: [UInt8] = []
    var keys = KeySchedule()
    var myLeaf: UInt32 = 0
    var removed = false                          // a commit took us out of the group
    var externalPsks: [String: [UInt8]]?         // hex id -> key; DAVE has none, the test vectors do
    private var sigPriv: [UInt8] = []
    private(set) var proposals: [PendingProposal] = []

    init() {}

    // A bare state for checking update paths against test vectors.
    init(groupId: [UInt8], tree: RatchetTree, myLeaf: UInt32) {
        self.groupId = groupId
        self.tree = tree
        self.myLeaf = myLeaf
    }

    var epochAuthenticator: [UInt8] { keys.epochAuthenticator }

    func context() -> GroupContext {
        GroupContext(groupId: groupId, epoch: epoch, treeHash: treeHash, confirmedTranscriptHash: confirmedTranscriptHash, extensions: extensions)
    }

    func export(_ label: [UInt8], _ context: [UInt8], _ length: Int) -> [UInt8] {
        KeySchedule.export(keys.exporterSecret, label, context, length)
    }

    func export(_ label: String, _ context: [UInt8], _ length: Int) -> [UInt8] { export(Mls.utf8(label), context, length) }

    func externalSenders() throws -> [ExternalSender] {
        guard let data = MlsExtension.find(extensions, Mls.extExternalSenders) else { return [] }
        return try Mls.readList(TlsReader(data), ExternalSender.read)
    }

    // A copy to queue proposals on, leaving this state as it was.
    func fork() -> MlsGroup { clone() }

    func members() -> [(index: UInt32, leaf: LeafNode)] {
        (0..<UInt32(tree.leafCount)).compactMap { i in tree.leaf(i).map { (i, $0) } }
    }

    private func clone() -> MlsGroup {
        let g = MlsGroup()
        g.groupId = groupId
        g.epoch = epoch
        g.tree = tree.clone()
        g.extensions = extensions
        g.treeHash = treeHash
        g.confirmedTranscriptHash = confirmedTranscriptHash
        g.interimTranscriptHash = interimTranscriptHash
        g.keys = keys
        g.myLeaf = myLeaf
        g.removed = removed
        g.externalPsks = externalPsks
        g.sigPriv = sigPriv
        g.proposals = proposals
        return g
    }

    private func setEpochKeys(_ ks: KeySchedule, _ confirmationTag: [UInt8]) {
        keys = ks
        interimTranscriptHash = Mls.hash(Bytes.concat(confirmedTranscriptHash, TlsWriter().opaque(confirmationTag).toArray()))
    }

    // ---- creating and joining ----

    // A group with just us in it, at epoch 0.
    static func create(groupId: [UInt8], leaf: LeafNode, leafPriv: [UInt8], sigPriv: [UInt8], extensions: [MlsExtension]) -> MlsGroup {
        let g = MlsGroup()
        g.groupId = groupId
        g.tree = RatchetTree(leaves: 1)
        g.extensions = extensions
        g.sigPriv = sigPriv
        g.tree.nodes[0] = .leaf(leaf)
        g.tree.privateKeys[0] = leafPriv
        g.treeHash = g.tree.treeHash()
        g.confirmedTranscriptHash = Mls.empty
        let ks = KeySchedule.fromEpochSecret(Mls.random(Mls.nh))
        g.setEpochKeys(ks, Mls.mac(ks.confirmationKey, g.confirmedTranscriptHash))
        return g
    }

    static func join(welcome: MlsWelcome, keyPackage: KeyPackage, initPriv: [UInt8], leafPriv: [UInt8], sigPriv: [UInt8],
                     externalTree: RatchetTree?, externalPsks: [String: [UInt8]]? = nil) throws -> MlsGroup {
        if welcome.cipherSuite != Mls.cipherSuite { throw MlsError.notSupported("Welcome uses another MLS cipher suite") }
        let myRef = keyPackage.ref()
        guard let mine = welcome.secrets.last(where: { Bytes.equal($0.newMember, myRef) }) else {
            throw MlsError.invalid("Welcome isn't for this key package")
        }

        guard let secretsBytes = try Mls.decryptWithLabel(initPriv, "Welcome", welcome.encryptedGroupInfo, mine.secrets) else {
            throw MlsError.invalid("can't decrypt group secrets")
        }
        let secrets = try GroupSecrets.read(TlsReader(secretsBytes))
        let psk = secrets.psks.isEmpty ? nil : try PskId.secret(secrets.psks, externalPsks)

        let ks0 = KeySchedule.fromJoiner(secrets.joinerSecret, psk, Mls.empty)   // only for the welcome secret
        let gcm = try AesGcm(key: Mls.expandWithLabel(ks0.welcomeSecret, "key", Mls.empty, Mls.nk))
        guard let infoBytes = gcm.open(nonce: Mls.expandWithLabel(ks0.welcomeSecret, "nonce", Mls.empty, Mls.nn),
                                       sealed: welcome.encryptedGroupInfo, aad: nil, tagLength: 16)
        else { throw MlsError.invalid("can't decrypt group info") }
        let info = try GroupInfo.read(TlsReader(infoBytes))
        let gc = info.context
        if gc.cipherSuite != Mls.cipherSuite { throw MlsError.notSupported("group uses another MLS cipher suite") }

        let tree: RatchetTree
        if let treeData = MlsExtension.find(info.extensions, Mls.extRatchetTree) { tree = try RatchetTree.parse(treeData) }
        else if let externalTree { tree = externalTree.clone() }
        else { throw MlsError.invalid("Welcome has no ratchet tree") }
        guard let signer = tree.leaf(info.signer), info.verify(signer.signatureKey) else { throw MlsError.invalid("group info signature is invalid") }
        if !Bytes.equal(tree.treeHash(), gc.treeHash) { throw MlsError.invalid("ratchet tree doesn't match the group") }
        if !tree.verifyParentHashes() { throw MlsError.invalid("ratchet tree parent hashes are invalid") }
        if !tree.verifyLeafSignatures(gc.groupId) { throw MlsError.invalid("ratchet tree has a bad leaf signature") }
        let me = tree.findLeaf(keyPackage.leafNode)
        if me < 0 { throw MlsError.invalid("we aren't in the welcomed group") }

        let g = MlsGroup()
        g.groupId = gc.groupId
        g.epoch = gc.epoch
        g.tree = tree
        g.extensions = gc.extensions
        g.treeHash = gc.treeHash
        g.confirmedTranscriptHash = gc.confirmedTranscriptHash
        g.myLeaf = UInt32(me)
        g.sigPriv = sigPriv
        g.externalPsks = externalPsks
        tree.privateKeys[2 * me] = leafPriv
        if var ps = secrets.pathSecret {
            // The secret belongs to our lowest common ancestor with the committer; ratchet it up.
            let x = TreeMath.commonAncestor(2 * me, 2 * Int(info.signer), tree.leafCount)
            for n in [x] + tree.filteredDirectPath(x) {
                try g.implantNodeKey(n, ps)
                ps = Mls.deriveSecret(ps, "path")
            }
        }
        let ks = KeySchedule.fromJoiner(secrets.joinerSecret, psk, gc.bytes())
        if !Bytes.equal(info.confirmationTag, Mls.mac(ks.confirmationKey, g.confirmedTranscriptHash)) {
            throw MlsError.invalid("Welcome confirmation tag is invalid")
        }
        g.setEpochKeys(ks, info.confirmationTag)
        return g
    }

    private func implantNodeKey(_ x: Int, _ pathSecret: [UInt8]) throws {
        let (priv, pub) = try Mls.deriveKeyPair(Mls.deriveSecret(pathSecret, "node"))
        if !Bytes.equal(pub, tree.publicKey(x)) { throw MlsError.invalid("path secret doesn't match the tree") }
        tree.privateKeys[x] = priv
    }

    // ---- incoming handshake messages ----

    // Checks group, epoch, membership tag and signature; returns the message if it's genuine.
    func unwrap(_ m: MlsMessage) throws -> PublicMessage {
        guard m.wireFormat == MlsMessage.publicWire, let pm = m.publicMessage else {
            throw MlsError.notSupported("only PublicMessage handshakes are supported")
        }
        let c = pm.content
        if !Bytes.equal(c.groupId, groupId) { throw MlsError.invalid("message is for another group") }
        if c.epoch != epoch { throw MlsError.invalid("message is for epoch \(c.epoch), group is at \(epoch)") }
        if c.contentType == FramedContent.applicationType { throw MlsError.notSupported("application messages aren't used") }
        let gc = context().bytes()
        if c.sender.type == MlsSender.member {
            guard let leaf = tree.leaf(c.sender.index) else { throw MlsError.invalid("sender isn't a member") }
            if !Bytes.equal(pm.membershipTag, pm.computeMembershipTag(keys.membershipKey, gc)) { throw MlsError.invalid("membership tag is invalid") }
            if !pm.verifySignature(leaf.signatureKey, gc) { throw MlsError.invalid("signature is invalid") }
        } else if c.sender.type == MlsSender.external {
            let senders = try externalSenders()
            if Int(c.sender.index) >= senders.count { throw MlsError.invalid("unknown external sender") }
            if c.contentType != FramedContent.proposalType { throw MlsError.invalid("external senders may only propose") }
            if !pm.verifySignature(senders[Int(c.sender.index)].signatureKey, gc) { throw MlsError.invalid("signature is invalid") }
        } else {
            throw MlsError.notSupported("new-member messages aren't supported")
        }
        return pm
    }

    // Queues a proposal for the next commit; returns its reference.
    @discardableResult func handleProposal(_ m: MlsMessage) throws -> [UInt8] {
        let pm = try unwrap(m)
        guard pm.content.contentType == FramedContent.proposalType, let proposal = pm.content.proposal else {
            throw MlsError.invalid("not a proposal")
        }
        let r = pm.proposalRef()
        proposals.append(PendingProposal(ref: r, proposal: proposal, sender: pm.content.sender))
        return r
    }

    func revokeProposal(_ reference: [UInt8]) -> Bool {
        let before = proposals.count
        proposals.removeAll { Bytes.equal($0.ref, reference) }
        return proposals.count < before
    }

    // Applies someone else's commit; returns the next epoch's state.
    func handleCommit(_ m: MlsMessage) throws -> MlsGroup {
        let pm = try unwrap(m)
        let c = pm.content
        guard c.contentType == FramedContent.commitType, c.sender.type == MlsSender.member, let commit = c.commit else {
            throw MlsError.invalid("not a member's commit")
        }
        let committer = c.sender.index
        if committer == myLeaf { throw MlsError.invalid("our own commit must be applied from the state it produced") }

        var resolved: [PendingProposal] = []
        for por in commit.proposals {
            if let p = por.proposal { resolved.append(PendingProposal(ref: [], proposal: p, sender: c.sender)); continue }
            guard let found = proposals.last(where: { Bytes.equal($0.ref, por.reference) }) else {
                throw MlsError.invalid("commit refers to an unknown proposal")
            }
            resolved.append(found)
        }

        let next = clone()
        let (added, needsPath, psks) = try next.applyProposals(resolved, committer)
        let path = commit.path
        if needsPath && path == nil { throw MlsError.invalid("commit needs an update path") }
        if next.tree.leaf(committer) == nil { throw MlsError.invalid("committer removed itself") }

        var commitSecret = Mls.zeros()
        let removed = next.tree.leaf(myLeaf) == nil
        if let path {
            if path.leafNode.source != LeafNode.fromCommit || !path.leafNode.verify(groupId, committer) {
                throw MlsError.invalid("update path leaf is invalid")
            }
            let leafParentHash = try next.tree.applyPath(committer, path.nodes.map { $0.encryptionKey })
            if !Bytes.equal(leafParentHash, path.leafNode.parentHash) { throw MlsError.invalid("update path parent hash is invalid") }
            next.tree.nodes[2 * Int(committer)] = .leaf(path.leafNode)
            next.treeHash = next.tree.treeHash()
            if !removed {
                let provisional = next.context()
                provisional.epoch = epoch + 1
                commitSecret = try next.decryptPath(committer, path, provisional.bytes(), added)
            }
        } else {
            next.treeHash = next.tree.treeHash()
        }

        next.confirmedTranscriptHash = Mls.hash(Bytes.concat(interimTranscriptHash, pm.confirmedInput()))
        next.epoch = epoch + 1
        next.proposals.removeAll()
        if removed { next.removed = true; return next }
        let psk = psks.isEmpty ? nil : try PskId.secret(psks, externalPsks)
        let ks = KeySchedule.fromInit(keys.initSecret, commitSecret, psk, next.context().bytes())
        if !Bytes.equal(pm.confirmationTag, Mls.mac(ks.confirmationKey, next.confirmedTranscriptHash)) {
            throw MlsError.invalid("commit confirmation tag is invalid")
        }
        next.setEpochKeys(ks, pm.confirmationTag)
        return next
    }

    typealias Added = [(leaf: UInt32, keyPackage: KeyPackage)]

    // Applies proposals in RFC order (context extensions, updates, removes, adds); returns the
    // leaves added. A path is required unless everything was an Add or a PSK.
    private func applyProposals(_ list: [PendingProposal], _ committer: UInt32) throws -> (added: Added, needsPath: Bool, psks: [PskId]) {
        var needsPath = list.isEmpty
        var psks: [PskId] = []
        for p in list {
            let t = p.proposal.type
            if t == MlsProposal.reInit || t == MlsProposal.externalInit { throw MlsError.notSupported("proposal type \(t) isn't supported") }
            if t == MlsProposal.psk { if let id = try p.proposal.pskId() { psks.append(id) } }
            else if t != MlsProposal.add { needsPath = true }
        }
        for p in list where p.proposal.type == MlsProposal.groupContextExtensions { extensions = p.proposal.extensions }
        for p in list where p.proposal.type == MlsProposal.update {
            if p.sender.type != MlsSender.member || p.sender.index == committer { throw MlsError.invalid("invalid update proposal") }
            guard let leaf = p.proposal.leafNode, leaf.source == LeafNode.fromUpdate, leaf.verify(groupId, p.sender.index) else {
                throw MlsError.invalid("update leaf is invalid")
            }
            tree.updateLeaf(p.sender.index, leaf)
        }
        for p in list where p.proposal.type == MlsProposal.remove {
            if tree.leaf(p.proposal.removed) == nil { throw MlsError.invalid("removing an empty leaf") }
            tree.removeLeaf(p.proposal.removed)
        }
        var added: Added = []
        for p in list where p.proposal.type == MlsProposal.add {
            guard let kp = p.proposal.keyPackage, kp.cipherSuite == Mls.cipherSuite, kp.verify(), kp.leafNode.verify(groupId, 0) else {
                throw MlsError.invalid("added key package is invalid")
            }
            added.append((tree.addLeaf(kp.leafNode), kp))
        }
        return (added, needsPath, psks)
    }

    private static func addedNodes(_ added: Added) -> Set<Int> { Set(added.map { 2 * Int($0.leaf) }) }

    // Finds the path secret encrypted to us, derives the keys above it, returns the commit secret.
    func decryptPath(_ committer: UInt32, _ path: UpdatePath, _ provisionalContext: [UInt8], _ added: Added) throws -> [UInt8] {
        let from = 2 * Int(committer), me = 2 * Int(myLeaf)
        let fdp = tree.filteredDirectPath(from)
        let skip = MlsGroup.addedNodes(added)
        for i in 0..<fdp.count {
            let copath = RatchetTree.copathChild(fdp[i], from)
            if !TreeMath.inSubtree(me, copath) { continue }
            let res = tree.resolution(copath).filter { !skip.contains($0) }
            let cts = path.nodes[i].encryptedPathSecret
            if cts.count != res.count { throw MlsError.invalid("update path has the wrong number of ciphertexts") }
            for j in 0..<res.count {
                guard let priv = tree.privateKeys[res[j]] else { continue }
                guard var ps = try Mls.decryptWithLabel(priv, "UpdatePathNode", provisionalContext, cts[j]) else {
                    throw MlsError.invalid("can't decrypt our path secret")
                }
                for k in i..<fdp.count {
                    try implantNodeKey(fdp[k], ps)
                    ps = Mls.deriveSecret(ps, "path")
                }
                return ps
            }
            throw MlsError.invalid("no key to decrypt the update path")
        }
        throw MlsError.invalid("update path doesn't cover us")
    }

    // ---- our own commits ----

    struct CommitResult {
        var message: [UInt8]         // the MLSMessage to send
        var welcome: MlsWelcome?     // for added members, or nil
        var next: MlsGroup           // our state once the commit is accepted
    }

    // Commits every queued proposal, by reference, as DAVE does. A path is included only when
    // MLS requires one (anything other than adds). pathSecretForTest pins the random secret.
    func commitPending(pathSecretForTest: [UInt8]? = nil) throws -> CommitResult {
        let next = clone()
        let resolved = proposals
        let (added, needsPath, psks) = try next.applyProposals(resolved, myLeaf)
        if next.tree.leaf(myLeaf) == nil { throw MlsError.invalid("can't commit our own removal") }

        var commitSecret = Mls.zeros()
        var path: UpdatePath? = nil
        var secrets: [[UInt8]] = []
        var fdp: [Int] = []
        let me = 2 * Int(myLeaf)
        if needsPath {
            fdp = next.tree.filteredDirectPath(me)
            var ps = pathSecretForTest ?? Mls.random(Mls.nh)
            var pubs: [[UInt8]] = [], privs: [[UInt8]] = []
            for _ in fdp {
                secrets.append(ps)
                let (priv, pub) = try Mls.deriveKeyPair(Mls.deriveSecret(ps, "node"))
                privs.append(priv)
                pubs.append(pub)
                ps = Mls.deriveSecret(ps, "path")
            }
            commitSecret = ps
            let leafParentHash = try next.tree.applyPath(myLeaf, pubs)
            for i in 0..<fdp.count { next.tree.privateKeys[fdp[i]] = privs[i] }

            let (leafPriv, leafPub) = NistP256.generate()
            let leaf = next.tree.leaf(myLeaf)!.copy()
            leaf.encryptionKey = leafPub
            leaf.source = LeafNode.fromCommit
            leaf.parentHash = leafParentHash
            try leaf.sign(sigPriv, groupId, myLeaf)
            next.tree.nodes[me] = .leaf(leaf)
            next.tree.privateKeys[me] = leafPriv
            next.treeHash = next.tree.treeHash()

            let provisional = next.context()
            provisional.epoch = epoch + 1
            let pcBytes = provisional.bytes()
            let skip = MlsGroup.addedNodes(added)
            let p = UpdatePath(leafNode: leaf)
            for i in 0..<fdp.count {
                let node = UpdatePathNode(encryptionKey: pubs[i])
                for r in next.tree.resolution(RatchetTree.copathChild(fdp[i], me)) where !skip.contains(r) {
                    node.encryptedPathSecret.append(try Mls.encryptWithLabel(next.tree.publicKey(r)!, "UpdatePathNode", pcBytes, secrets[i]))
                }
                p.nodes.append(node)
            }
            path = p
        } else {
            next.treeHash = next.tree.treeHash()
        }

        let commit = MlsCommit()
        commit.path = path
        commit.proposals = resolved.map { ProposalOrRef(reference: $0.ref) }
        let content = FramedContent()
        content.groupId = groupId
        content.epoch = epoch
        content.sender = MlsSender(type: MlsSender.member, index: myLeaf)
        content.contentType = FramedContent.commitType
        content.commit = commit
        let pm = PublicMessage(content: content)
        let oldContext = context().bytes()
        try pm.sign(sigPriv, oldContext)

        next.confirmedTranscriptHash = Mls.hash(Bytes.concat(interimTranscriptHash, pm.confirmedInput()))
        next.epoch = epoch + 1
        next.proposals.removeAll()
        let newContext = next.context().bytes()
        let ks = KeySchedule.fromInit(keys.initSecret, commitSecret, psks.isEmpty ? nil : try PskId.secret(psks, externalPsks), newContext)
        pm.confirmationTag = Mls.mac(ks.confirmationKey, next.confirmedTranscriptHash)
        pm.membershipTag = pm.computeMembershipTag(keys.membershipKey, oldContext)
        next.setEpochKeys(ks, pm.confirmationTag)

        var welcome: MlsWelcome? = nil
        if !added.isEmpty {
            let info = GroupInfo(context: next.context())
            info.extensions = [MlsExtension(type: Mls.extRatchetTree, data: next.tree.serialize())]
            info.confirmationTag = pm.confirmationTag
            info.signer = myLeaf
            try info.sign(sigPriv)
            let w = MlsWelcome()
            let gcm = try AesGcm(key: Mls.expandWithLabel(ks.welcomeSecret, "key", Mls.empty, Mls.nk))
            w.encryptedGroupInfo = gcm.seal(nonce: Mls.expandWithLabel(ks.welcomeSecret, "nonce", Mls.empty, Mls.nn),
                                            plaintext: info.bytes(), aad: nil, tagLength: 16)
            for a in added {
                var pathSecret: [UInt8]? = nil
                for i in 0..<fdp.count where TreeMath.inSubtree(2 * Int(a.leaf), RatchetTree.copathChild(fdp[i], me)) {
                    pathSecret = secrets[i]
                    break
                }
                let gs = GroupSecrets()
                gs.joinerSecret = ks.joinerSecret
                gs.pathSecret = pathSecret
                w.secrets.append(EncryptedGroupSecrets(
                    newMember: a.keyPackage.ref(),
                    secrets: try Mls.encryptWithLabel(a.keyPackage.initKey, "Welcome", w.encryptedGroupInfo, Mls.serialize(gs.write))))
            }
            welcome = w
        }
        return CommitResult(message: MlsMessage(pm).bytes(), welcome: welcome, next: next)
    }

    // Builds the PublicMessage an external sender (the DAVE voice gateway) uses to propose.
    static func externalProposal(groupId: [UInt8], epoch: UInt64, senderIndex: UInt32, proposal: MlsProposal, sigPriv: [UInt8]) throws -> [UInt8] {
        let content = FramedContent()
        content.groupId = groupId
        content.epoch = epoch
        content.sender = MlsSender(type: MlsSender.external, index: senderIndex)
        content.contentType = FramedContent.proposalType
        content.proposal = proposal
        let pm = PublicMessage(content: content)
        try pm.sign(sigPriv, nil)
        return MlsMessage(pm).bytes()
    }

    // A fresh leaf and key package for joining; the caller keeps the private keys.
    static func makeKeyPackage(credential: MlsCredential, sigPriv: [UInt8], sigPub: [UInt8]) throws -> (keyPackage: KeyPackage, leafPriv: [UInt8], initPriv: [UInt8]) {
        let (leafPriv, leafPub) = NistP256.generate()
        let (initPriv, initPub) = NistP256.generate()
        let leaf = LeafNode(encryptionKey: leafPub, signatureKey: sigPub, credential: credential)
        try leaf.sign(sigPriv, nil, 0)
        let kp = KeyPackage(initKey: initPub, leafNode: leaf)
        try kp.sign(sigPriv)
        return (kp, leafPriv, initPriv)
    }
}
