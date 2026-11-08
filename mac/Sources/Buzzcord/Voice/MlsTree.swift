import Foundation

// Array-indexed binary tree math (RFC 9420 appendix C): leaves at even indices, parents at odd.
enum TreeMath {
    static func level(_ x: Int) -> Int {
        var k = 0
        while (x >> k) & 1 == 1 { k += 1 }
        return k
    }

    static func nodeWidth(_ leaves: Int) -> Int { leaves == 0 ? 0 : 2 * (leaves - 1) + 1 }

    private static func log2(_ x: Int) -> Int {
        var k = 0
        while (x >> (k + 1)) > 0 { k += 1 }
        return k
    }

    static func root(_ leaves: Int) -> Int { (1 << log2(nodeWidth(leaves))) - 1 }

    static func left(_ x: Int) -> Int {
        let k = level(x)
        precondition(k > 0, "leaf has no children")
        return x ^ (1 << (k - 1))
    }

    static func right(_ x: Int) -> Int {
        let k = level(x)
        precondition(k > 0, "leaf has no children")
        return x ^ (3 << (k - 1))
    }

    static func parent(_ x: Int) -> Int {
        let k = level(x)
        let b = (x >> (k + 1)) & 1
        return (x | (1 << k)) ^ (b << (k + 1))
    }

    static func sibling(_ x: Int) -> Int {
        let p = parent(x)
        return x < p ? right(p) : left(p)
    }

    static func directPath(_ x: Int, _ leaves: Int) -> [Int] {
        var path: [Int] = []
        let r = root(leaves)
        var x = x
        while x != r { x = parent(x); path.append(x) }
        return path
    }

    // Whether node x is inside the subtree rooted at c.
    static func inSubtree(_ x: Int, _ c: Int) -> Bool {
        let span = (1 << level(c)) - 1
        return x >= c - span && x <= c + span
    }

    static func commonAncestor(_ a: Int, _ b: Int, _ leaves: Int) -> Int {
        if inSubtree(b, a) { return a }
        for p in directPath(a, leaves) where inSubtree(b, p) { return p }
        return root(leaves)
    }
}

enum TreeNode {
    case leaf(LeafNode)
    case parent(ParentNode)

    var leaf: LeafNode? { if case .leaf(let n) = self { return n }; return nil }
    var parent: ParentNode? { if case .parent(let n) = self { return n }; return nil }
}

// The group's ratchet tree: public nodes, plus the HPKE private keys this member knows.
final class RatchetTree {
    var nodes: [TreeNode?] = []                     // nil = blank
    var privateKeys: [Int: [UInt8]] = [:]

    init(leaves: Int) { nodes = Array(repeating: nil, count: TreeMath.nodeWidth(leaves)) }

    var leafCount: Int { (nodes.count + 1) / 2 }
    var root: Int { TreeMath.root(leafCount) }
    func leaf(_ i: UInt32) -> LeafNode? { Int(i) < leafCount ? nodes[2 * Int(i)]?.leaf : nil }

    func clone() -> RatchetTree {
        let t = RatchetTree(leaves: 0)
        t.nodes = nodes.map { n in
            if case .parent(let p)? = n { return .parent(p.copy()) }
            return n
        }
        t.privateKeys = privateKeys
        return t
    }

    // ---- the ratchet_tree extension: optional<Node> ratchet_tree<V> ----

    static func parse(_ data: [UInt8]) throws -> RatchetTree {
        let r = TlsReader(data)
        let v = try r.vector()
        try r.expectEnd()
        var list: [TreeNode?] = []
        while !v.atEnd {
            if !(try v.optional()) { list.append(nil); continue }
            let type = try v.u8()
            if type == 1 { list.append(.leaf(try LeafNode.read(v))) }
            else if type == 2 { list.append(.parent(try ParentNode.read(v))) }
            else { throw MlsError.format("bad node type") }
        }
        if list.count % 2 == 0 || (list.last ?? nil) == nil { throw MlsError.format("ratchet tree must end with a leaf") }
        var leaves = 1
        while leaves < (list.count + 1) / 2 { leaves *= 2 }
        let t = RatchetTree(leaves: 0)
        t.nodes = list
        while t.nodes.count < TreeMath.nodeWidth(leaves) { t.nodes.append(nil) }
        for (i, n) in t.nodes.enumerated() {
            if let n, (i % 2 == 0) != (n.leaf != nil) { throw MlsError.format("node type doesn't match its position") }
        }
        return t
    }

    func serialize() -> [UInt8] {
        var last = nodes.count - 1
        while last > 0 && nodes[last] == nil { last -= 1 }
        let w = TlsWriter()
        w.vector { x in
            for i in 0...max(0, last) where i < nodes.count {
                let n = nodes[i]
                x.optional(n != nil) { y in
                    switch n! {
                    case .leaf(let l): y.u8(1); l.write(y)
                    case .parent(let p): y.u8(2); p.write(y)
                    }
                }
            }
        }
        return w.toArray()
    }

    // ---- structure ----

    func publicKey(_ x: Int) -> [UInt8]? {
        switch nodes[x] {
        case .leaf(let l)?: return l.encryptionKey
        case .parent(let p)?: return p.encryptionKey
        case nil: return nil
        }
    }

    func resolution(_ x: Int) -> [Int] {
        var list: [Int] = []
        if let n = nodes[x] {
            list.append(x)
            if let p = n.parent { for u in p.unmergedLeaves { list.append(2 * Int(u)) } }
        } else if x % 2 == 1 {
            list.append(contentsOf: resolution(TreeMath.left(x)))
            list.append(contentsOf: resolution(TreeMath.right(x)))
        }
        return list
    }

    // The child of parent p that does not contain node x.
    static func copathChild(_ p: Int, _ x: Int) -> Int { x < p ? TreeMath.right(p) : TreeMath.left(p) }

    // Direct path of node x, minus the nodes whose other child has an empty resolution.
    func filteredDirectPath(_ x: Int) -> [Int] {
        TreeMath.directPath(x, leafCount).filter { !resolution(RatchetTree.copathChild($0, x)).isEmpty }
    }

    // ---- tree hash and parent hash (RFC 9420 7.8, 7.9) ----

    func treeHash() -> [UInt8] { hashAt(root, nil) }

    func hashAt(_ x: Int, _ removed: [UInt32]?) -> [UInt8] {
        let w = TlsWriter()
        if x % 2 == 0 {
            let leaf = UInt32(x / 2)
            let n = removed?.contains(leaf) == true ? nil : nodes[x]?.leaf
            w.u8(1).u32(leaf).optional(n != nil) { n!.write($0) }
        } else {
            var p = nodes[x]?.parent
            if let orig = p, let removed, !removed.isEmpty {
                let c = orig.copy()
                c.unmergedLeaves.removeAll { removed.contains($0) }
                p = c
            }
            w.u8(2).optional(p != nil) { p!.write($0) }
            w.opaque(hashAt(TreeMath.left(x), removed)).opaque(hashAt(TreeMath.right(x), removed))
        }
        return Mls.hash(w.toArray())
    }

    // The parent hash of parent p as seen from its child on the side away from `sibling`.
    func parentHashOf(_ p: Int, _ sibling: Int) -> [UInt8] {
        let node = nodes[p]!.parent!
        let original = hashAt(sibling, node.unmergedLeaves)
        return Mls.hash(TlsWriter().opaque(node.encryptionKey).opaque(node.parentHash).opaque(original).toArray())
    }

    // Every non-blank parent must be vouched for by a parent hash in one child's resolution.
    func verifyParentHashes() -> Bool {
        for x in stride(from: 1, to: nodes.count, by: 2) {
            guard nodes[x]?.parent != nil else { continue }
            let l = TreeMath.left(x), r = TreeMath.right(x)
            if !hasParentHash(l, parentHashOf(x, r)) && !hasParentHash(r, parentHashOf(x, l)) { return false }
        }
        return true
    }

    private func hasParentHash(_ child: Int, _ target: [UInt8]) -> Bool {
        for d in resolution(child) {
            let ph: [UInt8]?
            switch nodes[d] {
            case .parent(let p)?: ph = p.parentHash
            case .leaf(let l)? where l.source == LeafNode.fromCommit: ph = l.parentHash
            default: ph = nil
            }
            if let ph, Bytes.equal(ph, target) { return true }
        }
        return false
    }

    func verifyLeafSignatures(_ groupId: [UInt8]) -> Bool {
        for i in 0..<UInt32(leafCount) {
            if let leaf = leaf(i), !leaf.verify(groupId, i) { return false }
        }
        return true
    }

    func findLeaf(_ match: LeafNode) -> Int {
        let want = match.bytes()
        for i in 0..<UInt32(leafCount) {
            if let leaf = leaf(i), Bytes.equal(leaf.bytes(), want) { return Int(i) }
        }
        return -1
    }

    // ---- changes ----

    private func blank(_ x: Int) {
        nodes[x] = nil
        privateKeys.removeValue(forKey: x)
    }

    private func blankPath(_ leaf: UInt32) {
        for p in TreeMath.directPath(2 * Int(leaf), leafCount) { blank(p) }
    }

    // Into the leftmost blank leaf, growing the tree if it's full; marks the leaf unmerged above it.
    @discardableResult func addLeaf(_ leaf: LeafNode) -> UInt32 {
        var i = 0
        while i < leafCount && nodes[2 * i] != nil { i += 1 }
        if i == leafCount {
            let width = TreeMath.nodeWidth(leafCount * 2)
            while nodes.count < width { nodes.append(nil) }
        }
        nodes[2 * i] = .leaf(leaf)
        for p in TreeMath.directPath(2 * i, leafCount) {
            nodes[p]?.parent?.unmergedLeaves.append(UInt32(i))
        }
        return UInt32(i)
    }

    func removeLeaf(_ i: UInt32) {
        blank(2 * Int(i))
        blankPath(i)
        // Drop the right half while it's empty.
        while leafCount > 1 {
            let half = leafCount / 2
            if (half..<leafCount).contains(where: { nodes[2 * $0] != nil }) { break }
            let width = TreeMath.nodeWidth(half)
            nodes.removeSubrange(width..<nodes.count)
            for k in privateKeys.keys where k >= nodes.count { privateKeys.removeValue(forKey: k) }
        }
    }

    func updateLeaf(_ i: UInt32, _ leaf: LeafNode) {
        nodes[2 * Int(i)] = .leaf(leaf)
        privateKeys.removeValue(forKey: 2 * Int(i))
        blankPath(i)
    }

    // Puts new public keys on the leaf's filtered direct path (blanking the rest of the direct
    // path), fills in parent hashes top-down, and returns the parent hash the leaf must carry.
    func applyPath(_ leaf: UInt32, _ publicKeys: [[UInt8]]) throws -> [UInt8] {
        let x = 2 * Int(leaf)
        let fdp = filteredDirectPath(x)
        if fdp.count != publicKeys.count { throw MlsError.format("update path has the wrong length") }
        blankPath(leaf)
        for i in 0..<fdp.count { nodes[fdp[i]] = .parent(ParentNode(encryptionKey: publicKeys[i])) }
        for i in stride(from: fdp.count - 2, through: 0, by: -1) {
            nodes[fdp[i]]!.parent!.parentHash = parentHashOf(fdp[i + 1], RatchetTree.copathChild(fdp[i + 1], x))
        }
        return fdp.isEmpty ? Mls.empty : parentHashOf(fdp[0], RatchetTree.copathChild(fdp[0], x))
    }
}
