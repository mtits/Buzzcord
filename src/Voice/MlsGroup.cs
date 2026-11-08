using System;
using System.Collections.Generic;

namespace Buzzcord
{
    // The secrets of one epoch (RFC 9420 section 8).
    public sealed class KeySchedule
    {
        public byte[] JoinerSecret, WelcomeSecret, EpochSecret, SenderDataSecret, EncryptionSecret, ExporterSecret,
            ExternalSecret, ConfirmationKey, MembershipKey, ResumptionPsk, EpochAuthenticator, InitSecret;

        // Extract takes the previous init secret as salt and the commit secret as input.
        public static KeySchedule FromInit(byte[] initSecret, byte[] commitSecret, byte[] pskSecret, byte[] groupContext)
        {
            var joiner = Mls.ExpandWithLabel(Hkdf.Extract(initSecret, commitSecret), "joiner", groupContext, Mls.Nh);
            return FromJoiner(joiner, pskSecret, groupContext);
        }

        public static KeySchedule FromJoiner(byte[] joinerSecret, byte[] pskSecret, byte[] groupContext)
        {
            var member = Hkdf.Extract(joinerSecret, pskSecret ?? Mls.Zeros());
            var ks = FromEpochSecret(Mls.ExpandWithLabel(member, "epoch", groupContext, Mls.Nh));
            ks.JoinerSecret = joinerSecret;
            ks.WelcomeSecret = Mls.DeriveSecret(member, "welcome");
            return ks;
        }

        public static KeySchedule FromEpochSecret(byte[] epochSecret)
        {
            return new KeySchedule
            {
                EpochSecret = epochSecret,
                SenderDataSecret = Mls.DeriveSecret(epochSecret, "sender data"),
                EncryptionSecret = Mls.DeriveSecret(epochSecret, "encryption"),
                ExporterSecret = Mls.DeriveSecret(epochSecret, "exporter"),
                ExternalSecret = Mls.DeriveSecret(epochSecret, "external"),
                ConfirmationKey = Mls.DeriveSecret(epochSecret, "confirm"),
                MembershipKey = Mls.DeriveSecret(epochSecret, "membership"),
                ResumptionPsk = Mls.DeriveSecret(epochSecret, "resumption"),
                EpochAuthenticator = Mls.DeriveSecret(epochSecret, "authentication"),
                InitSecret = Mls.DeriveSecret(epochSecret, "init"),
            };
        }

        public static byte[] Export(byte[] exporterSecret, byte[] label, byte[] context, int length)
        {
            return Mls.ExpandWithLabel(Mls.DeriveSecret(exporterSecret, label), "exported", Mls.Hash(context), length);
        }
    }

    public sealed class PendingProposal
    {
        public byte[] Ref;
        public Proposal Proposal;
        public Sender Sender;
    }

    // One member's view of an MLS group in one epoch. Handshake messages are PublicMessages only
    // (DAVE never uses PrivateMessage), and PSKs, ReInit and external commits aren't supported.
    // Operations that move to a new epoch return a new MlsGroup and leave this one unchanged, so a
    // bad commit can't corrupt the state.
    public sealed class MlsGroup
    {
        public byte[] GroupId;
        public ulong Epoch;
        public RatchetTree Tree;
        public List<Extension> Extensions = new List<Extension>();
        public byte[] TreeHash, ConfirmedTranscriptHash, InterimTranscriptHash;
        public KeySchedule Keys;
        public uint MyLeaf;
        public bool Removed;           // a commit took us out of the group
        public IDictionary<string, byte[]> ExternalPsks;   // hex id -> key; DAVE has none, the test vectors do
        byte[] sigPriv;
        List<PendingProposal> proposals = new List<PendingProposal>();

        public byte[] EpochAuthenticator { get { return Keys.EpochAuthenticator; } }
        public IList<PendingProposal> Proposals { get { return proposals; } }

        public GroupContext Context()
        {
            return new GroupContext { GroupId = GroupId, Epoch = Epoch, TreeHash = TreeHash, ConfirmedTranscriptHash = ConfirmedTranscriptHash, Extensions = Extensions };
        }

        public byte[] Export(byte[] label, byte[] context, int length) { return KeySchedule.Export(Keys.ExporterSecret, label, context, length); }
        public byte[] Export(string label, byte[] context, int length) { return Export(Mls.Utf8(label), context, length); }

        public List<ExternalSender> ExternalSenders()
        {
            var data = Extension.Find(Extensions, Mls.ExtExternalSenders);
            return data == null ? new List<ExternalSender>() : Mls.ReadList(new TlsReader(data), ExternalSender.Read);
        }

        // A copy to queue proposals on, leaving this state as it was.
        public MlsGroup Fork() { return Clone(); }

        public IEnumerable<KeyValuePair<uint, LeafNode>> Members()
        {
            for (uint i = 0; i < Tree.LeafCount; i++)
            {
                var leaf = Tree.Leaf(i);
                if (leaf != null) yield return new KeyValuePair<uint, LeafNode>(i, leaf);
            }
        }

        MlsGroup Clone()
        {
            var g = (MlsGroup)MemberwiseClone();
            g.Tree = Tree.Clone();
            g.Extensions = new List<Extension>(Extensions);
            g.proposals = new List<PendingProposal>(proposals);
            return g;
        }

        void SetEpochKeys(KeySchedule ks, byte[] confirmationTag)
        {
            Keys = ks;
            InterimTranscriptHash = Mls.Hash(Bytes.Concat(ConfirmedTranscriptHash, new TlsWriter().Opaque(confirmationTag).ToArray()));
        }

        // ---- creating and joining ----

        // A group with just us in it, at epoch 0.
        public static MlsGroup Create(byte[] groupId, LeafNode leaf, byte[] leafPriv, byte[] sigPriv, List<Extension> extensions)
        {
            var g = new MlsGroup { GroupId = groupId, Tree = new RatchetTree(1), Extensions = extensions ?? new List<Extension>(), sigPriv = sigPriv };
            g.Tree.Nodes[0] = leaf;
            g.Tree.Private[0] = leafPriv;
            g.TreeHash = g.Tree.TreeHash();
            g.ConfirmedTranscriptHash = Mls.Empty;
            var ks = KeySchedule.FromEpochSecret(Mls.Random(Mls.Nh));
            g.SetEpochKeys(ks, Mls.Mac(ks.ConfirmationKey, g.ConfirmedTranscriptHash));
            return g;
        }

        public static MlsGroup Join(Welcome welcome, KeyPackage keyPackage, byte[] initPriv, byte[] leafPriv, byte[] sigPriv, RatchetTree externalTree,
            IDictionary<string, byte[]> externalPsks = null)
        {
            if (welcome.CipherSuite != Mls.CipherSuite) throw new NotSupportedException("Welcome uses another MLS cipher suite");
            var myRef = keyPackage.Ref();
            EncryptedGroupSecrets mine = null;
            foreach (var s in welcome.Secrets) if (Bytes.Equal(s.NewMember, myRef)) mine = s;
            if (mine == null) throw new InvalidOperationException("Welcome isn't for this key package");

            var secretsBytes = Mls.DecryptWithLabel(initPriv, "Welcome", welcome.EncryptedGroupInfo, mine.Secrets);
            if (secretsBytes == null) throw new InvalidOperationException("can't decrypt group secrets");
            var secrets = GroupSecrets.Read(new TlsReader(secretsBytes));
            var psk = secrets.Psks.Count > 0 ? PskId.Secret(secrets.Psks, externalPsks) : null;

            var ks0 = KeySchedule.FromJoiner(secrets.JoinerSecret, psk, Mls.Empty); // only for the welcome secret
            byte[] infoBytes;
            using (var gcm = new AesGcm(Mls.ExpandWithLabel(ks0.WelcomeSecret, "key", Mls.Empty, Mls.Nk)))
                infoBytes = gcm.Open(Mls.ExpandWithLabel(ks0.WelcomeSecret, "nonce", Mls.Empty, Mls.Nn), welcome.EncryptedGroupInfo, null, 16);
            if (infoBytes == null) throw new InvalidOperationException("can't decrypt group info");
            var info = GroupInfo.Read(new TlsReader(infoBytes));
            var gc = info.Context;
            if (gc.CipherSuite != Mls.CipherSuite) throw new NotSupportedException("group uses another MLS cipher suite");

            var treeData = Extension.Find(info.Extensions, Mls.ExtRatchetTree);
            var tree = treeData != null ? RatchetTree.Parse(treeData) : externalTree != null ? externalTree.Clone() : null;
            if (tree == null) throw new InvalidOperationException("Welcome has no ratchet tree");
            var signer = tree.Leaf(info.Signer);
            if (signer == null || !info.Verify(signer.SignatureKey)) throw new InvalidOperationException("group info signature is invalid");
            if (!Bytes.Equal(tree.TreeHash(), gc.TreeHash)) throw new InvalidOperationException("ratchet tree doesn't match the group");
            if (!tree.VerifyParentHashes()) throw new InvalidOperationException("ratchet tree parent hashes are invalid");
            if (!tree.VerifyLeafSignatures(gc.GroupId)) throw new InvalidOperationException("ratchet tree has a bad leaf signature");
            int me = tree.FindLeaf(keyPackage.LeafNode);
            if (me < 0) throw new InvalidOperationException("we aren't in the welcomed group");

            var g = new MlsGroup
            {
                GroupId = gc.GroupId, Epoch = gc.Epoch, Tree = tree, Extensions = gc.Extensions, TreeHash = gc.TreeHash,
                ConfirmedTranscriptHash = gc.ConfirmedTranscriptHash, MyLeaf = (uint)me, sigPriv = sigPriv, ExternalPsks = externalPsks
            };
            tree.Private[2 * me] = leafPriv;
            if (secrets.PathSecret != null)
            {
                // The secret belongs to our lowest common ancestor with the committer; ratchet it up.
                int x = TreeMath.CommonAncestor(2 * me, (int)(2 * info.Signer), tree.LeafCount);
                var ps = secrets.PathSecret;
                var chain = new List<int> { x };
                chain.AddRange(tree.FilteredDirectPath(x));
                foreach (var n in chain)
                {
                    g.ImplantNodeKey(n, ps);
                    ps = Mls.DeriveSecret(ps, "path");
                }
            }
            var ks = KeySchedule.FromJoiner(secrets.JoinerSecret, psk, gc.Bytes());
            if (!Bytes.Equal(info.ConfirmationTag, Mls.Mac(ks.ConfirmationKey, g.ConfirmedTranscriptHash)))
                throw new InvalidOperationException("Welcome confirmation tag is invalid");
            g.SetEpochKeys(ks, info.ConfirmationTag);
            return g;
        }

        void ImplantNodeKey(int x, byte[] pathSecret)
        {
            byte[] priv, pub;
            Mls.DeriveKeyPair(Mls.DeriveSecret(pathSecret, "node"), out priv, out pub);
            if (!Bytes.Equal(pub, Tree.PublicKey(x))) throw new InvalidOperationException("path secret doesn't match the tree");
            Tree.Private[x] = priv;
        }

        // ---- incoming handshake messages ----

        // Checks group, epoch, membership tag and signature; returns the message if it's genuine.
        public PublicMessage Unwrap(MlsMessage m)
        {
            if (m.WireFormat != MlsMessage.PublicWire) throw new NotSupportedException("only PublicMessage handshakes are supported");
            var pm = m.Public;
            var c = pm.Content;
            if (!Bytes.Equal(c.GroupId, GroupId)) throw new InvalidOperationException("message is for another group");
            if (c.Epoch != Epoch) throw new InvalidOperationException("message is for epoch " + c.Epoch + ", group is at " + Epoch);
            if (c.ContentType == FramedContent.Application) throw new NotSupportedException("application messages aren't used");
            var gc = Context().Bytes();
            if (c.Sender.Type == Sender.Member)
            {
                var leaf = Tree.Leaf(c.Sender.Index);
                if (leaf == null) throw new InvalidOperationException("sender isn't a member");
                if (!Bytes.Equal(pm.MembershipTag, pm.ComputeMembershipTag(Keys.MembershipKey, gc))) throw new InvalidOperationException("membership tag is invalid");
                if (!pm.VerifySignature(leaf.SignatureKey, gc)) throw new InvalidOperationException("signature is invalid");
            }
            else if (c.Sender.Type == Sender.External)
            {
                var senders = ExternalSenders();
                if (c.Sender.Index >= senders.Count) throw new InvalidOperationException("unknown external sender");
                if (c.ContentType != FramedContent.ProposalType) throw new InvalidOperationException("external senders may only propose");
                if (!pm.VerifySignature(senders[(int)c.Sender.Index].SignatureKey, gc)) throw new InvalidOperationException("signature is invalid");
            }
            else throw new NotSupportedException("new-member messages aren't supported");
            return pm;
        }

        // Queues a proposal for the next commit; returns its reference.
        public byte[] HandleProposal(MlsMessage m)
        {
            var pm = Unwrap(m);
            if (pm.Content.ContentType != FramedContent.ProposalType) throw new InvalidOperationException("not a proposal");
            var r = pm.ProposalRef();
            proposals.Add(new PendingProposal { Ref = r, Proposal = pm.Content.Proposal, Sender = pm.Content.Sender });
            return r;
        }

        public bool RevokeProposal(byte[] reference)
        {
            return proposals.RemoveAll(p => Bytes.Equal(p.Ref, reference)) > 0;
        }

        // Applies someone else's commit; returns the next epoch's state.
        public MlsGroup HandleCommit(MlsMessage m)
        {
            var pm = Unwrap(m);
            var c = pm.Content;
            if (c.ContentType != FramedContent.CommitType || c.Sender.Type != Sender.Member) throw new InvalidOperationException("not a member's commit");
            uint committer = c.Sender.Index;
            if (committer == MyLeaf) throw new InvalidOperationException("our own commit must be applied from the state it produced");

            var resolved = new List<PendingProposal>();
            foreach (var por in c.Commit.Proposals)
            {
                if (por.Proposal != null) { resolved.Add(new PendingProposal { Proposal = por.Proposal, Sender = c.Sender }); continue; }
                PendingProposal found = null;
                foreach (var p in proposals) if (Bytes.Equal(p.Ref, por.Reference)) found = p;
                if (found == null) throw new InvalidOperationException("commit refers to an unknown proposal");
                resolved.Add(found);
            }

            var next = Clone();
            bool needsPath;
            List<PskId> psks;
            var added = next.ApplyProposals(resolved, committer, out needsPath, out psks);
            var path = c.Commit.Path;
            if (needsPath && path == null) throw new InvalidOperationException("commit needs an update path");
            if (next.Tree.Leaf(committer) == null) throw new InvalidOperationException("committer removed itself");

            byte[] commitSecret = Mls.Zeros();
            bool removed = next.Tree.Leaf(MyLeaf) == null;
            if (path != null)
            {
                if (path.LeafNode.Source != LeafNode.FromCommit || !path.LeafNode.Verify(GroupId, committer)) throw new InvalidOperationException("update path leaf is invalid");
                var keys = new List<byte[]>();
                foreach (var n in path.Nodes) keys.Add(n.EncryptionKey);
                var leafParentHash = next.Tree.ApplyPath(committer, keys);
                if (!Bytes.Equal(leafParentHash, path.LeafNode.ParentHash)) throw new InvalidOperationException("update path parent hash is invalid");
                next.Tree.Nodes[(int)(2 * committer)] = path.LeafNode;
                next.TreeHash = next.Tree.TreeHash();
                if (!removed)
                {
                    var provisional = next.Context();
                    provisional.Epoch = Epoch + 1;
                    commitSecret = next.DecryptPath(committer, path, provisional.Bytes(), added);
                }
            }
            else next.TreeHash = next.Tree.TreeHash();

            next.ConfirmedTranscriptHash = Mls.Hash(Bytes.Concat(InterimTranscriptHash, pm.ConfirmedInput()));
            next.Epoch = Epoch + 1;
            next.proposals.Clear();
            if (removed) { next.Removed = true; return next; }
            var psk = psks.Count > 0 ? PskId.Secret(psks, ExternalPsks) : null;
            var ks = KeySchedule.FromInit(Keys.InitSecret, commitSecret, psk, next.Context().Bytes());
            if (!Bytes.Equal(pm.ConfirmationTag, Mls.Mac(ks.ConfirmationKey, next.ConfirmedTranscriptHash))) throw new InvalidOperationException("commit confirmation tag is invalid");
            next.SetEpochKeys(ks, pm.ConfirmationTag);
            return next;
        }

        // Applies proposals in RFC order (context extensions, updates, removes, adds); returns the
        // leaves added. A path is required unless everything was an Add or a PSK.
        List<KeyValuePair<uint, KeyPackage>> ApplyProposals(List<PendingProposal> list, uint committer, out bool needsPath, out List<PskId> psks)
        {
            needsPath = list.Count == 0;
            psks = new List<PskId>();
            foreach (var p in list)
            {
                int t = p.Proposal.Type;
                if (t == Proposal.ReInit || t == Proposal.ExternalInit) throw new NotSupportedException("proposal type " + t + " isn't supported");
                if (t == Proposal.Psk) psks.Add(p.Proposal.PskId());
                else if (t != Proposal.Add) needsPath = true;
            }
            foreach (var p in list)
                if (p.Proposal.Type == Proposal.GroupContextExtensions) Extensions = p.Proposal.Extensions;
            foreach (var p in list)
            {
                if (p.Proposal.Type != Proposal.Update) continue;
                if (p.Sender.Type != Sender.Member || p.Sender.Index == committer) throw new InvalidOperationException("invalid update proposal");
                var leaf = p.Proposal.LeafNode;
                if (leaf.Source != LeafNode.FromUpdate || !leaf.Verify(GroupId, p.Sender.Index)) throw new InvalidOperationException("update leaf is invalid");
                Tree.UpdateLeaf(p.Sender.Index, leaf);
            }
            foreach (var p in list)
            {
                if (p.Proposal.Type != Proposal.Remove) continue;
                if (Tree.Leaf(p.Proposal.Removed) == null) throw new InvalidOperationException("removing an empty leaf");
                Tree.RemoveLeaf(p.Proposal.Removed);
            }
            var added = new List<KeyValuePair<uint, KeyPackage>>();
            foreach (var p in list)
            {
                if (p.Proposal.Type != Proposal.Add) continue;
                var kp = p.Proposal.KeyPackage;
                if (kp.CipherSuite != Mls.CipherSuite || !kp.Verify() || !kp.LeafNode.Verify(GroupId, 0)) throw new InvalidOperationException("added key package is invalid");
                added.Add(new KeyValuePair<uint, KeyPackage>(Tree.AddLeaf(kp.LeafNode), kp));
            }
            return added;
        }

        static HashSet<int> AddedNodes(List<KeyValuePair<uint, KeyPackage>> added)
        {
            var set = new HashSet<int>();
            foreach (var a in added) set.Add((int)(2 * a.Key));
            return set;
        }

        // Finds the path secret encrypted to us, derives the keys above it, returns the commit secret.
        internal byte[] DecryptPath(uint committer, UpdatePath path, byte[] provisionalContext, List<KeyValuePair<uint, KeyPackage>> added)
        {
            int from = (int)(2 * committer), me = (int)(2 * MyLeaf);
            var fdp = Tree.FilteredDirectPath(from);
            var skip = AddedNodes(added);
            for (int i = 0; i < fdp.Count; i++)
            {
                int copath = RatchetTree.CopathChild(fdp[i], from);
                if (!TreeMath.InSubtree(me, copath)) continue;
                var res = Tree.Resolution(copath);
                res.RemoveAll(skip.Contains);
                var cts = path.Nodes[i].EncryptedPathSecret;
                if (cts.Count != res.Count) throw new InvalidOperationException("update path has the wrong number of ciphertexts");
                for (int j = 0; j < res.Count; j++)
                {
                    byte[] priv;
                    if (!Tree.Private.TryGetValue(res[j], out priv)) continue;
                    var ps = Mls.DecryptWithLabel(priv, "UpdatePathNode", provisionalContext, cts[j]);
                    if (ps == null) throw new InvalidOperationException("can't decrypt our path secret");
                    for (int k = i; k < fdp.Count; k++)
                    {
                        ImplantNodeKey(fdp[k], ps);
                        ps = Mls.DeriveSecret(ps, "path");
                    }
                    return ps;
                }
                throw new InvalidOperationException("no key to decrypt the update path");
            }
            throw new InvalidOperationException("update path doesn't cover us");
        }

        // ---- our own commits ----

        public sealed class CommitResult
        {
            public byte[] Message;       // the MLSMessage to send
            public Welcome Welcome;      // for added members, or null
            public MlsGroup Next;        // our state once the commit is accepted
        }

        // Commits every queued proposal, by reference, as DAVE does. A path is included only when
        // MLS requires one (anything other than adds). pathSecretForTest pins the random secret.
        public CommitResult CommitPending(byte[] pathSecretForTest = null)
        {
            var next = Clone();
            var resolved = new List<PendingProposal>(proposals);
            bool needsPath;
            List<PskId> psks;
            var added = next.ApplyProposals(resolved, MyLeaf, out needsPath, out psks);
            if (next.Tree.Leaf(MyLeaf) == null) throw new InvalidOperationException("can't commit our own removal");

            byte[] commitSecret = Mls.Zeros();
            UpdatePath path = null;
            var secrets = new List<byte[]>();
            var fdp = new List<int>();
            if (needsPath)
            {
                int me = (int)(2 * MyLeaf);
                fdp = next.Tree.FilteredDirectPath(me);
                var ps = pathSecretForTest ?? Mls.Random(Mls.Nh);
                var pubs = new List<byte[]>();
                var privs = new List<byte[]>();
                foreach (var x in fdp)
                {
                    secrets.Add(ps);
                    byte[] priv, pub;
                    Mls.DeriveKeyPair(Mls.DeriveSecret(ps, "node"), out priv, out pub);
                    privs.Add(priv);
                    pubs.Add(pub);
                    ps = Mls.DeriveSecret(ps, "path");
                }
                commitSecret = ps;
                var leafParentHash = next.Tree.ApplyPath(MyLeaf, pubs);
                for (int i = 0; i < fdp.Count; i++) next.Tree.Private[fdp[i]] = privs[i];

                byte[] leafPriv, leafPub;
                P256.Generate(out leafPriv, out leafPub);
                var leaf = next.Tree.Leaf(MyLeaf).Copy();
                leaf.EncryptionKey = leafPub;
                leaf.Source = LeafNode.FromCommit;
                leaf.ParentHash = leafParentHash;
                leaf.Sign(sigPriv, GroupId, MyLeaf);
                next.Tree.Nodes[me] = leaf;
                next.Tree.Private[me] = leafPriv;
                next.TreeHash = next.Tree.TreeHash();

                var provisional = next.Context();
                provisional.Epoch = Epoch + 1;
                var pcBytes = provisional.Bytes();
                var skip = AddedNodes(added);
                path = new UpdatePath { LeafNode = leaf };
                for (int i = 0; i < fdp.Count; i++)
                {
                    var node = new UpdatePathNode { EncryptionKey = pubs[i] };
                    var res = next.Tree.Resolution(RatchetTree.CopathChild(fdp[i], me));
                    res.RemoveAll(skip.Contains);
                    foreach (var r in res) node.EncryptedPathSecret.Add(Mls.EncryptWithLabel(next.Tree.PublicKey(r), "UpdatePathNode", pcBytes, secrets[i]));
                    path.Nodes.Add(node);
                }
            }
            else next.TreeHash = next.Tree.TreeHash();

            var commit = new Commit { Path = path };
            foreach (var p in resolved) commit.Proposals.Add(new ProposalOrRef { Reference = p.Ref });
            var pm = new PublicMessage
            {
                Content = new FramedContent { GroupId = GroupId, Epoch = Epoch, Sender = new Sender { Type = Sender.Member, Index = MyLeaf }, ContentType = FramedContent.CommitType, Commit = commit }
            };
            var oldContext = Context().Bytes();
            pm.Sign(sigPriv, oldContext);

            next.ConfirmedTranscriptHash = Mls.Hash(Bytes.Concat(InterimTranscriptHash, pm.ConfirmedInput()));
            next.Epoch = Epoch + 1;
            next.proposals.Clear();
            var newContext = next.Context().Bytes();
            var ks = KeySchedule.FromInit(Keys.InitSecret, commitSecret, psks.Count > 0 ? PskId.Secret(psks, ExternalPsks) : null, newContext);
            pm.ConfirmationTag = Mls.Mac(ks.ConfirmationKey, next.ConfirmedTranscriptHash);
            pm.MembershipTag = pm.ComputeMembershipTag(Keys.MembershipKey, oldContext);
            next.SetEpochKeys(ks, pm.ConfirmationTag);

            Welcome welcome = null;
            if (added.Count > 0)
            {
                var info = new GroupInfo
                {
                    Context = next.Context(),
                    Extensions = new List<Extension> { new Extension { Type = Mls.ExtRatchetTree, Data = next.Tree.Serialize() } },
                    ConfirmationTag = pm.ConfirmationTag,
                    Signer = MyLeaf,
                };
                info.Sign(sigPriv);
                welcome = new Welcome();
                using (var gcm = new AesGcm(Mls.ExpandWithLabel(ks.WelcomeSecret, "key", Mls.Empty, Mls.Nk)))
                    welcome.EncryptedGroupInfo = gcm.Seal(Mls.ExpandWithLabel(ks.WelcomeSecret, "nonce", Mls.Empty, Mls.Nn), info.Bytes(), null, 16);
                foreach (var a in added)
                {
                    byte[] pathSecret = null;
                    for (int i = 0; i < fdp.Count; i++)
                        if (TreeMath.InSubtree((int)(2 * a.Key), RatchetTree.CopathChild(fdp[i], (int)(2 * MyLeaf)))) { pathSecret = secrets[i]; break; }
                    var gs = new GroupSecrets { JoinerSecret = ks.JoinerSecret, PathSecret = pathSecret };
                    welcome.Secrets.Add(new EncryptedGroupSecrets
                    {
                        NewMember = a.Value.Ref(),
                        Secrets = Mls.EncryptWithLabel(a.Value.InitKey, "Welcome", welcome.EncryptedGroupInfo, Mls.Serialize(gs.Write)),
                    });
                }
            }
            return new CommitResult { Message = new MlsMessage { WireFormat = MlsMessage.PublicWire, Public = pm }.Bytes(), Welcome = welcome, Next = next };
        }

        // Builds the PublicMessage an external sender (the DAVE voice gateway) uses to propose.
        public static byte[] ExternalProposal(byte[] groupId, ulong epoch, uint senderIndex, Proposal proposal, byte[] sigPriv)
        {
            var pm = new PublicMessage
            {
                Content = new FramedContent { GroupId = groupId, Epoch = epoch, Sender = new Sender { Type = Sender.External, Index = senderIndex }, ContentType = FramedContent.ProposalType, Proposal = proposal }
            };
            pm.Sign(sigPriv, null);
            return new MlsMessage { WireFormat = MlsMessage.PublicWire, Public = pm }.Bytes();
        }

        // A fresh leaf and key package for joining; the caller keeps the private keys.
        public static KeyPackage MakeKeyPackage(Credential credential, byte[] sigPriv, byte[] sigPub, out byte[] leafPriv, out byte[] initPriv)
        {
            byte[] leafPub, initPub;
            P256.Generate(out leafPriv, out leafPub);
            P256.Generate(out initPriv, out initPub);
            var leaf = new LeafNode { EncryptionKey = leafPub, SignatureKey = sigPub, Credential = credential };
            leaf.Sign(sigPriv, null, 0);
            var kp = new KeyPackage { InitKey = initPub, LeafNode = leaf };
            kp.Sign(sigPriv);
            return kp;
        }
    }
}
