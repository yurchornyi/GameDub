using System;
using System.Buffers.Binary;
using System.Text;

namespace GameDub.Pipeline;

/// <summary>
/// Sec-MS-GEC v2 token for the Edge read-aloud service: "&lt;CS64 hex&gt;-&lt;SHA256 hex&gt;".
/// Port of edge-tts drm.py (rany2/edge-tts). The SHA-256 here starts from a precomputed
/// mid-block state, so it is not the standard digest of the input.
/// </summary>
public static class EdgeDrm
{
    public const string TrustedClientToken = "6A5AA1D4EAFF4E9FB37E23D68491D6F4";
    const long WinEpoch = 11644473600L;

    static readonly uint[] Initial = { 0x6A09E667, 0xBB67AE85, 0x3C6EF372, 0xA54FF53A, 0x510E527F, 0x9B05688C, 0x1F83D9AB, 0x5BE0CD19 };
    static readonly uint[] State = { 0xFD9FBEA2, 0x91243CE7, 0xBE499094, 0xCD2E1E0A, 0x6C006F49, 0x04BD35AB, 0xFEB0A7E4, 0x03EFFBD2 };
    static readonly uint[] Schedule = { 0xEB24BF9E, 0x1558691F, 0xA0C09EE2, 0x0CFA32BA, 0xA622EE8C, 0xD1DD9491, 0xC0DD25A0, 0xF9E70AB6, 0xF0C31573, 0x7058CE4E, 0x7188E606, 0x00E5AB33, 0xD258B060, 0x72766564 };
    static readonly uint[] K =
    {
        0x428A2F98, 0x71374491, 0xB5C0FBCF, 0xE9B5DBA5, 0x3956C25B, 0x59F111F1, 0x923F82A4, 0xAB1C5ED5,
        0xD807AA98, 0x12835B01, 0x243185BE, 0x550C7DC3, 0x72BE5D74, 0x80DEB1FE, 0x9BDC06A7, 0xC19BF174,
        0xE49B69C1, 0xEFBE4786, 0x0FC19DC6, 0x240CA1CC, 0x2DE92C6F, 0x4A7484AA, 0x5CB0A9DC, 0x76F988DA,
        0x983E5152, 0xA831C66D, 0xB00327C8, 0xBF597FC7, 0xC6E00BF3, 0xD5A79147, 0x06CA6351, 0x14292967,
        0x27B70A85, 0x2E1B2138, 0x4D2C6DFC, 0x53380D13, 0x650A7354, 0x766A0ABB, 0x81C2C92E, 0x92722C85,
        0xA2BFE8A1, 0xA81A664B, 0xC24B8B70, 0xC76C51A3, 0xD192E819, 0xD6990624, 0xF40E3585, 0x106AA070,
        0x19A4C116, 0x1E376C08, 0x2748774C, 0x34B0BCB5, 0x391C0CB3, 0x4ED8AA4A, 0x5B9CCA4F, 0x682E6FF3,
        0x748F82EE, 0x78A5636F, 0x84C87814, 0x8CC70208, 0x90BEFFFA, 0xA4506CEB, 0xBEF9A3F7, 0xC67178F2,
    };
    const uint Adjustment = 0xC9DC06A7, Sigma0Adj = 0x059C0B80, Sigma1Adj = 0x000B92C0;
    static readonly uint[] BlockK = BuildBlockK();

    static readonly (uint coef, uint offset)[] Cs64Coefficients =
    {
        (0xE82E91B1, 0xB32700D0), (0x44DE24C6, 0x88DFF51D), (0x3BEB65BC, 0x29514038), (0xC89C5933, 0xB328CBDF),
        (0x8D1EC211, 0xD405B4D5), (0x314EF5D5, 0x26E55F3C), (0xE92491B1, 0x607801C6), (0xF0A5B9D5, 0x82B2C1FB),
        (0x06064CF3, 0xFA51A3DA), (0xDA79FA60, 0x207CC464), (0xBED20560, 0x3C3C74F3), (0x89F9C9F8, 0xD2D14B61),
        (0x35FA38C6, 0xBB04A2C1), (0xCB49E3C6, 0xC5626167),
    };
    const uint Cs64Factor = 0x82893933, Cs64Adj0 = 0xF2F90B1A, Cs64Adj1 = 0x0B1A0000;

    static uint[] BuildBlockK()
    {
        var k = (uint[])K.Clone();
        k[14] = Adjustment;
        return k;
    }

    public static string Generate(long skewSeconds = 0)
    {
        long t = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + skewSeconds + WinEpoch;
        t -= t % 300;
        t *= 10_000_000L;
        var data = Encoding.ASCII.GetBytes($"{t}{TrustedClientToken}");
        var digest = Sha256(data);
        var (c0, c1) = Cs64(data, digest);
        var cs = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(cs, c0);
        BinaryPrimitives.WriteUInt32LittleEndian(cs.AsSpan(4), c1);
        return Convert.ToHexString(cs) + "-" + Convert.ToHexString(digest);
    }

    static uint Rotr(uint x, int n) => (x >> n) | (x << (32 - n));
    static uint Rol16(uint x) => (x << 16) | (x >> 16);
    static uint SmallSigma0(uint x) => Rotr(x, 7) ^ Rotr(x, 18) ^ (x >> 3);
    static uint SmallSigma1(uint x) => Rotr(x, 17) ^ Rotr(x, 19) ^ (x >> 10);

    static void Expand(uint[] w, int first)
    {
        for (int i = first; i < 64; i++)
            w[i] = SmallSigma1(w[i - 2]) + w[i - 7] + SmallSigma0(w[i - 15]) + w[i - 16];
    }

    static uint[] Rounds(uint[] s, uint[] w, int first)
    {
        var k = first == 14 ? BlockK : K;
        uint a = s[0], b = s[1], c = s[2], d = s[3], e = s[4], f = s[5], g = s[6], h = s[7];
        for (int i = first; i < 64; i++)
        {
            uint t1 = h + (Rotr(e, 6) ^ Rotr(e, 11) ^ Rotr(e, 25)) + (g ^ (e & (f ^ g))) + k[i] + w[i];
            uint t2 = (Rotr(a, 2) ^ Rotr(a, 13) ^ Rotr(a, 22)) + ((a & b) | (c & (a | b)));
            h = g; g = f; f = e; e = d + t1; d = c; c = b; b = a; a = t1 + t2;
        }
        return new[] { a, b, c, d, e, f, g, h };
    }

    static uint[] Init(ReadOnlySpan<byte> d7)
    {
        var w = new uint[64];
        w[14] = (uint)((d7[0] << 16) | (d7[1] << 8) | d7[2]);
        w[15] = BinaryPrimitives.ReadUInt32BigEndian(d7[3..7]);
        for (int i = 16; i < 30; i++)
        {
            uint v = Schedule[i - 16];
            if (i - 2 >= 14) v += i == 16 ? SmallSigma1(w[i - 2]) ^ Sigma1Adj : SmallSigma1(w[i - 2]);
            if (i - 7 >= 14) v += w[i - 7];
            if (i - 15 >= 14) v += i == 29 ? SmallSigma0(w[i - 15]) ^ Sigma0Adj : SmallSigma0(w[i - 15]);
            w[i] = v;
        }
        w[30] = SmallSigma1(w[28]) + w[23] + SmallSigma0(w[15]) + w[14] + Adjustment - K[14];
        Expand(w, 31);
        var work = Rounds(State, w, 14);
        var res = new uint[8];
        for (int i = 0; i < 8; i++) res[i] = Initial[i] + work[i];
        return res;
    }

    static byte[] Sha256(byte[] data)
    {
        int length = 57 + data.Length;
        int zeros = ((55 - length) % 64 + 64) % 64;
        var padded = new byte[data.Length - 7 + 1 + zeros + 8];
        data.AsSpan(7).CopyTo(padded);
        padded[data.Length - 7] = 0x80;
        BinaryPrimitives.WriteUInt64BigEndian(padded.AsSpan(padded.Length - 8), (ulong)length * 8);

        var state = Init(data.AsSpan(0, 7));
        for (int off = 0; off < padded.Length; off += 64)
        {
            var w = new uint[64];
            for (int i = 0; i < 16; i++) w[i] = BinaryPrimitives.ReadUInt32BigEndian(padded.AsSpan(off + i * 4));
            Expand(w, 16);
            var work = Rounds(state, w, 0);
            for (int i = 0; i < 8; i++) state[i] += work[i];
        }
        var outp = new byte[32];
        for (int i = 0; i < 8; i++) BinaryPrimitives.WriteUInt32BigEndian(outp.AsSpan(i * 4), state[i]);
        return outp;
    }

    static (uint, uint) Cs64(byte[] data, byte[] digest)
    {
        uint keyA = BinaryPrimitives.ReadUInt32LittleEndian(digest) | 1;
        uint keyB = BinaryPrimitives.ReadUInt32LittleEndian(digest.AsSpan(4)) | 1;
        uint[] factors = { keyA * Cs64Factor, keyB * Cs64Factor };
        var terms = new System.Collections.Generic.List<uint>();
        var offsets = new System.Collections.Generic.List<uint>();
        for (int i = 0; i < Cs64Coefficients.Length; i++)
        {
            terms.Add(Cs64Coefficients[i].coef * factors[i % 2]);
            offsets.Add(Cs64Coefficients[i].offset);
        }

        // Little-endian words of (0x00 + data), zero-padded to an even word count.
        var src = new byte[1 + data.Length];
        data.CopyTo(src, 1);
        int nWords = (src.Length + 3) / 4;
        if (nWords % 2 == 1) nWords++;
        var buf = new byte[nWords * 4];
        src.CopyTo(buf, 0);
        for (int i = 0; i < nWords; i++)
        {
            uint word = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(i * 4));
            uint term = word * (i % 2 == 0 ? keyA : keyB);
            uint offset = Rol16(word) * (i % 2 == 0 ? 0xEF0569FBu : 0xC31713DBu);
            if (i == 0) { term += Cs64Adj0 * factors[0]; offset += Cs64Adj1; }
            terms.Add(term);
            offsets.Add(offset);
        }

        uint swapAcc = 0, swapLast = 0, state = 0, acc = 0;
        for (int i = 0; i < terms.Count; i += 2)
        {
            uint x = terms[i], y = terms[i + 1];
            uint t = Rol16(offsets[i] + x);
            uint q = t * 0x76F50F34;
            swapAcc += Rol16(q) * 0xC3EFEA97 + q;
            t = Rol16(offsets[i + 1] + y);
            q = t * 0x1881CE3C;
            swapLast = Rol16(q) * 0x35BD1EC9 + q;
            swapAcc += swapLast;

            state = state * keyA + x;
            state = Rol16(state) * 0xCF98B111;
            state = Rol16(state) * 0x87085B9F;
            state = Rol16(state) * 0x12CEB96D;
            state = Rol16(state) * 0x257E1D83;
            acc += state;
            t = state * keyB + y;
            t = Rol16(t) * 0xA27416F5;
            t = Rol16(t) * 0xD38396FF;
            state = Rol16(Rol16(t) * 0x7C932B89) * 0xBFA49F69;
            acc += state;
        }
        return (swapLast ^ state, swapAcc ^ acc);
    }
}
