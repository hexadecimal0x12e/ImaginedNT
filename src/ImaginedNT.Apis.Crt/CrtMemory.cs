using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using ImaginedNT.Core.Runtime;

namespace ImaginedNT.Apis.Crt;

internal static class CrtMemory
{
    internal const ulong EInvalid = 22, ERange = 34, ENoMemory = 12, Truncated = 80;
    private static readonly Lazy<Encoding> Ansi = new(() => CodePagesEncodingProvider.Instance.GetEncoding(0) ?? Encoding.UTF8);
    private static readonly Lazy<bool[]> AnsiLeadBytes = new(() =>
    {
        var table = new bool[256];
        if (Ansi.Value.IsSingleByte || Ansi.Value.CodePage == 65001) return table;
        var decoder = Ansi.Value.GetDecoder();
        Span<byte> input = stackalloc byte[1];
        Span<char> output = stackalloc char[2];
        for (var i = 0; i < table.Length; i++)
        {
            decoder.Reset();
            input[0] = (byte)i;
            table[i] = decoder.GetChars(input, output, flush: false) == 0;
        }
        return table;
    });

    internal static int NextPathChar(string value, int index, int width) =>
        width == 1 && index + 1 < value.Length && AnsiLeadBytes.Value[(byte)value[index]] ? index + 2 : index + 1;

    internal static bool Range(ulong address, ulong length) => length == 0 ||
        (address != 0 && address <= long.MaxValue && length <= (ulong)long.MaxValue - address);

    internal static bool Overlaps(ulong a, ulong aLength, ulong b, ulong bLength) =>
        aLength != 0 && bLength != 0 && (a <= b ? b - a < aLength : a - b < bLength);

    internal static bool Read<T>(ApiCallContext ctx, ulong address, out T value) where T : unmanaged
    {
        Span<byte> bytes = stackalloc byte[Marshal.SizeOf<T>()];
        value = default;
        if (!Range(address, (ulong)bytes.Length) || !ctx.Memory.TryRead(address, bytes)) return false;
        value = MemoryMarshal.Read<T>(bytes);
        return true;
    }

    internal static bool Write<T>(ApiCallContext ctx, ulong address, T value) where T : unmanaged
    {
        Span<byte> bytes = stackalloc byte[Marshal.SizeOf<T>()];
        MemoryMarshal.Write(bytes, in value);
        return Range(address, (ulong)bytes.Length) && ctx.Memory.TryWrite(address, bytes);
    }

    internal static bool WriteChar(ApiCallContext ctx, ulong address, ushort value, int width) =>
        width == 1 ? Write(ctx, address, (byte)value) : Write(ctx, address, value);

    internal static bool Length(ApiCallContext ctx, ulong address, int width, ulong limit, out ulong length)
    {
        length = 0;
        if (limit == 0) return true;
        Span<byte> bytes = stackalloc byte[1024];
        var r = new Reader(ctx, address, width, bytes);
        while (length < limit)
        {
            if (!r.Next(out var c)) return false;
            if (c == 0) return true;
            length++;
        }
        return true;
    }

    internal static bool Copy(ApiCallContext ctx, ulong dest, ulong source, ulong length)
    {
        if (!Range(dest, length) || !Range(source, length)) return false;
        Span<byte> bytes = stackalloc byte[4096];
        var backward = dest > source && dest - source < length;
        for (ulong done = 0; done < length;)
        {
            var n = (int)Math.Min((ulong)bytes.Length, length - done);
            var offset = backward ? length - done - (ulong)n : done;
            if (!ctx.Memory.TryRead(source + offset, bytes[..n]) ||
                !ctx.Memory.TryWrite(dest + offset, bytes[..n])) return false;
            done += (ulong)n;
        }
        return true;
    }

    internal static bool Fill(ApiCallContext ctx, ulong address, ulong count, ushort value, int width)
    {
        if (count > (ulong)long.MaxValue / (ulong)width || !Range(address, count * (ulong)width)) return false;
        Span<byte> bytes = stackalloc byte[4096];
        if (width == 1) bytes.Fill((byte)value);
        else
            for (var i = 0; i < bytes.Length; i += 2)
                BinaryPrimitives.WriteUInt16LittleEndian(bytes[i..], value);
        var length = count * (ulong)width;
        for (ulong done = 0; done < length;)
        {
            var n = (int)Math.Min((ulong)bytes.Length, length - done);
            if (!ctx.Memory.TryWrite(address + done, bytes[..n])) return false;
            done += (ulong)n;
        }
        return true;
    }

    internal static ulong ClearError(ApiCallContext ctx, ulong dest, ulong capacity, int width, ulong error)
    {
        if (dest != 0 && capacity != 0) WriteChar(ctx, dest, 0, width);
        return error;
    }

    internal static bool ReadText(ApiCallContext ctx, ulong address, int width, out string value,
        bool ansi = false, int maxLength = int.MaxValue)
    {
        value = string.Empty;
        Span<byte> bytes = stackalloc byte[1024];
        var r = new Reader(ctx, address, width, bytes);
        var text = new StringBuilder();
        while (r.Next(out var c))
        {
            if (c == 0)
            {
                value = ansi && width == 1 ? Ansi.Value.GetString(Encoding.Latin1.GetBytes(text.ToString())) : text.ToString();
                return true;
            }
            if (text.Length >= maxLength) return false;
            text.Append((char)c);
        }
        return false;
    }

    // Narrow path/string transforms preserve bytes; locale-dependent conversion is outside this API set.
    internal static byte[] Encode(string value, int width, bool ansi = false)
    {
        if (width == 1) return (ansi ? Ansi.Value : Encoding.Latin1).GetBytes(value + '\0');
        var bytes = new byte[(value.Length + 1) * 2];
        MemoryMarshal.AsBytes(value.AsSpan()).CopyTo(bytes);
        return bytes;
    }

    internal static bool WriteText(ApiCallContext ctx, ulong address, string value, int width) =>
        ctx.Memory.TryWrite(address, Encode(value, width));

    internal ref struct Reader
    {
        private readonly ApiCallContext _ctx;
        private readonly int _width;
        private readonly Span<byte> _bytes;
        private ulong _address;
        private int _offset, _length;

        internal Reader(ApiCallContext ctx, ulong address, int width, Span<byte> bytes)
        {
            _ctx = ctx;
            _address = address;
            _width = width;
            _bytes = bytes;
            _offset = _length = 0;
        }

        internal bool Next(out ushort value)
        {
            value = 0;
            if (_offset == _length)
            {
                // Stop reads at page boundaries so a terminator beside a guard page remains readable.
                var n = Math.Min(_bytes.Length, (int)(4096 - (_address & 4095)));
                n -= n % _width;
                if (n == 0) n = _width;
                if (!Range(_address, (ulong)n) || !_ctx.Memory.TryRead(_address, _bytes[..n])) return false;
                _address += (ulong)n;
                _offset = 0;
                _length = n;
            }
            value = _width == 1 ? _bytes[_offset] : BinaryPrimitives.ReadUInt16LittleEndian(_bytes[_offset..]);
            _offset += _width;
            return true;
        }
    }
}
