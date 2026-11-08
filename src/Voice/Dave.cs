using System;
using System.Collections.Generic;

namespace Buzzcord
{
    // DAVE, Discord's end-to-end encryption for calls (protocol version 1), for one voice
    // connection: the MLS group the voice gateway coordinates (opcodes 21-31) and the per-sender
    // media keys that come from it. It doesn't touch the network: the voice connection feeds it
    // gateway messages and frames, and sends what it raises. Mirrors Discord's libdave.
    public sealed class DaveSession
    {
        public const int MaxProtocolVersion = 1;
        const string MediaKeyLabel = "Discord Secure Frames v0";
        public static readonly byte[] Silence = { 0xF8, 0xFF, 0xFE };

        readonly object sync = new object();
        readonly ulong selfId;
        readonly byte[] groupId;
        readonly byte[] sigPriv, sigPub;

        public readonly HashSet<ulong> Members = new HashSet<ulong>();   // who's in the call, from the voice gateway
        public Func<DateTime> Now = () => DateTime.UtcNow;

        public event Action<int, byte[]> SendBinary;   // opcode 26 or 28, payload
        public event Action<int, JObj> SendJson;       // opcode 23 or 31, data
        public event Action<string> Log;
        public event Action StateChanged;              // new epoch or protocol version

        public int ProtocolVersion { get; private set; }

        ExternalSender externalSender;
        LeafNode selfLeaf;
        byte[] selfLeafPriv;
        KeyPackage joinKeyPackage;
        byte[] joinInitPriv;
        MlsGroup pendingGroup, current, withProposals, outboundNext;
        byte[] outboundCommit;

        FrameEncryptor encryptor, pendingEncryptor;
        readonly Dictionary<ulong, List<FrameDecryptor>> decryptors = new Dictionary<ulong, List<FrameDecryptor>>();
        int? pendingTransitionId;
        int pendingTransitionVersion;
        DateTime passthroughUntil = DateTime.MaxValue;

        public DaveSession(ulong selfUserId, ulong channelId)
        {
            selfId = selfUserId;
            groupId = BigEndian(channelId);
            P256.Generate(out sigPriv, out sigPub);
            Members.Add(selfUserId);
        }

        public static byte[] BigEndian(ulong v)
        {
            var b = new byte[8];
            for (int i = 7; i >= 0; i--) { b[i] = (byte)v; v >>= 8; }
            return b;
        }

        public static ulong FromBigEndian(byte[] b)
        {
            ulong v = 0;
            if (b != null && b.Length <= 8) foreach (var x in b) v = (v << 8) | x;
            return v;
        }

        static byte[] LittleEndian(ulong v) { return BitConverter.GetBytes(v); } // Windows is little-endian

        void Say(string s) { if (Log != null) Log("dave: " + s); }

        // Epoch authenticator as Discord's 30-digit "voice privacy code" (6 groups of 5 digits).
        public string PrivacyCode
        {
            get
            {
                lock (sync)
                {
                    if (current == null) return null;
                    var a = current.EpochAuthenticator;
                    var parts = new List<string>();
                    for (int g = 0; g < 6; g++)
                    {
                        ulong v = 0;
                        for (int i = 0; i < 5; i++) v = (v << 8) | a[g * 5 + i];
                        parts.Add((v % 100000).ToString("D5"));
                    }
                    return string.Join(" ", parts);
                }
            }
        }

        public void SetMember(ulong id, bool inCall)
        {
            lock (sync)
            {
                if (inCall) Members.Add(id);
                else if (id != selfId) Members.Remove(id);
            }
        }

        public bool Established { get { lock (sync) return current != null; } }
        public ulong Epoch { get { lock (sync) return current != null ? current.Epoch : 0; } }

        // ---- gateway messages ----

        // From Session Description (op 4): the DAVE version the call will use.
        public void Start(int protocolVersion)
        {
            lock (sync)
            {
                ProtocolVersion = protocolVersion;
                Say("call uses DAVE protocol version " + protocolVersion);
                Reinit();
            }
        }

        void Reinit()
        {
            pendingGroup = current = withProposals = outboundNext = null;
            outboundCommit = null;
            joinKeyPackage = null;
            encryptor = pendingEncryptor = null;
            pendingTransitionId = null;
            if (ProtocolVersion == 0)
            {
                passthroughUntil = DateTime.MaxValue;
                Say("transport encryption only");
                return;
            }
            byte[] leafPub;
            P256.Generate(out selfLeafPriv, out leafPub);
            selfLeaf = new LeafNode { EncryptionKey = leafPub, SignatureKey = sigPub, Credential = Credential.Basic(BigEndian(selfId)) };
            selfLeaf.Sign(sigPriv, null, 0);
            if (externalSender != null) CreatePendingGroup();
            Say("sending a new key package" + (externalSender != null ? "" : " (no voice gateway identity yet)"));
            SendKeyPackage();
        }

        void CreatePendingGroup()
        {
            var ext = new List<Extension>
            {
                new Extension { Type = Mls.ExtExternalSenders, Data = Mls.Serialize(w => Mls.WriteList(w, new List<ExternalSender> { externalSender }, (x, s) => s.Write(x))) }
            };
            pendingGroup = MlsGroup.Create(groupId, selfLeaf, selfLeafPriv, sigPriv, ext);
        }

        // Key packages are single-use: a new one every time we wait to be added.
        void SendKeyPackage()
        {
            byte[] initPub;
            P256.Generate(out joinInitPriv, out initPub);
            joinKeyPackage = new KeyPackage { InitKey = initPub, LeafNode = selfLeaf };
            joinKeyPackage.Sign(sigPriv);
            // A bare KeyPackage, as libdave sends it. The protocol write-up says MLSMessage, but the
            // voice gateway ignores a wrapped one: it then never asks anyone to add us.
            Raise(26, joinKeyPackage.Bytes());
        }

        void Raise(int op, byte[] payload) { if (SendBinary != null) SendBinary(op, payload); }
        void Raise(int op, JObj data) { if (SendJson != null) SendJson(op, data); }

        // Binary opcodes 25, 27, 29, 30 (payload without the sequence number and opcode).
        public void OnBinary(int op, byte[] payload)
        {
            lock (sync)
            {
                switch (op)
                {
                    case 25: OnExternalSender(payload); break;
                    case 27: OnProposals(payload); break;
                    case 29: OnCommit(payload); break;
                    case 30: OnWelcome(payload); break;
                }
            }
        }

        void OnExternalSender(byte[] payload)
        {
            try
            {
                var r = new TlsReader(payload);
                externalSender = ExternalSender.Read(r);
                r.ExpectEnd();
            }
            catch (Exception ex) { Say("bad external sender: " + ex.Message); return; }
            Say("got the voice gateway identity" + (current == null && selfLeaf != null && ProtocolVersion > 0 ? "; made our own pending group" : ""));
            if (current == null && selfLeaf != null && ProtocolVersion > 0) CreatePendingGroup();
        }

        void OnProposals(byte[] payload)
        {
            var baseState = pendingGroup ?? current;
            if (baseState == null) { Say("proposals before we have a group"); return; }
            try
            {
                if (withProposals == null) withProposals = baseState.Fork();
                var r = new TlsReader(payload);
                bool revoke = r.U8() == 1;
                if (revoke)
                {
                    foreach (var reference in Mls.ReadList(r, x => x.Opaque()))
                        if (!withProposals.RevokeProposal(reference)) throw new InvalidOperationException("revoking an unknown proposal");
                }
                else
                {
                    foreach (var m in Mls.ReadList(r, MlsMessage.Read))
                    {
                        CheckProposal(m, withProposals);
                        withProposals.HandleProposal(m);
                    }
                }
                r.ExpectEnd();

                Say((revoke ? "proposals revoked" : "proposals received") + ", " + withProposals.Proposals.Count + " queued at epoch " + withProposals.Epoch + "; members in call: " + string.Join(",", Members));
                var result = withProposals.CommitPending();
                outboundCommit = result.Message;
                outboundNext = result.Next;
                var w = new TlsWriter().Raw(result.Message);
                if (result.Welcome != null) w.Raw(result.Welcome.Bytes());
                Say("sending our commit" + (result.Welcome != null ? " with a welcome" : ""));
                Raise(28, w.ToArray());
            }
            catch (Exception ex) { Say("proposals failed: " + ex.Message); }
        }

        // DAVE's rules on top of MLS: only the gateway proposes, only adds and removes, and only
        // adds of people actually in the call.
        void CheckProposal(MlsMessage m, MlsGroup g)
        {
            if (m.WireFormat != MlsMessage.PublicWire) throw new InvalidOperationException("proposal must be a PublicMessage");
            var c = m.Public.Content;
            if (c.Epoch != g.Epoch) throw new InvalidOperationException("proposal for epoch " + c.Epoch + ", we're at " + g.Epoch);
            if (c.ContentType != FramedContent.ProposalType || c.Sender.Type != Sender.External) throw new InvalidOperationException("proposal must come from the voice gateway");
            if (c.Proposal.Type == Proposal.Add)
            {
                var cred = c.Proposal.KeyPackage.LeafNode.Credential;
                if (cred.Type != 1 || !Members.Contains(FromBigEndian(cred.Identity))) throw new InvalidOperationException("add proposal for someone not in the call");
            }
            else if (c.Proposal.Type != Proposal.Remove) throw new InvalidOperationException("unexpected proposal type");
        }

        void OnCommit(byte[] payload)
        {
            int transitionId = -1;
            try
            {
                var r = new TlsReader(payload);
                transitionId = r.U16();
                var commitBytes = r.Raw(r.Remaining);
                if (withProposals == null)
                {
                    if (current == null) { Say("commit while resetting, ignored"); return; }
                    throw new InvalidOperationException("commit without queued proposals");
                }
                bool ours = outboundCommit != null && Bytes.Equal(commitBytes, outboundCommit);
                Say("commit announced for transition " + transitionId + (ours ? " (ours)" : " (someone else's)"));
                if (current == null && !ours) throw new InvalidOperationException("someone else's commit before our welcome");
                var msg = MlsMessage.Parse(commitBytes);
                if (msg.WireFormat != MlsMessage.PublicWire || !Bytes.Equal(msg.Public.Content.GroupId, groupId)) throw new InvalidOperationException("commit for another group");
                foreach (var por in msg.Public.Content.Commit.Proposals)
                    if (por.Reference == null) throw new InvalidOperationException("commit with an inline proposal");
                var next = ours ? outboundNext : withProposals.HandleCommit(msg);
                if (next.Removed) throw new InvalidOperationException("the commit removed us");
                Adopt(next, transitionId);
            }
            catch (Exception ex)
            {
                Say("commit failed: " + ex.Message);
                RecoverFromInvalid(transitionId);
            }
        }

        void OnWelcome(byte[] payload)
        {
            int transitionId = -1;
            try
            {
                var r = new TlsReader(payload);
                transitionId = r.U16();
                var welcome = Welcome.Read(r);
                r.ExpectEnd();
                Say("welcome received for transition " + transitionId);
                if (current != null) throw new InvalidOperationException("welcome after we already have a group");
                if (joinKeyPackage == null || externalSender == null) throw new InvalidOperationException("welcome before we're ready to join");
                var g = MlsGroup.Join(welcome, joinKeyPackage, joinInitPriv, selfLeafPriv, sigPriv, null);
                if (!Bytes.Equal(g.GroupId, groupId)) throw new InvalidOperationException("welcome to another group");
                var senders = g.ExternalSenders();
                if (senders.Count != 1 || !Bytes.Equal(senders[0].Bytes(), externalSender.Bytes())) throw new InvalidOperationException("welcome names another voice gateway");
                foreach (var m in g.Members())
                    if (m.Value.Credential.Type != 1 || !Members.Contains(FromBigEndian(m.Value.Credential.Identity))) throw new InvalidOperationException("welcome lists someone not in the call");
                Adopt(g, transitionId);
            }
            catch (Exception ex)
            {
                Say("welcome failed: " + ex.Message);
                RecoverFromInvalid(transitionId);
            }
        }

        // A new epoch: receivers take its keys at once (keeping the old ones for 10 s), our own
        // sending switches when the gateway executes the transition.
        void Adopt(MlsGroup next, int transitionId)
        {
            current = next;
            pendingGroup = withProposals = outboundNext = null;
            outboundCommit = null;
            joinKeyPackage = null;
            var now = Now();
            var seen = new HashSet<ulong>();
            foreach (var m in next.Members())
            {
                ulong uid = FromBigEndian(m.Value.Credential.Identity);
                if (!seen.Add(uid)) throw new InvalidOperationException("duplicate identity in the group");
                var ratchet = new HashRatchet(next.Export(MediaKeyLabel, LittleEndian(uid), 16));
                if (uid == selfId) { pendingEncryptor = new FrameEncryptor(ratchet); continue; }
                List<FrameDecryptor> list;
                if (!decryptors.TryGetValue(uid, out list)) decryptors[uid] = list = new List<FrameDecryptor>();
                foreach (var old in list) if (old.Expiry > now + TimeSpan.FromSeconds(10)) old.Expiry = now + TimeSpan.FromSeconds(10);
                list.Insert(0, new FrameDecryptor(ratchet, now));
            }
            Say("epoch " + next.Epoch + " with " + seen.Count + " members; privacy code " + PrivacyCode);
            if (transitionId == 0) Execute(ProtocolVersion);
            else
            {
                pendingTransitionId = transitionId;
                pendingTransitionVersion = ProtocolVersion;
                Raise(23, Json.O("transition_id", (long)transitionId));
            }
            if (StateChanged != null) StateChanged();
        }

        void RecoverFromInvalid(int transitionId)
        {
            if (transitionId >= 0) Raise(31, Json.O("transition_id", (long)transitionId));
            Reinit();
        }

        public void OnPrepareTransition(int transitionId, int protocolVersion)
        {
            lock (sync)
            {
                pendingTransitionId = transitionId;
                pendingTransitionVersion = protocolVersion;
                Say("prepare transition " + transitionId + " to version " + protocolVersion);
                if (transitionId == 0) { Execute(protocolVersion); return; }
                if (protocolVersion == 0) passthroughUntil = DateTime.MaxValue; // others may stop encrypting first
                Raise(23, Json.O("transition_id", (long)transitionId));
            }
        }

        public void OnExecuteTransition(int transitionId)
        {
            lock (sync)
            {
                if (pendingTransitionId != transitionId) { Say("execute for unknown transition " + transitionId); return; }
                Say("executing transition " + transitionId);
                Execute(pendingTransitionVersion);
            }
        }

        void Execute(int version)
        {
            int before = ProtocolVersion;
            ProtocolVersion = version;
            pendingTransitionId = null;
            if (version == 0)
            {
                encryptor = null;
                passthroughUntil = DateTime.MaxValue;
            }
            else
            {
                if (pendingEncryptor != null) { encryptor = pendingEncryptor; pendingEncryptor = null; }
                // People switching on encryption may still send a few plain frames.
                passthroughUntil = before == 0 ? Now() + TimeSpan.FromSeconds(10) : DateTime.MinValue;
            }
            if (StateChanged != null) StateChanged();
        }

        // Op 24: epoch 1 means start over with a brand new group.
        public void OnPrepareEpoch(int epoch, int protocolVersion)
        {
            lock (sync)
            {
                Say("prepare epoch " + epoch + ", version " + protocolVersion);
                if (epoch != 1) return;
                ProtocolVersion = protocolVersion;
                Reinit();
            }
        }

        // ---- media ----

        // An Opus frame from us: DAVE-encrypted once we're in a group, else sent as it is.
        public byte[] Encrypt(byte[] frame)
        {
            lock (sync)
            {
                if (ProtocolVersion == 0 || encryptor == null) return frame;
                return encryptor.Encrypt(frame);
            }
        }

        // A frame from someone else; null if it can't be trusted.
        public byte[] Decrypt(ulong userId, byte[] frame)
        {
            lock (sync)
            {
                if (frame.Length == 3 && frame[0] == 0xF8 && frame[1] == 0xFF && frame[2] == 0xFE) return frame;
                var parsed = DaveFrame.Parse(frame);
                var now = Now();
                if (parsed == null) return ProtocolVersion == 0 || now < passthroughUntil ? frame : null;
                List<FrameDecryptor> list;
                if (!decryptors.TryGetValue(userId, out list)) return null;
                list.RemoveAll(d => d.Expiry < now);
                foreach (var d in list)
                {
                    var plain = d.Decrypt(parsed, frame, now);
                    if (plain != null) return plain;
                }
                return null;
            }
        }
    }

    // MLS-style sender ratchet (RFC 9420 9.1): one key per generation, derived on demand.
    public sealed class HashRatchet
    {
        byte[] nextSecret;
        uint nextGeneration;
        readonly Dictionary<uint, byte[]> keys = new Dictionary<uint, byte[]>();

        public HashRatchet(byte[] baseSecret) { nextSecret = baseSecret; }

        public byte[] Get(uint generation)
        {
            byte[] key;
            if (keys.TryGetValue(generation, out key)) return key;
            if (generation < nextGeneration) return null; // erased
            while (nextGeneration <= generation)
            {
                keys[nextGeneration] = Mls.DeriveTreeSecret(nextSecret, "key", nextGeneration, 16);
                nextSecret = Mls.DeriveTreeSecret(nextSecret, "secret", nextGeneration, Mls.Nh);
                nextGeneration++;
            }
            return keys[generation];
        }

        public void Erase(uint generation) { keys.Remove(generation); }
    }

    // The DAVE frame trailer: 8-byte tag, ULEB128 nonce, unencrypted ranges (none for Opus),
    // a size byte and the 0xFAFA marker.
    public sealed class DaveFrame
    {
        public int CiphertextLength;
        public uint Nonce;

        public static DaveFrame Parse(byte[] f)
        {
            int n = f.Length;
            if (n < 11 || f[n - 1] != 0xFA || f[n - 2] != 0xFA) return null;
            int supplemental = f[n - 3];
            if (supplemental < 11 || supplemental > n) return null;
            int tagAt = n - supplemental;
            int pos = tagAt + 8, end = n - 3;
            ulong nonce = 0;
            int shift = 0;
            while (true)
            {
                if (pos >= end || shift > 28) return null;
                byte b = f[pos++];
                nonce |= (ulong)(b & 0x7F) << shift;
                shift += 7;
                if ((b & 0x80) == 0) break;
            }
            if (pos != end || nonce > uint.MaxValue) return null; // unencrypted ranges aren't used for audio
            return new DaveFrame { CiphertextLength = tagAt, Nonce = (uint)nonce };
        }

        public static byte[] Leb128(uint v)
        {
            var list = new List<byte>();
            while (v >= 0x80) { list.Add((byte)(0x80 | (v & 0x7F))); v >>= 7; }
            list.Add((byte)v);
            return list.ToArray();
        }

        public static byte[] FullNonce(uint truncated)
        {
            var n = new byte[12];
            BitConverter.GetBytes(truncated).CopyTo(n, 8); // little-endian, as libdave copies it
            return n;
        }

        // Generations are the nonce's top byte; keep counting past 255 when the nonce wraps.
        public static uint WrappedGeneration(uint oldest, uint generation)
        {
            uint remainder = oldest % 256;
            uint factor = oldest / 256 + (generation < remainder ? 1u : 0u);
            return factor * 256 + generation;
        }
    }

    public sealed class FrameEncryptor
    {
        readonly HashRatchet ratchet;
        uint nonce, generation;
        AesGcm cipher;

        public FrameEncryptor(HashRatchet ratchet) { this.ratchet = ratchet; }

        public byte[] Encrypt(byte[] frame)
        {
            nonce++;
            uint gen = DaveFrame.WrappedGeneration(generation, nonce >> 24);
            if (gen != generation || cipher == null)
            {
                generation = gen;
                if (cipher != null) cipher.Dispose();
                cipher = new AesGcm(ratchet.Get(generation));
            }
            var sealedFrame = cipher.Seal(DaveFrame.FullNonce(nonce), frame, null, 8);
            var leb = DaveFrame.Leb128(nonce);
            var w = new TlsWriter().Raw(sealedFrame).Raw(leb).U8(8 + leb.Length + 3).U8(0xFA).U8(0xFA);
            return w.ToArray();
        }
    }

    // One sender's keys for one epoch, with libdave's limits on generations and replays.
    public sealed class FrameDecryptor
    {
        const int MaxGenerationGap = 250, MaxMissingNonces = 1000, MaxFramesPerSecond = 50 + 2 * 60;
        readonly HashRatchet ratchet;
        readonly DateTime created;
        public DateTime Expiry = DateTime.MaxValue;
        readonly Dictionary<uint, AesGcm> ciphers = new Dictionary<uint, AesGcm>();
        // Generations are never retired here: each lasts 2^24 frames (about 93 hours of audio).
        const uint oldestGeneration = 0;
        uint newestGeneration;
        ulong? newestNonce;
        readonly LinkedList<ulong> missing = new LinkedList<ulong>();

        public FrameDecryptor(HashRatchet ratchet, DateTime now) { this.ratchet = ratchet; created = now; }

        public byte[] Decrypt(DaveFrame f, byte[] frame, DateTime now)
        {
            uint gen = DaveFrame.WrappedGeneration(oldestGeneration, f.Nonce >> 24);
            if (gen < oldestGeneration || gen > newestGeneration + MaxGenerationGap) return null;
            long maxGenerations = ((long)MaxFramesPerSecond * (long)Math.Max(1, (now - created).TotalSeconds)) >> 24;
            if (gen > maxGenerations) return null;
            ulong big = ((ulong)gen << 24) | (f.Nonce & 0xFFFFFF);
            ulong expected = newestNonce.HasValue ? newestNonce.Value + 1 : 0;
            if (big < expected && !missing.Contains(big)) return null; // a replay, or too old

            AesGcm cipher;
            if (!ciphers.TryGetValue(gen, out cipher))
            {
                var key = ratchet.Get(gen);
                if (key == null) return null;
                ciphers[gen] = cipher = new AesGcm(key);
            }
            var plain = cipher.Open(DaveFrame.FullNonce(f.Nonce), frame, 0, f.CiphertextLength + 8, null, 8);
            if (plain == null) return null;

            if (big >= expected)
            {
                ulong gap = Math.Min(big - expected, MaxMissingNonces);
                while (missing.Count > 0 && (ulong)missing.Count + gap > MaxMissingNonces) missing.RemoveFirst();
                for (ulong i = big - gap; i < big; i++) missing.AddLast(i);
                newestNonce = big;
            }
            else missing.Remove(big);
            if (gen > newestGeneration) newestGeneration = gen;
            return plain;
        }
    }
}
