using System.Buffers.Binary;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;

namespace ImaginedNT.Apis.Crt;

// Native locale evaluation runs on a disposable thread with per-thread CRT state.
// Only copied data and relocated guest pointers leave that thread.
internal sealed class CrtLocaleData
{
    internal string[] Categories = new string[6];
    internal int[] NarrowCategories = new int[6], WideCategories = new int[6];
    internal byte[] Bytes = [];
    internal List<(int Field, int Target)> Pointers = new();
    internal uint CodePage, CollateCodePage;
    internal int MbCurMax, MultibyteCodePage, Pctype, Wctype, LocaleNames, Lconv, Mbcinfo;
    internal ulong Address;
    internal int References = 1;

    internal static CrtLocaleData? Create(CrtLocaleData? previous, int category, string? locale, int? codePage, bool localeObject = false)
    {
        CrtLocaleData? result = null;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (Native.Configure(1) == -1) throw new InvalidOperationException("Cannot enable isolated CRT locale");
                if (Native.SetWide(0, previous?.Categories[0] ?? "C") == 0)
                    throw new InvalidOperationException("Cannot restore CRT locale snapshot");
                if (previous is not null && Native.SetMbcp(previous.MultibyteCodePage) != 0)
                    throw new InvalidOperationException("Cannot restore CRT code page");
                if (locale is not null && Native.SetWide(category, locale) == 0) return;
                if (localeObject && Native.SetMbcp((int)Native.CodePage()) != 0) return;
                if (codePage.HasValue && Native.SetMbcp(codePage.Value) != 0) return;
                result = Capture();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        }) { IsBackground = true, Name = "CRT locale snapshot" };
        thread.Start();
        thread.Join();
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
        return result;
    }

    private static CrtLocaleData Capture()
    {
        var handle = Native.GetCurrentLocale();
        if (handle == 0) throw new OutOfMemoryException("Cannot snapshot CRT locale");
        try
        {
            return Capture(Marshal.ReadIntPtr(handle), Marshal.ReadIntPtr(handle, 8));
        }
        finally
        {
            Native.FreeLocale(handle);
        }
    }

    private static CrtLocaleData Capture(nint locinfo, nint mbcinfo)
    {
        var data = new CrtLocaleData
        {
            CodePage = Native.CodePage(),
            CollateCodePage = Native.CollateCodePage(),
            MbCurMax = Native.MbCurMax(),
            MultibyteCodePage = Native.GetMbcp()
        };
        var b = new Blob(data);
        // Windows x64 UCRT layout from corecrt_internal.h. Relocate every pointer.
        b.Add(Copy(locinfo, 344));
        b.Int32(16, 1);
        for (var field = 32; field < 344; field += 8) b.ZeroPointer(field);
        data.Pctype = b.Add(Copy(Native.Pctype() - 256, 768)) + 256;
        data.Wctype = b.Add(Copy(Native.Wctype() - 2, 514)) + 2;
        b.Pointer(0, data.Pctype);
        b.Int32(8, data.MbCurMax);
        b.Int32(12, unchecked((int)data.CodePage));
        data.LocaleNames = 296;
        var names = Native.LocaleNames();
        for (var i = 0; i < 6; i++)
        {
            data.Categories[i] = Marshal.PtrToStringUni(Native.SetWide(i, null))
                ?? throw new InvalidOperationException("Cannot query CRT locale");
            data.NarrowCategories[i] = b.Add(CopyString(Native.SetNarrow(i, 0), 1));
            data.WideCategories[i] = b.Add(Encoding.Unicode.GetBytes(data.Categories[i] + '\0'));
            for (var part = 0; part < 4; part++)
            {
                var field = 32 + i * 32 + part * 8;
                var pointer = Marshal.ReadIntPtr(locinfo, field);
                if (pointer == 0) continue;
                b.Pointer(field, b.Add(part < 2 ? CopyString(pointer, part == 0 ? 1 : 2) : BitConverter.GetBytes(1)));
            }
            var name = Marshal.ReadIntPtr(names, i * 8);
            if (name != 0) b.Pointer(data.LocaleNames + i * 8, b.Add(CopyString(name, 2)));
        }
        var lconv = Native.Localeconv();
        data.Lconv = b.Add(Copy(lconv, 152));
        for (var i = 0; i < 10; i++)
            b.Pointer(data.Lconv + i * 8, b.Add(CopyString(Marshal.ReadIntPtr(lconv, i * 8), 1)));
        for (var i = 0; i < 8; i++)
        {
            var pointer = Marshal.ReadIntPtr(lconv, 88 + i * 8);
            // These fields may be null in the C locale.
            b.ZeroPointer(data.Lconv + 88 + i * 8);
            if (pointer != 0) b.Pointer(data.Lconv + 88 + i * 8, b.Add(CopyString(pointer, 2)));
        }
        b.Pointer(248, data.Lconv);
        foreach (var field in new[] { 224, 232, 240, 256 })
            if (Marshal.ReadIntPtr(locinfo, field) != 0) b.Pointer(field, b.Add(BitConverter.GetBytes(1)));
        var ctype1 = Marshal.ReadIntPtr(locinfo, 264);
        if (ctype1 != 0) b.Pointer(264, data.Pctype - 2);
        foreach (var field in new[] { 272, 280 })
        {
            var pointer = Marshal.ReadIntPtr(locinfo, field);
            if (pointer != 0) b.Pointer(field, b.Add(Copy(pointer - 128, 384)) + 128);
        }
        var time = Marshal.ReadIntPtr(locinfo, 288);
        if (time != 0)
        {
            var target = b.Add(Copy(time, 704));
            b.Pointer(288, target);
            b.Int32(target + 348, 1);
            for (var i = 0; i < 43; i++)
                b.Pointer(target + i * 8, b.Add(CopyString(Marshal.ReadIntPtr(time, i * 8), 1)));
            for (var i = 0; i < 44; i++)
            {
                var field = 352 + i * 8;
                var pointer = Marshal.ReadIntPtr(time, field);
                b.ZeroPointer(target + field);
                if (pointer != 0) b.Pointer(target + field, b.Add(CopyString(pointer, 2)));
            }
        }
        data.Mbcinfo = b.Add(Copy(mbcinfo, 552));
        b.Int32(data.Mbcinfo, 1);
        b.ZeroPointer(data.Mbcinfo + 544);
        var mbName = Marshal.ReadIntPtr(mbcinfo, 544);
        if (mbName != 0) b.Pointer(data.Mbcinfo + 544, b.Add(CopyString(mbName, 2)));
        data.Bytes = b.ToArray();
        return data;
    }

    private static byte[] Copy(nint pointer, int length)
    {
        var bytes = new byte[length];
        Marshal.Copy(pointer, bytes, 0, length);
        return bytes;
    }

    private static byte[] CopyString(nint pointer, int width)
    {
        if (pointer == 0) return new byte[width];
        var length = 0;
        while (width == 1 ? Marshal.ReadByte(pointer, length) != 0 : Marshal.ReadInt16(pointer, length) != 0)
            length += width;
        return Copy(pointer, length + width);
    }

    private sealed class Blob(CrtLocaleData data)
    {
        private readonly MemoryStream _stream = new();
        internal int Add(byte[] bytes)
        {
            while (_stream.Length % 8 != 0) _stream.WriteByte(0);
            var offset = checked((int)_stream.Length);
            _stream.Write(bytes);
            return offset;
        }
        internal void Pointer(int field, int target)
        {
            ZeroPointer(field);
            data.Pointers.Add((field, target));
        }
        internal void ZeroPointer(int field) => _stream.GetBuffer().AsSpan(field, 8).Clear();
        internal void Int32(int field, int value) => BinaryPrimitives.WriteInt32LittleEndian(_stream.GetBuffer().AsSpan(field), value);
        internal byte[] ToArray() => _stream.ToArray();
    }

    internal static void SetErrno(int value) => Native.SetErrno(value);

    private static class Native
    {
        private const string Dll = "ucrtbase.dll";
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "_get_current_locale")]
        internal static extern nint GetCurrentLocale();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "_free_locale")]
        internal static extern void FreeLocale(nint locale);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "_set_errno")]
        internal static extern int SetErrno(int value);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "_configthreadlocale")]
        internal static extern int Configure(int mode);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, ExactSpelling = true, EntryPoint = "_wsetlocale")]
        internal static extern nint SetWide(int category, string? locale);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "setlocale")]
        internal static extern nint SetNarrow(int category, nint locale);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "___lc_codepage_func")]
        internal static extern uint CodePage();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "___lc_collate_cp_func")]
        internal static extern uint CollateCodePage();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "___lc_locale_name_func")]
        internal static extern nint LocaleNames();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "___mb_cur_max_func")]
        internal static extern int MbCurMax();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "__pctype_func")]
        internal static extern nint Pctype();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "__pwctype_func")]
        internal static extern nint Wctype();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "localeconv")]
        internal static extern nint Localeconv();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "_getmbcp")]
        internal static extern int GetMbcp();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "_setmbcp")]
        internal static extern int SetMbcp(int codePage);
    }
}
