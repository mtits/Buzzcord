using System;
using System.Collections.Generic;
using System.IO;

namespace Buzzcord
{
    // MLS checks against the MLS working group's test vectors (testdata\mls, from
    // github.com/mlswg/mls-implementations). Hash-only checks also run the other SHA-256 suites (1
    // and 3); anything that signs or encrypts runs suite 2, the one DAVE uses.
    static partial class SelfTest
    {
        static string VectorDir()
        {
            var home = Environment.GetEnvironmentVariable("BUZZCORD_HOME");
            if (string.IsNullOrEmpty(home)) home = Path.GetDirectoryName(Path.GetDirectoryName(AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\')));
            return Path.Combine(home, "testdata", "mls");
        }

        // The vectors in one file, optionally only those for the given suites.
        static List<JObj> Vectors(string name, params int[] suites)
        {
            var path = Path.Combine(VectorDir(), name + ".json");
            if (!File.Exists(path)) throw new FileNotFoundException("missing test vectors: " + path);
            var all = Json.Parse(File.ReadAllText(path)) as List<object>;
            var list = new List<JObj>();
            foreach (var o in all)
            {
                var j = o as JObj;
                if (j == null) continue;
                if (suites.Length > 0 && Array.IndexOf(suites, (int)j.Long("cipher_suite")) < 0) continue;
                list.Add(j);
            }
            Assert(list.Count > 0, "no vectors for the wanted suites in " + name);
            return list;
        }

        static byte[] H(JObj o, string key) { return Hex.Decode(o.Str(key) ?? ""); }
        static void SameHex(byte[] actual, JObj o, string key, string what) { Same(actual, o.Str(key) ?? "", what); }

        static string MlsTreeMathTest()
        {
            int n = 0;
            foreach (var v in Vectors("tree-math"))
            {
                int leaves = (int)v.Long("n_leaves");
                Assert(TreeMath.NodeWidth(leaves) == v.Long("n_nodes"), "node width for " + leaves);
                Assert(TreeMath.Root(leaves) == v.Long("root"), "root for " + leaves);
                int root = TreeMath.Root(leaves);
                var left = v.Arr("left"); var right = v.Arr("right"); var parent = v.Arr("parent"); var sibling = v.Arr("sibling");
                for (int i = 0; i < left.Count; i++)
                {
                    bool leaf = i % 2 == 0;
                    Assert(leaf ? left[i] == null : Convert.ToInt32(left[i]) == TreeMath.Left(i), "left of " + i);
                    Assert(leaf ? right[i] == null : Convert.ToInt32(right[i]) == TreeMath.Right(i), "right of " + i);
                    Assert(i == root ? parent[i] == null : Convert.ToInt32(parent[i]) == TreeMath.Parent(i), "parent of " + i + " in " + leaves);
                    Assert(i == root ? sibling[i] == null : Convert.ToInt32(sibling[i]) == TreeMath.Sibling(i), "sibling of " + i);
                }
                n++;
            }
            return n + " trees";
        }

        static string MlsCryptoBasicsTest()
        {
            var v = Vectors("crypto-basics", 2)[0];
            var o = v.Obj("ref_hash");
            SameHex(Mls.RefHash(o.Str("label"), H(o, "value")), o, "out", "RefHash");
            o = v.Obj("expand_with_label");
            SameHex(Mls.ExpandWithLabel(H(o, "secret"), o.Str("label"), H(o, "context"), (int)o.Long("length")), o, "out", "ExpandWithLabel");
            o = v.Obj("derive_secret");
            SameHex(Mls.DeriveSecret(H(o, "secret"), o.Str("label")), o, "out", "DeriveSecret");
            o = v.Obj("derive_tree_secret");
            SameHex(Mls.DeriveTreeSecret(H(o, "secret"), o.Str("label"), (uint)o.Long("generation"), (int)o.Long("length")), o, "out", "DeriveTreeSecret");

            o = v.Obj("sign_with_label");
            Assert(Bytes.Equal(P256.PublicFromPrivate(H(o, "priv")), H(o, "pub")), "signature key pair");
            Assert(Mls.VerifyWithLabel(H(o, "pub"), o.Str("label"), H(o, "content"), H(o, "signature")), "their signature doesn't verify");
            var mine = Mls.SignWithLabel(H(o, "priv"), o.Str("label"), H(o, "content"));
            Assert(Mls.VerifyWithLabel(H(o, "pub"), o.Str("label"), H(o, "content"), mine), "our signature doesn't verify");

            o = v.Obj("encrypt_with_label");
            var ct = new HpkeCiphertext { KemOutput = H(o, "kem_output"), Ciphertext = H(o, "ciphertext") };
            SameHex(Mls.DecryptWithLabel(H(o, "priv"), o.Str("label"), H(o, "context"), ct), o, "plaintext", "DecryptWithLabel");
            var ours = Mls.EncryptWithLabel(H(o, "pub"), o.Str("label"), H(o, "context"), H(o, "plaintext"));
            SameHex(Mls.DecryptWithLabel(H(o, "priv"), o.Str("label"), H(o, "context"), ours), o, "plaintext", "EncryptWithLabel round trip");
            return null;
        }

        static string MlsKeyScheduleTest()
        {
            int epochs = 0;
            foreach (var v in Vectors("key-schedule", 1, 2, 3))
            {
                int suite = (int)v.Long("cipher_suite");
                var init = H(v, "initial_init_secret");
                ulong epoch = 0;
                foreach (var e in v.Objs("epochs"))
                {
                    var gc = new GroupContext { CipherSuite = suite, GroupId = H(v, "group_id"), Epoch = epoch, TreeHash = H(e, "tree_hash"), ConfirmedTranscriptHash = H(e, "confirmed_transcript_hash") };
                    var gcBytes = gc.Bytes();
                    SameHex(gcBytes, e, "group_context", "group context");
                    var ks = KeySchedule.FromInit(init, H(e, "commit_secret"), H(e, "psk_secret"), gcBytes);
                    SameHex(ks.JoinerSecret, e, "joiner_secret", "joiner");
                    SameHex(ks.WelcomeSecret, e, "welcome_secret", "welcome");
                    SameHex(ks.InitSecret, e, "init_secret", "init");
                    SameHex(ks.SenderDataSecret, e, "sender_data_secret", "sender data");
                    SameHex(ks.EncryptionSecret, e, "encryption_secret", "encryption");
                    SameHex(ks.ExporterSecret, e, "exporter_secret", "exporter");
                    SameHex(ks.EpochAuthenticator, e, "epoch_authenticator", "authenticator");
                    SameHex(ks.ExternalSecret, e, "external_secret", "external");
                    SameHex(ks.ConfirmationKey, e, "confirmation_key", "confirmation key");
                    SameHex(ks.MembershipKey, e, "membership_key", "membership key");
                    SameHex(ks.ResumptionPsk, e, "resumption_psk", "resumption");
                    var x = e.Obj("exporter");
                    // The exporter label is the hex text itself, not the bytes it spells.
                    SameHex(KeySchedule.Export(ks.ExporterSecret, Mls.Utf8(x.Str("label")), H(x, "context"), (int)x.Long("length")), x, "secret", "exported value");
                    if (suite == 2)
                    {
                        byte[] priv, pub;
                        Mls.DeriveKeyPair(ks.ExternalSecret, out priv, out pub);
                        SameHex(pub, e, "external_pub", "external public key");
                    }
                    init = ks.InitSecret;
                    epoch++;
                    epochs++;
                }
            }
            return epochs + " epochs";
        }

        static string MlsTranscriptTest()
        {
            foreach (var v in Vectors("transcript-hashes", 1, 2, 3))
            {
                var r = new TlsReader(H(v, "authenticated_content"));
                int wire = r.U16();
                var content = FramedContent.Read(r);
                var sig = r.Opaque();
                var tag = r.Opaque();
                r.ExpectEnd();
                var w = new TlsWriter().U16(wire);
                content.Write(w);
                var confirmed = Mls.Hash(Bytes.Concat(H(v, "interim_transcript_hash_before"), w.Opaque(sig).ToArray()));
                SameHex(confirmed, v, "confirmed_transcript_hash_after", "confirmed transcript hash");
                Assert(Bytes.Equal(tag, Mls.Mac(H(v, "confirmation_key"), confirmed)), "confirmation tag");
                SameHex(Mls.Hash(Bytes.Concat(confirmed, new TlsWriter().Opaque(tag).ToArray())), v, "interim_transcript_hash_after", "interim transcript hash");
            }
            return null;
        }

        // Every struct must parse and re-serialize to the same bytes.
        static string MlsMessagesTest()
        {
            var codecs = new Dictionary<string, Func<TlsReader, byte[]>>
            {
                { "mls_welcome", r => MlsMessage.Read(r).Bytes() },
                { "mls_group_info", r => MlsMessage.Read(r).Bytes() },
                { "mls_key_package", r => MlsMessage.Read(r).Bytes() },
                { "public_message_application", r => MlsMessage.Read(r).Bytes() },
                { "public_message_proposal", r => MlsMessage.Read(r).Bytes() },
                { "public_message_commit", r => MlsMessage.Read(r).Bytes() },
                { "private_message", r => MlsMessage.Read(r).Bytes() },
                { "group_secrets", r => Mls.Serialize(GroupSecrets.Read(r).Write) },
                { "add_proposal", r => Mls.Serialize(Proposal.ReadBody(r, Proposal.Add).WriteBody) },
                { "update_proposal", r => Mls.Serialize(Proposal.ReadBody(r, Proposal.Update).WriteBody) },
                { "remove_proposal", r => Mls.Serialize(Proposal.ReadBody(r, Proposal.Remove).WriteBody) },
                { "pre_shared_key_proposal", r => Mls.Serialize(Proposal.ReadBody(r, Proposal.Psk).WriteBody) },
                { "re_init_proposal", r => Mls.Serialize(Proposal.ReadBody(r, Proposal.ReInit).WriteBody) },
                { "external_init_proposal", r => Mls.Serialize(Proposal.ReadBody(r, Proposal.ExternalInit).WriteBody) },
                { "group_context_extensions_proposal", r => Mls.Serialize(Proposal.ReadBody(r, Proposal.GroupContextExtensions).WriteBody) },
                { "commit", r => Mls.Serialize(Commit.Read(r).Write) },
            };
            int n = 0;
            foreach (var v in Vectors("messages"))
            {
                foreach (var kv in codecs)
                {
                    var data = H(v, kv.Key);
                    var r = new TlsReader(data);
                    byte[] back;
                    try { back = kv.Value(r); r.ExpectEnd(); }
                    catch (Exception ex) { throw new Exception(kv.Key + " #" + n + ": " + ex.Message); }
                    Assert(Bytes.Equal(back, data), kv.Key + " #" + n + " didn't round-trip");
                }
                var tree = H(v, "ratchet_tree");
                Assert(Bytes.Equal(RatchetTree.Parse(tree).Serialize(), tree), "ratchet_tree #" + n + " didn't round-trip");
                n++;
            }
            return n + " sets of " + (codecs.Count + 1) + " structs";
        }

        static string MlsTreeValidationTest()
        {
            int n = 0;
            foreach (var v in Vectors("tree-validation", 1, 2, 3))
            {
                var tree = RatchetTree.Parse(H(v, "tree"));
                var hashes = v.Arr("tree_hashes");
                for (int i = 0; i < hashes.Count; i++) Same(tree.HashAt(i, null), (string)hashes[i], "tree hash of node " + i + " in tree " + n);
                var res = v.Arr("resolutions");
                for (int i = 0; i < res.Count; i++)
                {
                    var want = new List<int>();
                    foreach (var x in (List<object>)res[i]) want.Add(Convert.ToInt32(x));
                    Assert(string.Join(",", want) == string.Join(",", tree.Resolution(i)), "resolution of node " + i + " in tree " + n);
                }
                Assert(tree.VerifyParentHashes(), "parent hashes of tree " + n);
                if (v.Long("cipher_suite") == 2) Assert(tree.VerifyLeafSignatures(H(v, "group_id")), "leaf signatures of tree " + n);
                n++;
            }
            return n + " trees";
        }

        static string MlsTreeOperationsTest()
        {
            int n = 0;
            foreach (var v in Vectors("tree-operations"))
            {
                var tree = RatchetTree.Parse(H(v, "tree_before"));
                SameHex(tree.TreeHash(), v, "tree_hash_before", "tree hash before #" + n);
                var p = Proposal.Read(new TlsReader(H(v, "proposal")));
                uint sender = (uint)v.Long("proposal_sender");
                if (p.Type == Proposal.Add) tree.AddLeaf(p.KeyPackage.LeafNode);
                else if (p.Type == Proposal.Update) tree.UpdateLeaf(sender, p.LeafNode);
                else if (p.Type == Proposal.Remove) tree.RemoveLeaf(p.Removed);
                SameHex(tree.Serialize(), v, "tree_after", "tree after #" + n + " (proposal type " + p.Type + ")");
                SameHex(tree.TreeHash(), v, "tree_hash_after", "tree hash after #" + n);
                n++;
            }
            return n + " operations";
        }

        // Each other member processes each UpdatePath: parent hashes, decryption, commit secret.
        static string MlsTreeKemTest()
        {
            int n = 0;
            foreach (var v in Vectors("treekem", 2))
            {
                var tree = RatchetTree.Parse(H(v, "ratchet_tree"));
                var groupId = H(v, "group_id");
                foreach (var u in v.Objs("update_paths"))
                {
                    uint sender = (uint)u.Long("sender");
                    var path = UpdatePath.Read(new TlsReader(H(u, "update_path")));
                    var keys = new List<byte[]>();
                    foreach (var node in path.Nodes) keys.Add(node.EncryptionKey);
                    foreach (var me in v.Objs("leaves_private"))
                    {
                        uint index = (uint)me.Long("index");
                        if (index == sender) continue;
                        var t = tree.Clone();
                        t.Private[(int)(2 * index)] = H(me, "encryption_priv");
                        foreach (var ps in me.Objs("path_secrets"))
                        {
                            byte[] priv, pub;
                            Mls.DeriveKeyPair(Mls.DeriveSecret(H(ps, "path_secret"), "node"), out priv, out pub);
                            t.Private[(int)ps.Long("node")] = priv;
                        }
                        var leafParentHash = t.ApplyPath(sender, keys);
                        Assert(Bytes.Equal(leafParentHash, path.LeafNode.ParentHash), "parent hash in path from " + sender);
                        Assert(path.LeafNode.Verify(groupId, sender), "path leaf signature from " + sender);
                        t.Nodes[(int)(2 * sender)] = path.LeafNode;
                        SameHex(t.TreeHash(), u, "tree_hash_after", "tree hash after path from " + sender);
                        Assert(t.VerifyParentHashes(), "parent hashes after path from " + sender);
                        var gc = new GroupContext { GroupId = groupId, Epoch = (ulong)v.Long("epoch"), TreeHash = H(u, "tree_hash_after"), ConfirmedTranscriptHash = H(v, "confirmed_transcript_hash") };
                        var g = new MlsGroup { GroupId = groupId, Tree = t, MyLeaf = index };
                        SameHex(g.DecryptPath(sender, path, gc.Bytes(), new List<KeyValuePair<uint, KeyPackage>>()), u, "commit_secret", "commit secret for leaf " + index + " from " + sender);
                        n++;
                    }
                }
            }
            return n + " path decryptions";
        }

        static string MlsWelcomeTest()
        {
            var v = Vectors("welcome", 2)[0];
            var kp = MlsMessage.Parse(H(v, "key_package")).KeyPackage;
            var welcome = MlsMessage.Parse(H(v, "welcome")).Welcome;
            EncryptedGroupSecrets mine = null;
            foreach (var s in welcome.Secrets) if (Bytes.Equal(s.NewMember, kp.Ref())) mine = s;
            Assert(mine != null, "key package reference not found in the Welcome");
            var gs = GroupSecrets.Read(new TlsReader(Mls.DecryptWithLabel(H(v, "init_priv"), "Welcome", welcome.EncryptedGroupInfo, mine.Secrets)));
            var welcomeSecret = KeySchedule.FromJoiner(gs.JoinerSecret, null, Mls.Empty).WelcomeSecret;
            byte[] infoBytes;
            using (var gcm = new AesGcm(Mls.ExpandWithLabel(welcomeSecret, "key", Mls.Empty, Mls.Nk)))
                infoBytes = gcm.Open(Mls.ExpandWithLabel(welcomeSecret, "nonce", Mls.Empty, Mls.Nn), welcome.EncryptedGroupInfo, null, 16);
            Assert(infoBytes != null, "group info didn't decrypt");
            var info = GroupInfo.Read(new TlsReader(infoBytes));
            Assert(info.Verify(H(v, "signer_pub")), "group info signature");
            var ks = KeySchedule.FromJoiner(gs.JoinerSecret, null, info.Context.Bytes());
            Assert(Bytes.Equal(info.ConfirmationTag, Mls.Mac(ks.ConfirmationKey, info.Context.ConfirmedTranscriptHash)), "confirmation tag");
            return null;
        }

        // Join from a Welcome, then follow every commit, checking the epoch authenticator each time.
        static string MlsPassiveClientTest(string file)
        {
            int passed = 0, skipped = 0, epochs = 0;
            var reasons = new HashSet<string>();
            foreach (var v in Vectors(file, 2))
            {
                try
                {
                    var kp = MlsMessage.Parse(H(v, "key_package")).KeyPackage;
                    var welcome = MlsMessage.Parse(H(v, "welcome")).Welcome;
                    var treeHex = v.Str("ratchet_tree");
                    var tree = string.IsNullOrEmpty(treeHex) ? null : RatchetTree.Parse(Hex.Decode(treeHex));
                    var psks = new Dictionary<string, byte[]>();
                    foreach (var p in v.Objs("external_psks")) psks[p.Str("psk_id")] = H(p, "psk");
                    var g = MlsGroup.Join(welcome, kp, H(v, "init_priv"), H(v, "encryption_priv"), H(v, "signature_priv"), tree, psks);
                    SameHex(g.EpochAuthenticator, v, "initial_epoch_authenticator", file + " #" + (passed + skipped) + " initial epoch");
                    foreach (var e in v.Objs("epochs"))
                    {
                        foreach (var p in e.Arr("proposals")) g.HandleProposal(MlsMessage.Parse(Hex.Decode((string)p)));
                        g = g.HandleCommit(MlsMessage.Parse(H(e, "commit")));
                        SameHex(g.EpochAuthenticator, e, "epoch_authenticator", file + " #" + (passed + skipped) + " epoch " + g.Epoch);
                        epochs++;
                    }
                    passed++;
                }
                catch (NotSupportedException ex) { skipped++; reasons.Add(ex.Message); }   // features DAVE never uses
            }
            Assert(passed > 0, "no " + file + " vector could run: " + string.Join("; ", reasons));
            return passed + " passed (" + epochs + " commits), " + skipped + " skipped: " + string.Join("; ", reasons);
        }

        static string MlsMessageProtectionTest()
        {
            var v = Vectors("message-protection", 2)[0];
            var gc = new GroupContext { GroupId = H(v, "group_id"), Epoch = (ulong)v.Long("epoch"), TreeHash = H(v, "tree_hash"), ConfirmedTranscriptHash = H(v, "confirmed_transcript_hash") }.Bytes();
            foreach (var kind in new[] { "proposal", "commit" })
            {
                var pm = MlsMessage.Parse(H(v, kind + "_pub")).Public;
                Assert(Bytes.Equal(pm.MembershipTag, pm.ComputeMembershipTag(H(v, "membership_key"), gc)), kind + " membership tag");
                Assert(pm.VerifySignature(H(v, "signature_pub"), gc), kind + " signature");
                var body = kind == "proposal" ? Mls.Serialize(pm.Content.Proposal.Write) : Mls.Serialize(pm.Content.Commit.Write);
                SameHex(body, v, kind, kind + " content");
            }
            return null;
        }
    }
}
