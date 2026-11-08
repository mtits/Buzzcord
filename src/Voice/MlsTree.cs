using System;
using System.Collections.Generic;

namespace Buzzcord
{
    // Array-indexed binary tree math (RFC 9420 appendix C): leaves at even indices, parents at odd.
    public static class TreeMath
    {
        public static int Level(int x)
        {
            int k = 0;
            while (((x >> k) & 1) == 1) k++;
            return k;
        }

        public static int NodeWidth(int leaves) { return leaves == 0 ? 0 : 2 * (leaves - 1) + 1; }

        static int Log2(int x)
        {
            int k = 0;
            while ((x >> (k + 1)) > 0) k++;
            return k;
        }

        public static int Root(int leaves) { return (1 << Log2(NodeWidth(leaves))) - 1; }
        public static int Left(int x) { int k = Level(x); if (k == 0) throw new ArgumentException("leaf has no children"); return x ^ (1 << (k - 1)); }
        public static int Right(int x) { int k = Level(x); if (k == 0) throw new ArgumentException("leaf has no children"); return x ^ (3 << (k - 1)); }

        public static int Parent(int x)
        {
            int k = Level(x);
            int b = (x >> (k + 1)) & 1;
            return (x | (1 << k)) ^ (b << (k + 1));
        }

        public static int Sibling(int x)
        {
            int p = Parent(x);
            return x < p ? Right(p) : Left(p);
        }

        public static List<int> DirectPath(int x, int leaves)
        {
            var path = new List<int>();
            int r = Root(leaves);
            while (x != r) { x = Parent(x); path.Add(x); }
            return path;
        }

        // Whether node x is inside the subtree rooted at c.
        public static bool InSubtree(int x, int c)
        {
            int span = (1 << Level(c)) - 1;
            return x >= c - span && x <= c + span;
        }

        public static int CommonAncestor(int a, int b, int leaves)
        {
            if (InSubtree(b, a)) return a;
            foreach (var p in DirectPath(a, leaves)) if (InSubtree(b, p)) return p;
            return Root(leaves);
        }
    }

    // The group's ratchet tree: public nodes, plus the HPKE private keys this member knows.
    public sealed class RatchetTree
    {
        public readonly List<object> Nodes = new List<object>();   // LeafNode, ParentNode or null (blank)
        public readonly Dictionary<int, byte[]> Private = new Dictionary<int, byte[]>();

        public RatchetTree(int leaves) { for (int i = 0; i < TreeMath.NodeWidth(leaves); i++) Nodes.Add(null); }

        public int LeafCount { get { return (Nodes.Count + 1) / 2; } }
        public int Root { get { return TreeMath.Root(LeafCount); } }
        public LeafNode Leaf(uint i) { return i < LeafCount ? Nodes[(int)(2 * i)] as LeafNode : null; }

        public RatchetTree Clone()
        {
            var t = new RatchetTree(0);
            foreach (var n in Nodes) t.Nodes.Add(n is ParentNode ? ((ParentNode)n).Copy() : n);
            foreach (var kv in Private) t.Private[kv.Key] = kv.Value;
            return t;
        }

        // ---- the ratchet_tree extension: optional<Node> ratchet_tree<V> ----

        public static RatchetTree Parse(byte[] data)
        {
            var r = new TlsReader(data);
            var v = r.Vector();
            r.ExpectEnd();
            var list = new List<object>();
            while (!v.AtEnd)
            {
                if (!v.Optional()) { list.Add(null); continue; }
                int type = v.U8();
                if (type == 1) list.Add(LeafNode.Read(v));
                else if (type == 2) list.Add(ParentNode.Read(v));
                else throw new FormatException("bad node type");
            }
            if (list.Count % 2 == 0 || (list.Count > 0 && list[list.Count - 1] == null)) throw new FormatException("ratchet tree must end with a leaf");
            int leaves = 1;
            while (leaves < (list.Count + 1) / 2) leaves *= 2;
            var t = new RatchetTree(0);
            t.Nodes.AddRange(list);
            while (t.Nodes.Count < TreeMath.NodeWidth(leaves)) t.Nodes.Add(null);
            for (int i = 0; i < t.Nodes.Count; i++)
                if (t.Nodes[i] != null && (i % 2 == 0) != (t.Nodes[i] is LeafNode)) throw new FormatException("node type doesn't match its position");
            return t;
        }

        public byte[] Serialize()
        {
            int last = Nodes.Count - 1;
            while (last > 0 && Nodes[last] == null) last--;
            var w = new TlsWriter();
            w.Vector(x =>
            {
                for (int i = 0; i <= last; i++)
                {
                    var n = Nodes[i];
                    x.Optional(n != null, y =>
                    {
                        if (n is LeafNode) { y.U8(1); ((LeafNode)n).Write(y); }
                        else { y.U8(2); ((ParentNode)n).Write(y); }
                    });
                }
            });
            return w.ToArray();
        }

        // ---- structure ----

        public byte[] PublicKey(int x)
        {
            var n = Nodes[x];
            return n is LeafNode ? ((LeafNode)n).EncryptionKey : n is ParentNode ? ((ParentNode)n).EncryptionKey : null;
        }

        public List<int> Resolution(int x)
        {
            var list = new List<int>();
            var n = Nodes[x];
            if (n != null)
            {
                list.Add(x);
                var p = n as ParentNode;
                if (p != null) foreach (var u in p.UnmergedLeaves) list.Add((int)(2 * u));
            }
            else if (x % 2 == 1)
            {
                list.AddRange(Resolution(TreeMath.Left(x)));
                list.AddRange(Resolution(TreeMath.Right(x)));
            }
            return list;
        }

        // The child of parent p that does not contain node x.
        public static int CopathChild(int p, int x) { return x < p ? TreeMath.Right(p) : TreeMath.Left(p); }

        // Direct path of node x, minus the nodes whose other child has an empty resolution.
        public List<int> FilteredDirectPath(int x)
        {
            var list = new List<int>();
            foreach (var p in TreeMath.DirectPath(x, LeafCount))
                if (Resolution(CopathChild(p, x)).Count > 0) list.Add(p);
            return list;
        }

        // ---- tree hash and parent hash (RFC 9420 7.8, 7.9) ----

        public byte[] TreeHash() { return HashAt(Root, null); }

        public byte[] HashAt(int x, ICollection<uint> removed)
        {
            var w = new TlsWriter();
            if (x % 2 == 0)
            {
                uint leaf = (uint)(x / 2);
                var n = removed != null && removed.Contains(leaf) ? null : Nodes[x] as LeafNode;
                w.U8(1).U32(leaf).Optional(n != null, y => n.Write(y));
            }
            else
            {
                var p = Nodes[x] as ParentNode;
                if (p != null && removed != null && removed.Count > 0)
                {
                    p = p.Copy();
                    p.UnmergedLeaves.RemoveAll(removed.Contains);
                }
                w.U8(2).Optional(p != null, y => p.Write(y));
                w.Opaque(HashAt(TreeMath.Left(x), removed)).Opaque(HashAt(TreeMath.Right(x), removed));
            }
            return Mls.Hash(w.ToArray());
        }

        // The parent hash of parent p as seen from its child on the side away from `sibling`.
        public byte[] ParentHashOf(int p, int sibling)
        {
            var node = (ParentNode)Nodes[p];
            var original = HashAt(sibling, node.UnmergedLeaves);
            return Mls.Hash(new TlsWriter().Opaque(node.EncryptionKey).Opaque(node.ParentHash).Opaque(original).ToArray());
        }

        // Every non-blank parent must be vouched for by a parent hash in one child's resolution.
        public bool VerifyParentHashes()
        {
            for (int x = 1; x < Nodes.Count; x += 2)
            {
                if (!(Nodes[x] is ParentNode)) continue;
                int l = TreeMath.Left(x), r = TreeMath.Right(x);
                if (!HasParentHash(l, ParentHashOf(x, r)) && !HasParentHash(r, ParentHashOf(x, l))) return false;
            }
            return true;
        }

        bool HasParentHash(int child, byte[] target)
        {
            foreach (var d in Resolution(child))
            {
                var n = Nodes[d];
                byte[] ph = n is ParentNode ? ((ParentNode)n).ParentHash
                    : n is LeafNode && ((LeafNode)n).Source == LeafNode.FromCommit ? ((LeafNode)n).ParentHash : null;
                if (ph != null && Bytes.Equal(ph, target)) return true;
            }
            return false;
        }

        public bool VerifyLeafSignatures(byte[] groupId)
        {
            for (uint i = 0; i < LeafCount; i++)
            {
                var leaf = Leaf(i);
                if (leaf != null && !leaf.Verify(groupId, i)) return false;
            }
            return true;
        }

        public int FindLeaf(LeafNode match)
        {
            var want = match.Bytes();
            for (uint i = 0; i < LeafCount; i++)
            {
                var leaf = Leaf(i);
                if (leaf != null && Bytes.Equal(leaf.Bytes(), want)) return (int)i;
            }
            return -1;
        }

        // ---- changes ----

        void Blank(int x)
        {
            Nodes[x] = null;
            Private.Remove(x);
        }

        void BlankPath(uint leaf) { foreach (var p in TreeMath.DirectPath((int)(2 * leaf), LeafCount)) Blank(p); }

        // Into the leftmost blank leaf, growing the tree if it's full; marks the leaf unmerged above it.
        public uint AddLeaf(LeafNode leaf)
        {
            uint i = 0;
            while (i < LeafCount && Nodes[(int)(2 * i)] != null) i++;
            if (i == LeafCount)
            {
                int width = TreeMath.NodeWidth(LeafCount * 2);
                while (Nodes.Count < width) Nodes.Add(null);
            }
            Nodes[(int)(2 * i)] = leaf;
            foreach (var p in TreeMath.DirectPath((int)(2 * i), LeafCount))
            {
                var pn = Nodes[p] as ParentNode;
                if (pn != null) pn.UnmergedLeaves.Add(i);
            }
            return i;
        }

        public void RemoveLeaf(uint i)
        {
            Blank((int)(2 * i));
            BlankPath(i);
            // Drop the right half while it's empty.
            while (LeafCount > 1)
            {
                int half = LeafCount / 2;
                bool empty = true;
                for (int j = half; j < LeafCount; j++) if (Nodes[2 * j] != null) { empty = false; break; }
                if (!empty) break;
                Nodes.RemoveRange(TreeMath.NodeWidth(half), Nodes.Count - TreeMath.NodeWidth(half));
                var stale = new List<int>();
                foreach (var k in Private.Keys) if (k >= Nodes.Count) stale.Add(k);
                foreach (var k in stale) Private.Remove(k);
            }
        }

        public void UpdateLeaf(uint i, LeafNode leaf)
        {
            Nodes[(int)(2 * i)] = leaf;
            Private.Remove((int)(2 * i));
            BlankPath(i);
        }

        // Puts new public keys on the leaf's filtered direct path (blanking the rest of the direct
        // path), fills in parent hashes top-down, and returns the parent hash the leaf must carry.
        public byte[] ApplyPath(uint leaf, List<byte[]> publicKeys)
        {
            int x = (int)(2 * leaf);
            var fdp = FilteredDirectPath(x);
            if (fdp.Count != publicKeys.Count) throw new FormatException("update path has the wrong length");
            BlankPath(leaf);
            for (int i = 0; i < fdp.Count; i++) Nodes[fdp[i]] = new ParentNode { EncryptionKey = publicKeys[i] };
            for (int i = fdp.Count - 2; i >= 0; i--)
                ((ParentNode)Nodes[fdp[i]]).ParentHash = ParentHashOf(fdp[i + 1], CopathChild(fdp[i + 1], x));
            return fdp.Count == 0 ? Mls.Empty : ParentHashOf(fdp[0], CopathChild(fdp[0], x));
        }
    }
}
