import XCTest
@testable import Buzzcord

// MLS checks against the MLS working group's test vectors (../testdata/mls, from
// github.com/mlswg/mls-implementations). Hash-only checks also run the other SHA-256 suites (1
// and 3); anything that signs or encrypts runs suite 2, the one DAVE uses. Expected details are
// the Windows build's, from bin/selftest.txt.
final class MlsTests: XCTestCase {
    func testTreeMath() throws { XCTAssertEqual(try MlsChecks.treeMath(), "10 trees") }
    func testCryptoBasics() throws { try MlsChecks.cryptoBasics() }
    func testKeySchedule() throws { XCTAssertEqual(try MlsChecks.keySchedule(), "15 epochs") }
    func testTranscriptHashes() throws { try MlsChecks.transcript() }
    func testMessageEncoding() throws { XCTAssertEqual(try MlsChecks.messages(), "300 sets of 17 structs") }
    func testTreeValidation() throws { XCTAssertEqual(try MlsChecks.treeValidation(), "42 trees") }
    func testTreeOperations() throws { XCTAssertEqual(try MlsChecks.treeOperations(), "5 operations") }
    func testTreeKemUpdatePaths() throws { XCTAssertEqual(try MlsChecks.treeKem(), "328 path decryptions") }
    func testWelcome() throws { try MlsChecks.welcome() }
    func testMessageProtection() throws { try MlsChecks.messageProtection() }

    func testJoiningFromWelcome() throws {
        XCTAssertEqual(try MlsChecks.passiveClient("passive-client-welcome"), "8 passed (0 commits), 0 skipped: ")
    }

    func testFollowingCommits() throws {
        XCTAssertEqual(try MlsChecks.passiveClient("passive-client-handling-commit"),
                       "9 passed (22 commits), 4 skipped: needs a pre-shared key we don't have")
    }
}

enum MlsChecks {
    static func treeMath() throws -> String {
        var n = 0
        for v in try Vectors.load("tree-math") {
            let leaves = Int(v.long("n_leaves"))
            try check(TreeMath.nodeWidth(leaves) == v.long("n_nodes"), "node width for \(leaves)")
            try check(TreeMath.root(leaves) == v.long("root"), "root for \(leaves)")
            let root = TreeMath.root(leaves)
            let left = v.arr("left") ?? [], right = v.arr("right") ?? [], parent = v.arr("parent") ?? [], sibling = v.arr("sibling") ?? []
            for i in 0..<left.count {
                let leaf = i % 2 == 0
                try check(leaf ? int(left[i]) == nil : int(left[i]) == TreeMath.left(i), "left of \(i)")
                try check(leaf ? int(right[i]) == nil : int(right[i]) == TreeMath.right(i), "right of \(i)")
                try check(i == root ? int(parent[i]) == nil : int(parent[i]) == TreeMath.parent(i), "parent of \(i) in \(leaves)")
                try check(i == root ? int(sibling[i]) == nil : int(sibling[i]) == TreeMath.sibling(i), "sibling of \(i)")
            }
            n += 1
        }
        return "\(n) trees"
    }

    static func cryptoBasics() throws {
        let v = try Vectors.load("crypto-basics", 2)[0]
        var o = v.obj("ref_hash")!
        try sameHex(Mls.refHash(o.str("label")!, h(o, "value")), o, "out", "RefHash")
        o = v.obj("expand_with_label")!
        try sameHex(Mls.expandWithLabel(h(o, "secret"), o.str("label")!, h(o, "context"), Int(o.long("length"))), o, "out", "ExpandWithLabel")
        o = v.obj("derive_secret")!
        try sameHex(Mls.deriveSecret(h(o, "secret"), o.str("label")!), o, "out", "DeriveSecret")
        o = v.obj("derive_tree_secret")!
        try sameHex(Mls.deriveTreeSecret(h(o, "secret"), o.str("label")!, UInt32(o.long("generation")), Int(o.long("length"))), o, "out", "DeriveTreeSecret")

        o = v.obj("sign_with_label")!
        try check(try NistP256.publicFromPrivate(h(o, "priv")) == h(o, "pub"), "signature key pair")
        try check(Mls.verifyWithLabel(h(o, "pub"), o.str("label")!, h(o, "content"), h(o, "signature")), "their signature doesn't verify")
        let mine = try Mls.signWithLabel(h(o, "priv"), o.str("label")!, h(o, "content"))
        try check(Mls.verifyWithLabel(h(o, "pub"), o.str("label")!, h(o, "content"), mine), "our signature doesn't verify")

        o = v.obj("encrypt_with_label")!
        let ct = HpkeCiphertext(kemOutput: h(o, "kem_output"), ciphertext: h(o, "ciphertext"))
        try sameHex(try Mls.decryptWithLabel(h(o, "priv"), o.str("label")!, h(o, "context"), ct), o, "plaintext", "DecryptWithLabel")
        let ours = try Mls.encryptWithLabel(h(o, "pub"), o.str("label")!, h(o, "context"), h(o, "plaintext"))
        try sameHex(try Mls.decryptWithLabel(h(o, "priv"), o.str("label")!, h(o, "context"), ours), o, "plaintext", "EncryptWithLabel round trip")
    }

    static func keySchedule() throws -> String {
        var epochs = 0
        for v in try Vectors.load("key-schedule", 1, 2, 3) {
            let suite = Int(v.long("cipher_suite"))
            var initSecret = h(v, "initial_init_secret")
            var epoch: UInt64 = 0
            for e in v.objs("epochs") {
                let gc = GroupContext(groupId: h(v, "group_id"), epoch: epoch, treeHash: h(e, "tree_hash"), confirmedTranscriptHash: h(e, "confirmed_transcript_hash"))
                gc.cipherSuite = suite
                let gcBytes = gc.bytes()
                try sameHex(gcBytes, e, "group_context", "group context")
                let ks = KeySchedule.fromInit(initSecret, h(e, "commit_secret"), h(e, "psk_secret"), gcBytes)
                try sameHex(ks.joinerSecret, e, "joiner_secret", "joiner")
                try sameHex(ks.welcomeSecret, e, "welcome_secret", "welcome")
                try sameHex(ks.initSecret, e, "init_secret", "init")
                try sameHex(ks.senderDataSecret, e, "sender_data_secret", "sender data")
                try sameHex(ks.encryptionSecret, e, "encryption_secret", "encryption")
                try sameHex(ks.exporterSecret, e, "exporter_secret", "exporter")
                try sameHex(ks.epochAuthenticator, e, "epoch_authenticator", "authenticator")
                try sameHex(ks.externalSecret, e, "external_secret", "external")
                try sameHex(ks.confirmationKey, e, "confirmation_key", "confirmation key")
                try sameHex(ks.membershipKey, e, "membership_key", "membership key")
                try sameHex(ks.resumptionPsk, e, "resumption_psk", "resumption")
                let x = e.obj("exporter")!
                // The exporter label is the hex text itself, not the bytes it spells.
                try sameHex(KeySchedule.export(ks.exporterSecret, Mls.utf8(x.str("label")!), h(x, "context"), Int(x.long("length"))), x, "secret", "exported value")
                if suite == 2 {
                    let (_, pub) = try Mls.deriveKeyPair(ks.externalSecret)
                    try sameHex(pub, e, "external_pub", "external public key")
                }
                initSecret = ks.initSecret
                epoch += 1
                epochs += 1
            }
        }
        return "\(epochs) epochs"
    }

    static func transcript() throws {
        for v in try Vectors.load("transcript-hashes", 1, 2, 3) {
            let r = TlsReader(h(v, "authenticated_content"))
            let wire = try r.u16()
            let content = try FramedContent.read(r)
            let sig = try r.opaque()
            let tag = try r.opaque()
            try r.expectEnd()
            let w = TlsWriter().u16(wire)
            content.write(w)
            let confirmed = Mls.hash(Bytes.concat(h(v, "interim_transcript_hash_before"), w.opaque(sig).toArray()))
            try sameHex(confirmed, v, "confirmed_transcript_hash_after", "confirmed transcript hash")
            try check(tag == Mls.mac(h(v, "confirmation_key"), confirmed), "confirmation tag")
            try sameHex(Mls.hash(Bytes.concat(confirmed, TlsWriter().opaque(tag).toArray())), v, "interim_transcript_hash_after", "interim transcript hash")
        }
    }

    // Every struct must parse and re-serialize to the same bytes.
    static func messages() throws -> String {
        let message: (TlsReader) throws -> [UInt8] = { try MlsMessage.read($0).bytes() }
        func body(_ type: Int) -> (TlsReader) throws -> [UInt8] { { r in Mls.serialize(try MlsProposal.readBody(r, type).writeBody) } }
        let codecs: [(String, (TlsReader) throws -> [UInt8])] = [
            ("mls_welcome", message),
            ("mls_group_info", message),
            ("mls_key_package", message),
            ("public_message_application", message),
            ("public_message_proposal", message),
            ("public_message_commit", message),
            ("private_message", message),
            ("group_secrets", { Mls.serialize(try GroupSecrets.read($0).write) }),
            ("add_proposal", body(MlsProposal.add)),
            ("update_proposal", body(MlsProposal.update)),
            ("remove_proposal", body(MlsProposal.remove)),
            ("pre_shared_key_proposal", body(MlsProposal.psk)),
            ("re_init_proposal", body(MlsProposal.reInit)),
            ("external_init_proposal", body(MlsProposal.externalInit)),
            ("group_context_extensions_proposal", body(MlsProposal.groupContextExtensions)),
            ("commit", { Mls.serialize(try MlsCommit.read($0).write) }),
        ]
        var n = 0
        for v in try Vectors.load("messages") {
            for (key, codec) in codecs {
                let data = h(v, key)
                let r = TlsReader(data)
                let back: [UInt8]
                do { back = try codec(r); try r.expectEnd() } catch { throw CheckFailed(description: "\(key) #\(n): \(error)") }
                try check(back == data, "\(key) #\(n) didn't round-trip")
            }
            let tree = h(v, "ratchet_tree")
            try check(try RatchetTree.parse(tree).serialize() == tree, "ratchet_tree #\(n) didn't round-trip")
            n += 1
        }
        return "\(n) sets of \(codecs.count + 1) structs"
    }

    static func treeValidation() throws -> String {
        var n = 0
        for v in try Vectors.load("tree-validation", 1, 2, 3) {
            let tree = try RatchetTree.parse(h(v, "tree"))
            let hashes = v.arr("tree_hashes") ?? []
            for i in 0..<hashes.count { try same(tree.hashAt(i, nil), hashes[i] as! String, "tree hash of node \(i) in tree \(n)") }
            let res = v.arr("resolutions") ?? []
            for i in 0..<res.count {
                let want = (res[i] as! [Any]).compactMap(int)
                try check(want == tree.resolution(i), "resolution of node \(i) in tree \(n)")
            }
            try check(tree.verifyParentHashes(), "parent hashes of tree \(n)")
            if v.long("cipher_suite") == 2 { try check(tree.verifyLeafSignatures(h(v, "group_id")), "leaf signatures of tree \(n)") }
            n += 1
        }
        return "\(n) trees"
    }

    static func treeOperations() throws -> String {
        var n = 0
        for v in try Vectors.load("tree-operations") {
            let tree = try RatchetTree.parse(h(v, "tree_before"))
            try sameHex(tree.treeHash(), v, "tree_hash_before", "tree hash before #\(n)")
            let p = try MlsProposal.read(TlsReader(h(v, "proposal")))
            let sender = UInt32(v.long("proposal_sender"))
            if p.type == MlsProposal.add { tree.addLeaf(p.keyPackage!.leafNode) }
            else if p.type == MlsProposal.update { tree.updateLeaf(sender, p.leafNode!) }
            else if p.type == MlsProposal.remove { tree.removeLeaf(p.removed) }
            try sameHex(tree.serialize(), v, "tree_after", "tree after #\(n) (proposal type \(p.type))")
            try sameHex(tree.treeHash(), v, "tree_hash_after", "tree hash after #\(n)")
            n += 1
        }
        return "\(n) operations"
    }

    // Each other member processes each UpdatePath: parent hashes, decryption, commit secret.
    static func treeKem() throws -> String {
        var n = 0
        for v in try Vectors.load("treekem", 2) {
            let tree = try RatchetTree.parse(h(v, "ratchet_tree"))
            let groupId = h(v, "group_id")
            for u in v.objs("update_paths") {
                let sender = UInt32(u.long("sender"))
                let path = try UpdatePath.read(TlsReader(h(u, "update_path")))
                let keys = path.nodes.map { $0.encryptionKey }
                for me in v.objs("leaves_private") {
                    let index = UInt32(me.long("index"))
                    if index == sender { continue }
                    let t = tree.clone()
                    t.privateKeys[2 * Int(index)] = h(me, "encryption_priv")
                    for ps in me.objs("path_secrets") {
                        let (priv, _) = try Mls.deriveKeyPair(Mls.deriveSecret(h(ps, "path_secret"), "node"))
                        t.privateKeys[Int(ps.long("node"))] = priv
                    }
                    let leafParentHash = try t.applyPath(sender, keys)
                    try check(leafParentHash == path.leafNode.parentHash, "parent hash in path from \(sender)")
                    try check(path.leafNode.verify(groupId, sender), "path leaf signature from \(sender)")
                    t.nodes[2 * Int(sender)] = .leaf(path.leafNode)
                    try sameHex(t.treeHash(), u, "tree_hash_after", "tree hash after path from \(sender)")
                    try check(t.verifyParentHashes(), "parent hashes after path from \(sender)")
                    let gc = GroupContext(groupId: groupId, epoch: UInt64(v.long("epoch")), treeHash: h(u, "tree_hash_after"),
                                          confirmedTranscriptHash: h(v, "confirmed_transcript_hash"))
                    let g = MlsGroup(groupId: groupId, tree: t, myLeaf: index)
                    try sameHex(try g.decryptPath(sender, path, gc.bytes(), []), u, "commit_secret", "commit secret for leaf \(index) from \(sender)")
                    n += 1
                }
            }
        }
        return "\(n) path decryptions"
    }

    static func welcome() throws {
        let v = try Vectors.load("welcome", 2)[0]
        let kp = try MlsMessage.parse(h(v, "key_package")).keyPackage!
        let welcome = try MlsMessage.parse(h(v, "welcome")).welcome!
        let mine = welcome.secrets.last { $0.newMember == kp.ref() }
        try check(mine != nil, "key package reference not found in the Welcome")
        let gsBytes = try Mls.decryptWithLabel(h(v, "init_priv"), "Welcome", welcome.encryptedGroupInfo, mine!.secrets)
        try check(gsBytes != nil, "group secrets didn't decrypt")
        let gs = try GroupSecrets.read(TlsReader(gsBytes!))
        let welcomeSecret = KeySchedule.fromJoiner(gs.joinerSecret, nil, Mls.empty).welcomeSecret
        let gcm = try AesGcm(key: Mls.expandWithLabel(welcomeSecret, "key", Mls.empty, Mls.nk))
        let infoBytes = gcm.open(nonce: Mls.expandWithLabel(welcomeSecret, "nonce", Mls.empty, Mls.nn), sealed: welcome.encryptedGroupInfo, aad: nil, tagLength: 16)
        try check(infoBytes != nil, "group info didn't decrypt")
        let info = try GroupInfo.read(TlsReader(infoBytes!))
        try check(info.verify(h(v, "signer_pub")), "group info signature")
        let ks = KeySchedule.fromJoiner(gs.joinerSecret, nil, info.context.bytes())
        try check(info.confirmationTag == Mls.mac(ks.confirmationKey, info.context.confirmedTranscriptHash), "confirmation tag")
    }

    // Join from a Welcome, then follow every commit, checking the epoch authenticator each time.
    static func passiveClient(_ file: String) throws -> String {
        var passed = 0, skipped = 0, epochs = 0
        var reasons: [String] = []
        for v in try Vectors.load(file, 2) {
            do {
                let kp = try MlsMessage.parse(h(v, "key_package")).keyPackage!
                let welcome = try MlsMessage.parse(h(v, "welcome")).welcome!
                let treeHex = v.str("ratchet_tree") ?? ""
                let tree = treeHex.isEmpty ? nil : try RatchetTree.parse(Hex.decode(treeHex))
                var psks: [String: [UInt8]] = [:]
                for p in v.objs("external_psks") { psks[p.str("psk_id")!] = h(p, "psk") }
                var g = try MlsGroup.join(welcome: welcome, keyPackage: kp, initPriv: h(v, "init_priv"), leafPriv: h(v, "encryption_priv"),
                                          sigPriv: h(v, "signature_priv"), externalTree: tree, externalPsks: psks)
                try sameHex(g.epochAuthenticator, v, "initial_epoch_authenticator", "\(file) #\(passed + skipped) initial epoch")
                for e in v.objs("epochs") {
                    for p in e.arr("proposals") ?? [] { try g.handleProposal(try MlsMessage.parse(Hex.decode(p as! String))) }
                    g = try g.handleCommit(try MlsMessage.parse(h(e, "commit")))
                    try sameHex(g.epochAuthenticator, e, "epoch_authenticator", "\(file) #\(passed + skipped) epoch \(g.epoch)")
                    epochs += 1
                }
                passed += 1
            } catch MlsError.notSupported(let reason) {   // features DAVE never uses
                skipped += 1
                if !reasons.contains(reason) { reasons.append(reason) }
            }
        }
        try check(passed > 0, "no \(file) vector could run: " + reasons.joined(separator: "; "))
        return "\(passed) passed (\(epochs) commits), \(skipped) skipped: " + reasons.joined(separator: "; ")
    }

    static func messageProtection() throws {
        let v = try Vectors.load("message-protection", 2)[0]
        let gc = GroupContext(groupId: h(v, "group_id"), epoch: UInt64(v.long("epoch")), treeHash: h(v, "tree_hash"),
                              confirmedTranscriptHash: h(v, "confirmed_transcript_hash")).bytes()
        for kind in ["proposal", "commit"] {
            let pm = try MlsMessage.parse(h(v, kind + "_pub")).publicMessage!
            try check(pm.membershipTag == pm.computeMembershipTag(h(v, "membership_key"), gc), "\(kind) membership tag")
            try check(pm.verifySignature(h(v, "signature_pub"), gc), "\(kind) signature")
            let content = kind == "proposal" ? Mls.serialize(pm.content.proposal!.write) : Mls.serialize(pm.content.commit!.write)
            try sameHex(content, v, kind, "\(kind) content")
        }
    }
}
