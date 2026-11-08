using System;
using System.IO;
using System.Text;

namespace Buzzcord
{
    // The TLS presentation language as MLS uses it (RFC 9420 section 2.1): big-endian integers and
    // vectors prefixed with a variable-length (1, 2 or 4 byte) length.
    public sealed class TlsWriter
    {
        readonly MemoryStream s = new MemoryStream();

        public TlsWriter U8(int v) { s.WriteByte((byte)v); return this; }
        public TlsWriter U16(int v) { s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v); return this; }
        public TlsWriter U32(uint v) { for (int i = 3; i >= 0; i--) s.WriteByte((byte)(v >> (i * 8))); return this; }
        public TlsWriter U64(ulong v) { for (int i = 7; i >= 0; i--) s.WriteByte((byte)(v >> (i * 8))); return this; }
        public TlsWriter Raw(byte[] b) { s.Write(b, 0, b.Length); return this; }

        public TlsWriter VarInt(uint v)
        {
            if (v < 0x40) return U8((int)v);
            if (v < 0x4000) return U16((int)(v | 0x4000));
            if (v < 0x40000000) return U32(v | 0x80000000);
            throw new ArgumentException("vector too long for MLS");
        }

        // opaque<V>: length then bytes.
        public TlsWriter Opaque(byte[] b)
        {
            b = b ?? new byte[0];
            VarInt((uint)b.Length);
            return Raw(b);
        }

        public TlsWriter Opaque(string utf8) { return Opaque(Encoding.UTF8.GetBytes(utf8)); }

        // A vector of structs: written into its own buffer, then length-prefixed.
        public TlsWriter Vector(Action<TlsWriter> body)
        {
            var inner = new TlsWriter();
            body(inner);
            return Opaque(inner.ToArray());
        }

        // optional<T>: a presence byte, then the value.
        public TlsWriter Optional(bool present, Action<TlsWriter> body)
        {
            U8(present ? 1 : 0);
            if (present) body(this);
            return this;
        }

        public byte[] ToArray() { return s.ToArray(); }
        public int Length { get { return (int)s.Length; } }
    }

    public sealed class TlsReader
    {
        readonly byte[] b;
        int pos;
        readonly int end;

        public TlsReader(byte[] data) : this(data, 0, data.Length) { }

        public TlsReader(byte[] data, int offset, int count)
        {
            b = data;
            pos = offset;
            end = offset + count;
        }

        public int Position { get { return pos; } }
        public int Remaining { get { return end - pos; } }
        public bool AtEnd { get { return pos == end; } }

        void Need(int n) { if (n < 0 || end - pos < n) throw new FormatException("MLS message ends early"); }

        public int U8() { Need(1); return b[pos++]; }
        public int U16() { Need(2); int v = (b[pos] << 8) | b[pos + 1]; pos += 2; return v; }

        public uint U32()
        {
            Need(4);
            uint v = 0;
            for (int i = 0; i < 4; i++) v = (v << 8) | b[pos++];
            return v;
        }

        public ulong U64()
        {
            Need(8);
            ulong v = 0;
            for (int i = 0; i < 8; i++) v = (v << 8) | b[pos++];
            return v;
        }

        public byte[] Raw(int n)
        {
            Need(n);
            var r = new byte[n];
            Buffer.BlockCopy(b, pos, r, 0, n);
            pos += n;
            return r;
        }

        // Rejects non-minimal encodings, as RFC 9420 requires.
        public uint VarInt()
        {
            Need(1);
            int prefix = b[pos] >> 6;
            uint v;
            switch (prefix)
            {
                case 0: v = (uint)U8(); return v;
                case 1: v = (uint)U16() & 0x3FFF; if (v < 0x40) throw new FormatException("non-minimal length"); return v;
                case 2: v = U32() & 0x3FFFFFFF; if (v < 0x4000) throw new FormatException("non-minimal length"); return v;
                default: throw new FormatException("invalid length prefix");
            }
        }

        public byte[] Opaque() { return Raw((int)VarInt()); }

        // A length-prefixed vector, read by its own reader.
        public TlsReader Vector()
        {
            int n = (int)VarInt();
            Need(n);
            var r = new TlsReader(b, pos, n);
            pos += n;
            return r;
        }

        public bool Optional()
        {
            int p = U8();
            if (p > 1) throw new FormatException("invalid optional");
            return p == 1;
        }

        public void ExpectEnd() { if (pos != end) throw new FormatException("unexpected bytes after MLS struct"); }

        // The bytes read since `start` (a Position taken earlier), for structs kept as they came.
        public byte[] Since(int start)
        {
            var r = new byte[pos - start];
            Buffer.BlockCopy(b, start, r, 0, r.Length);
            return r;
        }
    }
}
