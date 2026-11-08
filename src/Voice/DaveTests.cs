using System;
using System.Collections.Generic;
using System.Text;

namespace Buzzcord
{
    // DAVE end to end with a fake voice gateway: two people form a group, a third joins, then
    // leaves; a broken commit makes a client reset; frames flow between everyone at each step.
    static partial class SelfTest
    {
        sealed class FakeDaveClient
        {
            public ulong Id;
            public DaveSession Session;
            public readonly List<KeyValuePair<int, byte[]>> Binary = new List<KeyValuePair<int, byte[]>>();
            public readonly List<KeyValuePair<int, JObj>> Json = new List<KeyValuePair<int, JObj>>();
            public readonly List<string> Logs = new List<string>();

            public FakeDaveClient(ulong id, ulong channel)
            {
                Id = id;
                Session = new DaveSession(id, channel);
                Session.SendBinary += (op, p) => Binary.Add(new KeyValuePair<int, byte[]>(op, p));
                Session.SendJson += (op, d) => Json.Add(new KeyValuePair<int, JObj>(op, d));
                Session.Log += s => Logs.Add(s);
            }

            public byte[] Take(int op)
            {
                for (int i = Binary.Count - 1; i >= 0; i--)
                    if (Binary[i].Key == op) { var p = Binary[i].Value; Binary.Clear(); return p; }
                throw new Exception("client " + Id + " didn't send op " + op + "; its log: " + string.Join(" | ", Logs));
            }

            public bool SentJson(int op, int transition)
            {
                foreach (var kv in Json) if (kv.Key == op && kv.Value.Long("transition_id") == transition) return true;
                return false;
            }
        }

        static string DaveFrameTest()
        {
            // Generations wrap with the nonce's top byte but keep counting.
            Assert(DaveFrame.WrappedGeneration(0, 5) == 5 && DaveFrame.WrappedGeneration(250, 3) == 259 && DaveFrame.WrappedGeneration(300, 50) == 306, "generation wrap");
            Assert(Hex.Encode(DaveFrame.Leb128(300)) == "ac02" && Hex.Encode(DaveFrame.Leb128(1)) == "01", "LEB128");
            var r1 = new HashRatchet(new byte[16]);
            var r2 = new HashRatchet(new byte[16]);
            Assert(Bytes.Equal(r1.Get(3), r2.Get(3)) && !Bytes.Equal(r1.Get(0), r1.Get(1)), "ratchet");
            var enc = new FrameEncryptor(new HashRatchet(new byte[16]));
            var frame = enc.Encrypt(Encoding.ASCII.GetBytes("opus!"));
            int n = frame.Length;
            Assert(frame[n - 1] == 0xFA && frame[n - 2] == 0xFA && frame[n - 3] == 8 + 1 + 3 && n == 5 + 8 + 1 + 3, "frame layout");
            var parsed = DaveFrame.Parse(frame);
            Assert(parsed != null && parsed.Nonce == 1 && parsed.CiphertextLength == 5, "frame parse");
            var dec = new FrameDecryptor(new HashRatchet(new byte[16]), DateTime.UtcNow);
            Assert(Encoding.ASCII.GetString(dec.Decrypt(parsed, frame, DateTime.UtcNow)) == "opus!", "frame decrypt");
            Assert(dec.Decrypt(parsed, frame, DateTime.UtcNow) == null, "replayed frame accepted");
            return null;
        }

        static string DaveCallTest()
        {
            const ulong channel = 1234567890123456789UL;
            var gatewayKeys = new byte[2][];
            P256.Generate(out gatewayKeys[0], out gatewayKeys[1]);
            var external = new ExternalSender { SignatureKey = gatewayKeys[1], Credential = Credential.Basic(Encoding.ASCII.GetBytes("voice")) };
            var groupId = DaveSession.BigEndian(channel);
            var log = new List<string>();

            Func<ulong, FakeDaveClient> join = id =>
            {
                var c = new FakeDaveClient(id, channel);
                c.Session.Log += s => log.Add(id + " " + s);
                c.Session.Start(1);
                c.Session.OnBinary(25, external.Bytes());
                return c;
            };
            Action<FakeDaveClient[], ulong[]> members = (cs, ids) => { foreach (var c in cs) { c.Session.Members.Clear(); foreach (var id in ids) c.Session.Members.Add(id); } };
            // The gateway's proposals, as op 27 (append).
            Func<ulong, Proposal[], byte[]> proposals = (epoch, ps) =>
            {
                var w = new TlsWriter().U8(0);
                w.Vector(x => { foreach (var p in ps) x.Raw(MlsGroup.ExternalProposal(groupId, epoch, 0, p, gatewayKeys[0])); });
                return w.ToArray();
            };
            // Op 26 carries a bare KeyPackage (what Discord's own client sends), not an MLSMessage.
            Func<byte[], KeyPackage> keyPackage = op26 =>
            {
                Assert(op26.Length > 4 && !(op26[2] == 0 && op26[3] == 5), "key package was wrapped in an MLSMessage");
                var r = new TlsReader(op26);
                var kp = KeyPackage.Read(r);
                r.ExpectEnd();
                return kp;
            };
            // Splits a client's op 28 into its commit and welcome.
            Func<byte[], KeyValuePair<byte[], byte[]>> split = op28 =>
            {
                var r = new TlsReader(op28);
                int start = r.Position;
                MlsMessage.Read(r);
                var commit = r.Since(start);
                return new KeyValuePair<byte[], byte[]>(commit, r.Remaining > 0 ? r.Raw(r.Remaining) : null);
            };
            Func<int, byte[], byte[]> withTransition = (t, body) => new TlsWriter().U16(t).Raw(body).ToArray();
            Action<FakeDaveClient[], int> execute = (cs, t) =>
            {
                foreach (var c in cs) Assert(c.SentJson(23, t), "client " + c.Id + " isn't ready for transition " + t + ": " + string.Join(" | ", log));
                foreach (var c in cs) { c.Session.OnExecuteTransition(t); c.Json.Clear(); }
            };
            Action<FakeDaveClient, FakeDaveClient, string> talk = (from, to, text) =>
            {
                var sent = from.Session.Encrypt(Encoding.UTF8.GetBytes(text));
                Assert(DaveFrame.Parse(sent) != null, from.Id + " sent an unencrypted frame");
                var got = to.Session.Decrypt(from.Id, sent);
                Assert(got != null && Encoding.UTF8.GetString(got) == text, to.Id + " couldn't decrypt " + from.Id + ": " + string.Join(" | ", log));
            };

            // 1. Alice and Bob create the call. Each makes a group of one; the gateway asks each to
            // add the other, and Alice's commit wins.
            var alice = join(1001);
            var bob = join(1002);
            members(new[] { alice, bob }, new ulong[] { 1001, 1002 });
            var kpAlice = keyPackage(alice.Take(26));
            var kpBob = keyPackage(bob.Take(26));
            alice.Session.OnBinary(27, proposals(0, new[] { new Proposal { Type = Proposal.Add, KeyPackage = kpBob } }));
            bob.Session.OnBinary(27, proposals(0, new[] { new Proposal { Type = Proposal.Add, KeyPackage = kpAlice } }));
            var aliceCommit = split(alice.Take(28));
            bob.Take(28);
            Assert(aliceCommit.Value != null, "commit adding Bob has no welcome");
            alice.Session.OnBinary(29, withTransition(5, aliceCommit.Key));
            bob.Session.OnBinary(30, withTransition(5, aliceCommit.Value));
            execute(new[] { alice, bob }, 5);
            Assert(alice.Session.Established && bob.Session.Established, "group not formed: " + string.Join(" | ", log));
            Assert(alice.Session.PrivacyCode == bob.Session.PrivacyCode && alice.Session.PrivacyCode.Length == 35, "privacy codes differ");
            talk(alice, bob, "hi bob");
            talk(bob, alice, "hi alice");
            var replay = alice.Session.Encrypt(Encoding.UTF8.GetBytes("once"));
            Assert(bob.Session.Decrypt(1001, replay) != null && bob.Session.Decrypt(1001, replay) == null, "replay accepted");
            var tampered = alice.Session.Encrypt(Encoding.UTF8.GetBytes("tamper"));
            tampered[2] ^= 1;
            Assert(bob.Session.Decrypt(1001, tampered) == null, "tampered frame accepted");
            Assert(bob.Session.Decrypt(1001, Encoding.ASCII.GetBytes("plain")) == null, "unencrypted frame accepted in an encrypted call");

            // 2. Carol joins; both members commit, Bob's wins.
            var carol = join(1003);
            members(new[] { alice, bob, carol }, new ulong[] { 1001, 1002, 1003 });
            var kpCarol = keyPackage(carol.Take(26));
            var addCarol = proposals(1, new[] { new Proposal { Type = Proposal.Add, KeyPackage = kpCarol } });
            alice.Session.OnBinary(27, addCarol);
            bob.Session.OnBinary(27, addCarol);
            alice.Take(28);
            var bobCommit = split(bob.Take(28));
            var beforeCarol = alice.Session.Encrypt(Encoding.UTF8.GetBytes("still epoch 1"));
            alice.Session.OnBinary(29, withTransition(6, bobCommit.Key));
            bob.Session.OnBinary(29, withTransition(6, bobCommit.Key));
            carol.Session.OnBinary(30, withTransition(6, bobCommit.Value));
            var lateFrame = bob.Session.Decrypt(1001, beforeCarol);
            Assert(lateFrame != null, "frame from just before the transition was dropped");
            execute(new[] { alice, bob, carol }, 6);
            Assert(alice.Session.Epoch == 2 && carol.Session.Epoch == 2, "not at epoch 2");
            Assert(alice.Session.PrivacyCode == carol.Session.PrivacyCode && bob.Session.PrivacyCode == carol.Session.PrivacyCode, "privacy codes differ after Carol joined");
            talk(carol, alice, "hey all");
            talk(alice, carol, "welcome carol");
            talk(bob, carol, "hello");

            // 3. Carol leaves; Alice's commit (with a path, since it removes) wins.
            members(new[] { alice, bob }, new ulong[] { 1001, 1002 });
            uint carolLeaf = 2;
            var removeCarol = proposals(2, new[] { new Proposal { Type = Proposal.Remove, Removed = carolLeaf } });
            alice.Session.OnBinary(27, removeCarol);
            bob.Session.OnBinary(27, removeCarol);
            var removal = split(alice.Take(28));
            bob.Take(28);
            Assert(removal.Value == null, "a removal shouldn't carry a welcome");
            Assert(MlsMessage.Parse(removal.Key).Public.Content.Commit.Path != null, "a removal must carry an update path");
            alice.Session.OnBinary(29, withTransition(7, removal.Key));
            bob.Session.OnBinary(29, withTransition(7, removal.Key));
            execute(new[] { alice, bob }, 7);
            talk(alice, bob, "just us");
            talk(bob, alice, "yep");
            var code = alice.Session.PrivacyCode;
            Assert(carol.Session.Decrypt(1001, alice.Session.Encrypt(Encoding.UTF8.GetBytes("secret"))) == null, "Carol can still hear after leaving");

            // An add for someone who isn't in the call is refused (no commit comes back).
            alice.Binary.Clear();
            alice.Session.OnBinary(27, proposals(3, new[] { new Proposal { Type = Proposal.Add, KeyPackage = kpCarol } }));
            Assert(alice.Binary.Count == 0, "committed an add for someone not in the call");
            alice.Session.Start(1);   // drop the half-queued state for the rest of the test
            Assert(!alice.Session.Established, "restart kept the old group");
            members(new[] { alice, bob }, new ulong[] { 1001, 1002, 1003 });

            // 4. A broken commit: Bob reports it (op 31) and starts over with a new key package.
            var broken = (byte[])removal.Key.Clone();
            broken[broken.Length - 5] ^= 0x55;
            bob.Session.OnBinary(27, proposals(3, new[] { new Proposal { Type = Proposal.Add, KeyPackage = kpCarol } }));
            bob.Binary.Clear();
            bob.Session.OnBinary(29, withTransition(8, broken));
            Assert(bob.SentJson(31, 8), "no invalid-commit report");
            bob.Take(26);
            Assert(!bob.Session.Established, "Bob kept a group after a broken commit");

            // 5. Revoking a queued proposal still produces a commit for what's left. Alice restarted,
            // so she's in her own new group (epoch 0) waiting to be added.
            alice.Take(26);
            alice.Binary.Clear();
            var queuedBytes = MlsGroup.ExternalProposal(groupId, 0, 0, new Proposal { Type = Proposal.Add, KeyPackage = kpCarol }, gatewayKeys[0]);
            var append = new TlsWriter().U8(0);
            append.Vector(x => x.Raw(queuedBytes));
            alice.Session.OnBinary(27, append.ToArray());
            alice.Take(28);
            var revoke = new TlsWriter().U8(1);
            revoke.Vector(x => x.Opaque(MlsMessage.Parse(queuedBytes).Public.ProposalRef()));
            alice.Session.OnBinary(27, revoke.ToArray());
            var empty = split(alice.Take(28));
            Assert(MlsMessage.Parse(empty.Key).Public.Content.Commit.Proposals.Count == 0, "revoked proposal still committed");
            return "3 members, 4 epochs, last privacy code " + code;
        }
    }
}
