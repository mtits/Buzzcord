using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace Buzzcord
{
    // MLS (RFC 9420) for ciphersuite 2, MLS_128_DHKEMP256_AES128GCM_SHA256_P256, the only one DAVE
    // uses. This file: the suite's crypto helpers and the wire structs. MlsTree.cs has the ratchet
    // tree, MlsGroup.cs the group state machine.
    public static class Mls
    {
        public const int CipherSuite = 2, Version = 1;
        public const int Nh = 32, Nk = 16, Nn = 12;
        public const int ExtRatchetTree = 2, ExtExternalSenders = 5;
        public static readonly byte[] Empty = new byte[0];

        public static byte[] Hash(byte[] data) { using (var s = SHA256.Create()) return s.ComputeHash(data); }
        public static byte[] Mac(byte[] key, byte[] data) { using (var h = new HMACSHA256(key)) return h.ComputeHash(data); }
        public static byte[] Utf8(string s) { return Encoding.UTF8.GetBytes(s); }
        static readonly byte[] Prefix = Utf8("MLS 1.0 ");

        public static byte[] Random(int n)
        {
            var b = new byte[n];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(b);
            return b;
        }

        public static byte[] RefHash(string label, byte[] value)
        {
            return Hash(new TlsWriter().Opaque(Utf8(label)).Opaque(value).ToArray());
        }

        public static byte[] ExpandWithLabel(byte[] secret, byte[] label, byte[] context, int length)
        {
            var info = new TlsWriter().U16(length).Opaque(Bytes.Concat(Prefix, label)).Opaque(context).ToArray();
            return Hkdf.Expand(secret, info, length);
        }

        public static byte[] ExpandWithLabel(byte[] secret, string label, byte[] context, int length)
        {
            return ExpandWithLabel(secret, Utf8(label), context, length);
        }

        public static byte[] DeriveSecret(byte[] secret, string label) { return ExpandWithLabel(secret, label, Empty, Nh); }
        public static byte[] DeriveSecret(byte[] secret, byte[] label) { return ExpandWithLabel(secret, label, Empty, Nh); }

        public static byte[] DeriveTreeSecret(byte[] secret, string label, uint generation, int length)
        {
            return ExpandWithLabel(secret, label, new TlsWriter().U32(generation).ToArray(), length);
        }

        static byte[] SignContent(string label, byte[] content)
        {
            return new TlsWriter().Opaque(Bytes.Concat(Prefix, Utf8(label))).Opaque(content).ToArray();
        }

        // Signatures travel DER-encoded in MLS.
        public static byte[] SignWithLabel(byte[] priv, string label, byte[] content)
        {
            return P256.ToDer(P256.Sign(priv, SignContent(label, content)));
        }

        public static bool VerifyWithLabel(byte[] pub, string label, byte[] content, byte[] signature)
        {
            var rs = signature != null ? P256.FromDer(signature) : null;
            return rs != null && P256.Verify(pub, SignContent(label, content), rs);
        }

        static byte[] EncryptContext(string label, byte[] context)
        {
            return new TlsWriter().Opaque(Bytes.Concat(Prefix, Utf8(label))).Opaque(context).ToArray();
        }

        public static HpkeCiphertext EncryptWithLabel(byte[] pub, string label, byte[] context, byte[] plaintext)
        {
            byte[] enc;
            var ct = Hpke.Seal(pub, EncryptContext(label, context), Empty, plaintext, out enc);
            return new HpkeCiphertext { KemOutput = enc, Ciphertext = ct };
        }

        public static byte[] DecryptWithLabel(byte[] priv, string label, byte[] context, HpkeCiphertext ct)
        {
            return Hpke.Open(ct.KemOutput, priv, EncryptContext(label, context), Empty, ct.Ciphertext);
        }

        public static void DeriveKeyPair(byte[] secret, out byte[] priv, out byte[] pub) { Hpke.DeriveKeyPair(secret, out priv, out pub); }

        public static byte[] Zeros() { return new byte[Nh]; }

        // Opaque vectors of structs.
        public static void WriteList<T>(TlsWriter w, List<T> items, Action<TlsWriter, T> write)
        {
            w.Vector(inner => { foreach (var x in items) write(inner, x); });
        }

        public static List<T> ReadList<T>(TlsReader r, Func<TlsReader, T> read)
        {
            var v = r.Vector();
            var list = new List<T>();
            while (!v.AtEnd) list.Add(read(v));
            return list;
        }

        public static List<int> ReadU16List(TlsReader r) { return ReadList(r, x => x.U16()); }
        public static void WriteU16List(TlsWriter w, List<int> l) { WriteList(w, l, (x, v) => x.U16(v)); }

        public static byte[] Serialize(Action<TlsWriter> write)
        {
            var w = new TlsWriter();
            write(w);
            return w.ToArray();
        }
    }

    public sealed class HpkeCiphertext
    {
        public byte[] KemOutput, Ciphertext;
        public void Write(TlsWriter w) { w.Opaque(KemOutput).Opaque(Ciphertext); }
        public static HpkeCiphertext Read(TlsReader r) { return new HpkeCiphertext { KemOutput = r.Opaque(), Ciphertext = r.Opaque() }; }
    }

    public sealed class Credential
    {
        public int Type = 1;              // 1 basic, 2 x509
        public byte[] Identity;
        public List<byte[]> Certificates;

        public static Credential Basic(byte[] identity) { return new Credential { Identity = identity }; }

        public void Write(TlsWriter w)
        {
            w.U16(Type);
            if (Type == 1) w.Opaque(Identity);
            else if (Type == 2) Mls.WriteList(w, Certificates, (x, c) => x.Opaque(c));
            else throw new FormatException("unknown credential type");
        }

        public static Credential Read(TlsReader r)
        {
            var c = new Credential { Type = r.U16() };
            if (c.Type == 1) c.Identity = r.Opaque();
            else if (c.Type == 2) c.Certificates = Mls.ReadList(r, x => x.Opaque());
            else throw new FormatException("unknown credential type " + c.Type);
            return c;
        }

        public byte[] Bytes() { return Mls.Serialize(Write); }
    }

    public sealed class Capabilities
    {
        public List<int> Versions = new List<int> { 1 }, CipherSuites = new List<int> { Mls.CipherSuite },
            Extensions = new List<int>(), Proposals = new List<int>(), Credentials = new List<int> { 1 };

        public void Write(TlsWriter w)
        {
            Mls.WriteU16List(w, Versions);
            Mls.WriteU16List(w, CipherSuites);
            Mls.WriteU16List(w, Extensions);
            Mls.WriteU16List(w, Proposals);
            Mls.WriteU16List(w, Credentials);
        }

        public static Capabilities Read(TlsReader r)
        {
            return new Capabilities
            {
                Versions = Mls.ReadU16List(r), CipherSuites = Mls.ReadU16List(r), Extensions = Mls.ReadU16List(r),
                Proposals = Mls.ReadU16List(r), Credentials = Mls.ReadU16List(r)
            };
        }
    }

    public sealed class Extension
    {
        public int Type;
        public byte[] Data;
        public void Write(TlsWriter w) { w.U16(Type).Opaque(Data); }
        public static Extension Read(TlsReader r) { return new Extension { Type = r.U16(), Data = r.Opaque() }; }

        public static void WriteAll(TlsWriter w, List<Extension> list) { Mls.WriteList(w, list ?? new List<Extension>(), (x, e) => e.Write(x)); }
        public static List<Extension> ReadAll(TlsReader r) { return Mls.ReadList(r, Read); }

        public static byte[] Find(List<Extension> list, int type)
        {
            if (list != null) foreach (var e in list) if (e.Type == type) return e.Data;
            return null;
        }
    }

    public sealed class ExternalSender
    {
        public byte[] SignatureKey;
        public Credential Credential;
        public void Write(TlsWriter w) { w.Opaque(SignatureKey); Credential.Write(w); }
        public static ExternalSender Read(TlsReader r) { return new ExternalSender { SignatureKey = r.Opaque(), Credential = Credential.Read(r) }; }
        public byte[] Bytes() { return Mls.Serialize(Write); }
    }

    public sealed class LeafNode
    {
        public const int FromKeyPackage = 1, FromUpdate = 2, FromCommit = 3;
        public byte[] EncryptionKey, SignatureKey;
        public Credential Credential;
        public Capabilities Capabilities = new Capabilities();
        public int Source = FromKeyPackage;
        public ulong NotBefore, NotAfter = ulong.MaxValue;
        public byte[] ParentHash = Mls.Empty;
        public List<Extension> Extensions = new List<Extension>();
        public byte[] Signature;

        void WriteBody(TlsWriter w)
        {
            w.Opaque(EncryptionKey).Opaque(SignatureKey);
            Credential.Write(w);
            Capabilities.Write(w);
            w.U8(Source);
            if (Source == FromKeyPackage) w.U64(NotBefore).U64(NotAfter);
            else if (Source == FromCommit) w.Opaque(ParentHash);
            Extension.WriteAll(w, Extensions);
        }

        public void Write(TlsWriter w) { WriteBody(w); w.Opaque(Signature); }
        public byte[] Bytes() { return Mls.Serialize(Write); }

        public static LeafNode Read(TlsReader r)
        {
            var n = new LeafNode { EncryptionKey = r.Opaque(), SignatureKey = r.Opaque(), Credential = Credential.Read(r), Capabilities = Capabilities.Read(r) };
            n.Source = r.U8();
            if (n.Source == FromKeyPackage) { n.NotBefore = r.U64(); n.NotAfter = r.U64(); }
            else if (n.Source == FromCommit) n.ParentHash = r.Opaque();
            else if (n.Source != FromUpdate) throw new FormatException("unknown leaf node source");
            n.Extensions = Extension.ReadAll(r);
            n.Signature = r.Opaque();
            return n;
        }

        byte[] Tbs(byte[] groupId, uint leafIndex)
        {
            var w = new TlsWriter();
            WriteBody(w);
            if (Source != FromKeyPackage) w.Opaque(groupId).U32(leafIndex);
            return w.ToArray();
        }

        public void Sign(byte[] sigPriv, byte[] groupId, uint leafIndex) { Signature = Mls.SignWithLabel(sigPriv, "LeafNodeTBS", Tbs(groupId, leafIndex)); }
        public bool Verify(byte[] groupId, uint leafIndex) { return Mls.VerifyWithLabel(SignatureKey, "LeafNodeTBS", Tbs(groupId, leafIndex), Signature); }

        public LeafNode Copy() { return Read(new TlsReader(Bytes())); }
    }

    public sealed class ParentNode
    {
        public byte[] EncryptionKey, ParentHash = Mls.Empty;
        public List<uint> UnmergedLeaves = new List<uint>();

        public void Write(TlsWriter w) { w.Opaque(EncryptionKey).Opaque(ParentHash); Mls.WriteList(w, UnmergedLeaves, (x, v) => x.U32(v)); }
        public static ParentNode Read(TlsReader r) { return new ParentNode { EncryptionKey = r.Opaque(), ParentHash = r.Opaque(), UnmergedLeaves = Mls.ReadList(r, x => x.U32()) }; }
        public ParentNode Copy() { return new ParentNode { EncryptionKey = EncryptionKey, ParentHash = ParentHash, UnmergedLeaves = new List<uint>(UnmergedLeaves) }; }
    }

    public sealed class KeyPackage
    {
        public int Version = Mls.Version, CipherSuite = Mls.CipherSuite;
        public byte[] InitKey;
        public LeafNode LeafNode;
        public List<Extension> Extensions = new List<Extension>();
        public byte[] Signature;

        void WriteBody(TlsWriter w) { w.U16(Version).U16(CipherSuite).Opaque(InitKey); LeafNode.Write(w); Extension.WriteAll(w, Extensions); }
        public void Write(TlsWriter w) { WriteBody(w); w.Opaque(Signature); }
        public byte[] Bytes() { return Mls.Serialize(Write); }

        public static KeyPackage Read(TlsReader r)
        {
            return new KeyPackage { Version = r.U16(), CipherSuite = r.U16(), InitKey = r.Opaque(), LeafNode = LeafNode.Read(r), Extensions = Extension.ReadAll(r), Signature = r.Opaque() };
        }

        public void Sign(byte[] sigPriv) { Signature = Mls.SignWithLabel(sigPriv, "KeyPackageTBS", Mls.Serialize(WriteBody)); }
        public bool Verify() { return Mls.VerifyWithLabel(LeafNode.SignatureKey, "KeyPackageTBS", Mls.Serialize(WriteBody), Signature); }
        public byte[] Ref() { return Mls.RefHash("MLS 1.0 KeyPackage Reference", Bytes()); }
    }

    public sealed class Proposal
    {
        public const int Add = 1, Update = 2, Remove = 3, Psk = 4, ReInit = 5, ExternalInit = 6, GroupContextExtensions = 7;
        public int Type;
        public KeyPackage KeyPackage;          // add
        public LeafNode LeafNode;              // update
        public uint Removed;                   // remove
        public List<Extension> Extensions;     // group context extensions
        public byte[] Raw;                     // psk, reinit, external init: kept as received

        public void Write(TlsWriter w)
        {
            w.U16(Type);
            WriteBody(w);
        }

        public void WriteBody(TlsWriter w)
        {
            switch (Type)
            {
                case Add: KeyPackage.Write(w); break;
                case Update: LeafNode.Write(w); break;
                case Remove: w.U32(Removed); break;
                case GroupContextExtensions: Extension.WriteAll(w, Extensions); break;
                default: w.Raw(Raw); break;
            }
        }

        public static Proposal Read(TlsReader r) { return ReadBody(r, r.U16()); }

        public static Proposal ReadBody(TlsReader r, int type)
        {
            var p = new Proposal { Type = type };
            int start = r.Position;
            switch (p.Type)
            {
                case Add: p.KeyPackage = KeyPackage.Read(r); break;
                case Update: p.LeafNode = LeafNode.Read(r); break;
                case Remove: p.Removed = r.U32(); break;
                case GroupContextExtensions: p.Extensions = Extension.ReadAll(r); break;
                case Psk: ReadPskId(r); p.Raw = r.Since(start); break;
                case ReInit: r.Opaque(); r.U16(); r.U16(); Extension.ReadAll(r); p.Raw = r.Since(start); break;
                case ExternalInit: r.Opaque(); p.Raw = r.Since(start); break;
                default: throw new FormatException("unknown proposal type " + p.Type);
            }
            return p;
        }

        // PreSharedKeyID: external(1) psk_id, or resumption(2) usage, group_id, epoch; then a nonce.
        public static PskId ReadPskId(TlsReader r)
        {
            int start = r.Position;
            var id = new PskId { Type = r.U8() };
            if (id.Type == 1) id.Id = r.Opaque();
            else if (id.Type == 2) { r.U8(); r.Opaque(); r.U64(); }
            else throw new FormatException("unknown PSK type");
            r.Opaque();
            id.Raw = r.Since(start);
            return id;
        }

        public PskId PskId() { return Type == Psk ? ReadPskId(new TlsReader(Raw)) : null; }
    }

    public sealed class PskId
    {
        public int Type;      // 1 external, 2 resumption
        public byte[] Id;     // external PSK id
        public byte[] Raw;    // the whole PreSharedKeyID, as PSKLabel needs it

        // RFC 9420 8.4: chain the PSKs into one psk_secret. Only external PSKs can be looked up.
        public static byte[] Secret(List<PskId> ids, IDictionary<string, byte[]> externalPsks)
        {
            var secret = Mls.Zeros();
            for (int i = 0; i < ids.Count; i++)
            {
                byte[] psk;
                if (ids[i].Type != 1 || externalPsks == null || !externalPsks.TryGetValue(Hex.Encode(ids[i].Id), out psk))
                    throw new NotSupportedException("needs a pre-shared key we don't have");
                var label = new TlsWriter().Raw(ids[i].Raw).U16(i).U16(ids.Count).ToArray();
                var input = Mls.ExpandWithLabel(Hkdf.Extract(Mls.Zeros(), psk), "derived psk", label, Mls.Nh);
                secret = Hkdf.Extract(input, secret);
            }
            return secret;
        }
    }

    public sealed class ProposalOrRef
    {
        public Proposal Proposal;   // by value
        public byte[] Reference;    // or by reference

        public void Write(TlsWriter w)
        {
            if (Proposal != null) { w.U8(1); Proposal.Write(w); }
            else w.U8(2).Opaque(Reference);
        }

        public static ProposalOrRef Read(TlsReader r)
        {
            int t = r.U8();
            if (t == 1) return new ProposalOrRef { Proposal = Proposal.Read(r) };
            if (t == 2) return new ProposalOrRef { Reference = r.Opaque() };
            throw new FormatException("bad ProposalOrRef");
        }
    }

    public sealed class UpdatePathNode
    {
        public byte[] EncryptionKey;
        public List<HpkeCiphertext> EncryptedPathSecret = new List<HpkeCiphertext>();
        public void Write(TlsWriter w) { w.Opaque(EncryptionKey); Mls.WriteList(w, EncryptedPathSecret, (x, c) => c.Write(x)); }
        public static UpdatePathNode Read(TlsReader r) { return new UpdatePathNode { EncryptionKey = r.Opaque(), EncryptedPathSecret = Mls.ReadList(r, HpkeCiphertext.Read) }; }
    }

    public sealed class UpdatePath
    {
        public LeafNode LeafNode;
        public List<UpdatePathNode> Nodes = new List<UpdatePathNode>();
        public void Write(TlsWriter w) { LeafNode.Write(w); Mls.WriteList(w, Nodes, (x, n) => n.Write(x)); }
        public static UpdatePath Read(TlsReader r) { return new UpdatePath { LeafNode = LeafNode.Read(r), Nodes = Mls.ReadList(r, UpdatePathNode.Read) }; }
    }

    public sealed class Commit
    {
        public List<ProposalOrRef> Proposals = new List<ProposalOrRef>();
        public UpdatePath Path;
        public void Write(TlsWriter w) { Mls.WriteList(w, Proposals, (x, p) => p.Write(x)); w.Optional(Path != null, x => Path.Write(x)); }
        public static Commit Read(TlsReader r) { var c = new Commit { Proposals = Mls.ReadList(r, ProposalOrRef.Read) }; if (r.Optional()) c.Path = UpdatePath.Read(r); return c; }
    }

    public sealed class Sender
    {
        public const int Member = 1, External = 2, NewMemberProposal = 3, NewMemberCommit = 4;
        public int Type;
        public uint Index;
        public void Write(TlsWriter w) { w.U8(Type); if (Type == Member || Type == External) w.U32(Index); }
        public static Sender Read(TlsReader r) { var s = new Sender { Type = r.U8() }; if (s.Type == Member || s.Type == External) s.Index = r.U32(); else if (s.Type < 1 || s.Type > 4) throw new FormatException("bad sender"); return s; }
    }

    public sealed class FramedContent
    {
        public const int Application = 1, ProposalType = 2, CommitType = 3;
        public byte[] GroupId;
        public ulong Epoch;
        public Sender Sender;
        public byte[] AuthenticatedData = Mls.Empty;
        public int ContentType;
        public byte[] ApplicationData;
        public Proposal Proposal;
        public Commit Commit;

        public void Write(TlsWriter w)
        {
            w.Opaque(GroupId).U64(Epoch);
            Sender.Write(w);
            w.Opaque(AuthenticatedData).U8(ContentType);
            if (ContentType == Application) w.Opaque(ApplicationData);
            else if (ContentType == ProposalType) Proposal.Write(w);
            else if (ContentType == CommitType) Commit.Write(w);
        }

        public static FramedContent Read(TlsReader r)
        {
            var c = new FramedContent { GroupId = r.Opaque(), Epoch = r.U64(), Sender = Sender.Read(r), AuthenticatedData = r.Opaque(), ContentType = r.U8() };
            if (c.ContentType == Application) c.ApplicationData = r.Opaque();
            else if (c.ContentType == ProposalType) c.Proposal = Proposal.Read(r);
            else if (c.ContentType == CommitType) c.Commit = Commit.Read(r);
            else throw new FormatException("bad content type");
            return c;
        }
    }

    // A PublicMessage: content, its signature (and confirmation tag for commits), and a membership tag
    // from members.
    public sealed class PublicMessage
    {
        public FramedContent Content;
        public byte[] Signature, ConfirmationTag, MembershipTag;

        public void WriteAuth(TlsWriter w)
        {
            w.Opaque(Signature);
            if (Content.ContentType == FramedContent.CommitType) w.Opaque(ConfirmationTag);
        }

        public void Write(TlsWriter w)
        {
            Content.Write(w);
            WriteAuth(w);
            if (Content.Sender.Type == Sender.Member) w.Opaque(MembershipTag);
        }

        public static PublicMessage Read(TlsReader r)
        {
            var m = new PublicMessage { Content = FramedContent.Read(r) };
            m.Signature = r.Opaque();
            if (m.Content.ContentType == FramedContent.CommitType) m.ConfirmationTag = r.Opaque();
            if (m.Content.Sender.Type == Sender.Member) m.MembershipTag = r.Opaque();
            return m;
        }

        // FramedContentTBS; the group context is included for member and new-member-commit senders.
        public byte[] Tbs(byte[] groupContext)
        {
            var w = new TlsWriter().U16(Mls.Version).U16(MlsMessage.PublicWire);
            Content.Write(w);
            if (Content.Sender.Type == Sender.Member || Content.Sender.Type == Sender.NewMemberCommit) w.Raw(groupContext);
            return w.ToArray();
        }

        public byte[] AuthenticatedContent()
        {
            var w = new TlsWriter().U16(MlsMessage.PublicWire);
            Content.Write(w);
            WriteAuth(w);
            return w.ToArray();
        }

        public byte[] ComputeMembershipTag(byte[] membershipKey, byte[] groupContext)
        {
            var w = new TlsWriter().Raw(Tbs(groupContext));
            WriteAuth(w);
            return Mls.Mac(membershipKey, w.ToArray());
        }

        public void Sign(byte[] sigPriv, byte[] groupContext) { Signature = Mls.SignWithLabel(sigPriv, "FramedContentTBS", Tbs(groupContext)); }
        public bool VerifySignature(byte[] sigPub, byte[] groupContext) { return Mls.VerifyWithLabel(sigPub, "FramedContentTBS", Tbs(groupContext), Signature); }

        public byte[] ProposalRef() { return Mls.RefHash("MLS 1.0 Proposal Reference", AuthenticatedContent()); }

        // ConfirmedTranscriptHashInput for a commit.
        public byte[] ConfirmedInput()
        {
            var w = new TlsWriter().U16(MlsMessage.PublicWire);
            Content.Write(w);
            return w.Opaque(Signature).ToArray();
        }
    }

    public sealed class GroupContext
    {
        public int Version = Mls.Version, CipherSuite = Mls.CipherSuite;
        public byte[] GroupId, TreeHash, ConfirmedTranscriptHash;
        public ulong Epoch;
        public List<Extension> Extensions = new List<Extension>();

        public void Write(TlsWriter w)
        {
            w.U16(Version).U16(CipherSuite).Opaque(GroupId).U64(Epoch).Opaque(TreeHash).Opaque(ConfirmedTranscriptHash);
            Extension.WriteAll(w, Extensions);
        }

        public static GroupContext Read(TlsReader r)
        {
            return new GroupContext { Version = r.U16(), CipherSuite = r.U16(), GroupId = r.Opaque(), Epoch = r.U64(), TreeHash = r.Opaque(), ConfirmedTranscriptHash = r.Opaque(), Extensions = Extension.ReadAll(r) };
        }

        public byte[] Bytes() { return Mls.Serialize(Write); }
    }

    public sealed class GroupInfo
    {
        public GroupContext Context;
        public List<Extension> Extensions = new List<Extension>();
        public byte[] ConfirmationTag, Signature;
        public uint Signer;

        void WriteBody(TlsWriter w) { Context.Write(w); Extension.WriteAll(w, Extensions); w.Opaque(ConfirmationTag).U32(Signer); }
        public void Write(TlsWriter w) { WriteBody(w); w.Opaque(Signature); }
        public byte[] Bytes() { return Mls.Serialize(Write); }

        public static GroupInfo Read(TlsReader r)
        {
            return new GroupInfo { Context = GroupContext.Read(r), Extensions = Extension.ReadAll(r), ConfirmationTag = r.Opaque(), Signer = r.U32(), Signature = r.Opaque() };
        }

        public void Sign(byte[] sigPriv) { Signature = Mls.SignWithLabel(sigPriv, "GroupInfoTBS", Mls.Serialize(WriteBody)); }
        public bool Verify(byte[] sigPub) { return Mls.VerifyWithLabel(sigPub, "GroupInfoTBS", Mls.Serialize(WriteBody), Signature); }
    }

    public sealed class GroupSecrets
    {
        public byte[] JoinerSecret, PathSecret;
        public List<PskId> Psks = new List<PskId>();

        public void Write(TlsWriter w)
        {
            w.Opaque(JoinerSecret).Optional(PathSecret != null, x => x.Opaque(PathSecret));
            Mls.WriteList(w, Psks, (x, p) => x.Raw(p.Raw));
        }

        public static GroupSecrets Read(TlsReader r)
        {
            var g = new GroupSecrets { JoinerSecret = r.Opaque() };
            if (r.Optional()) g.PathSecret = r.Opaque();
            g.Psks = Mls.ReadList(r, Proposal.ReadPskId);
            return g;
        }
    }

    public sealed class EncryptedGroupSecrets
    {
        public byte[] NewMember;
        public HpkeCiphertext Secrets;
        public void Write(TlsWriter w) { w.Opaque(NewMember); Secrets.Write(w); }
        public static EncryptedGroupSecrets Read(TlsReader r) { return new EncryptedGroupSecrets { NewMember = r.Opaque(), Secrets = HpkeCiphertext.Read(r) }; }
    }

    public sealed class Welcome
    {
        public int CipherSuite = Mls.CipherSuite;
        public List<EncryptedGroupSecrets> Secrets = new List<EncryptedGroupSecrets>();
        public byte[] EncryptedGroupInfo;
        public void Write(TlsWriter w) { w.U16(CipherSuite); Mls.WriteList(w, Secrets, (x, s) => s.Write(x)); w.Opaque(EncryptedGroupInfo); }
        public static Welcome Read(TlsReader r) { return new Welcome { CipherSuite = r.U16(), Secrets = Mls.ReadList(r, EncryptedGroupSecrets.Read), EncryptedGroupInfo = r.Opaque() }; }
        public byte[] Bytes() { return Mls.Serialize(Write); }
    }

    // MLSMessage: version, wire format, then the message itself.
    public sealed class MlsMessage
    {
        public const int PublicWire = 1, PrivateWire = 2, WelcomeWire = 3, GroupInfoWire = 4, KeyPackageWire = 5;
        public int WireFormat;
        public PublicMessage Public;
        public Welcome Welcome;
        public GroupInfo GroupInfo;
        public KeyPackage KeyPackage;
        public byte[] PrivateRaw;

        public void Write(TlsWriter w)
        {
            w.U16(Mls.Version).U16(WireFormat);
            switch (WireFormat)
            {
                case PublicWire: Public.Write(w); break;
                case PrivateWire: w.Raw(PrivateRaw); break;
                case WelcomeWire: Welcome.Write(w); break;
                case GroupInfoWire: GroupInfo.Write(w); break;
                case KeyPackageWire: KeyPackage.Write(w); break;
            }
        }

        public byte[] Bytes() { return Mls.Serialize(Write); }

        public static MlsMessage Read(TlsReader r)
        {
            if (r.U16() != Mls.Version) throw new FormatException("not MLS 1.0");
            var m = new MlsMessage { WireFormat = r.U16() };
            int start = r.Position;
            switch (m.WireFormat)
            {
                case PublicWire: m.Public = PublicMessage.Read(r); break;
                case PrivateWire:
                    r.Opaque(); r.U64(); r.U8(); r.Opaque(); r.Opaque(); r.Opaque();
                    m.PrivateRaw = r.Since(start);
                    break;
                case WelcomeWire: m.Welcome = Welcome.Read(r); break;
                case GroupInfoWire: m.GroupInfo = GroupInfo.Read(r); break;
                case KeyPackageWire: m.KeyPackage = KeyPackage.Read(r); break;
                default: throw new FormatException("unknown wire format " + m.WireFormat);
            }
            return m;
        }

        public static MlsMessage Parse(byte[] data)
        {
            var r = new TlsReader(data);
            var m = Read(r);
            r.ExpectEnd();
            return m;
        }
    }
}
