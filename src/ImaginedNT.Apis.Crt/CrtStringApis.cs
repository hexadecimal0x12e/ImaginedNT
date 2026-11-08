using ImaginedNT.Core.Runtime;

namespace ImaginedNT.Apis.Crt;

public static class CrtStringApis
{
    private const string Module = "api-ms-win-crt-string-l1-1-0.dll";

    [PartiallyImaginedApi(Module, "__isascii", ApiCategory.Memory, "ucrtbase!__isascii")]
    private static ulong IsAscii(ApiCallContext ctx) => ctx.Arg32(0) <= 127 ? 1UL : 0UL;

    [PartiallyImaginedApi(Module, "__iscsym", ApiCategory.Memory, "ucrtbase!__iscsym")]
    private static ulong IsCSymbol(ApiCallContext ctx) => IsSymbol(ctx.Arg32(0), true);

    [PartiallyImaginedApi(Module, "__iscsymf", ApiCategory.Memory, "ucrtbase!__iscsymf")]
    private static ulong IsCSymbolFirst(ApiCallContext ctx) => IsSymbol(ctx.Arg32(0), false);

    [PartiallyImaginedApi(Module, "__iswcsym", ApiCategory.Memory, "ucrtbase!__iswcsym")]
    private static ulong IsWideCSymbol(ApiCallContext ctx) => IsSymbol(ctx.Arg32(0) & 0xffff, true);

    [PartiallyImaginedApi(Module, "__iswcsymf", ApiCategory.Memory, "ucrtbase!__iswcsymf")]
    private static ulong IsWideCSymbolFirst(ApiCallContext ctx) => IsSymbol(ctx.Arg32(0) & 0xffff, false);

    [PartiallyImaginedApi(Module, "_memccpy", ApiCategory.Memory, "ucrtbase!_memccpy")]
    private static ulong Memccpy(ApiCallContext ctx) => CopyUntil(ctx);

    [PartiallyImaginedApi("api-ms-win-crt-private-l1-1-0.dll", "memcpy", ApiCategory.Memory, "ucrtbase!memcpy")]
    [PartiallyImaginedApi("api-ms-win-crt-private-l1-1-0.dll", "memmove", ApiCategory.Memory, "ucrtbase!memmove")]
    private static ulong CopyMemory(ApiCallContext ctx)
    {
        if (CrtMemory.Copy(ctx, ctx.Arg(0), ctx.Arg(1), ctx.Arg(2))) return ctx.Arg(0);
        ctx.Runtime.Trace.Warn($"{ctx.Name}: cannot copy 0x{ctx.Arg(2):X} bytes from 0x{ctx.Arg(1):X} to 0x{ctx.Arg(0):X}",
            ctx.Process.Id, ctx.Thread.Id);
        return 0;
    }

    [PartiallyImaginedApi(Module, "memset", ApiCategory.Memory, "ucrtbase!memset")]
    private static ulong Memset(ApiCallContext ctx) => CrtMemory.Fill(ctx, ctx.Arg(0), ctx.Arg(2), (byte)ctx.Arg32(1), 1) ? ctx.Arg(0) : 0;

    [PartiallyImaginedApi(Module, "__strncnt", ApiCategory.Memory, "ucrtbase!__strncnt")]
    private static ulong StrCount(ApiCallContext ctx) => GetLength(ctx, 1, ctx.Arg(1));

    [PartiallyImaginedApi(Module, "strlen", ApiCategory.Memory, "ucrtbase!strlen")]
    private static ulong StrLength(ApiCallContext ctx) => GetLength(ctx, 1, ulong.MaxValue);

    [PartiallyImaginedApi(Module, "strnlen", ApiCategory.Memory, "ucrtbase!strnlen")]
    private static ulong StrBoundedLength(ApiCallContext ctx) => GetLength(ctx, 1, ctx.Arg(1));

    [PartiallyImaginedApi(Module, "strcpy", ApiCategory.Memory, "ucrtbase!strcpy")]
    private static ulong StrCopy(ApiCallContext ctx) => CopyString(ctx, 1, false, false);

    [PartiallyImaginedApi(Module, "strncpy", ApiCategory.Memory, "ucrtbase!strncpy")]
    private static ulong StrBoundedCopy(ApiCallContext ctx) => CopyString(ctx, 1, true, false);

    [PartiallyImaginedApi(Module, "strcat", ApiCategory.Memory, "ucrtbase!strcat")]
    private static ulong StrAppend(ApiCallContext ctx) => CopyString(ctx, 1, false, true);

    [PartiallyImaginedApi(Module, "strncat", ApiCategory.Memory, "ucrtbase!strncat")]
    private static ulong StrBoundedAppend(ApiCallContext ctx) => CopyString(ctx, 1, true, true);

    [PartiallyImaginedApi(Module, "strcpy_s", ApiCategory.Memory, "ucrtbase!strcpy_s")]
    private static ulong StrCopySecure(ApiCallContext ctx) => CopyStringSecure(ctx, 1, false, false);

    [PartiallyImaginedApi(Module, "strncpy_s", ApiCategory.Memory, "ucrtbase!strncpy_s")]
    private static ulong StrBoundedCopySecure(ApiCallContext ctx) => CopyStringSecure(ctx, 1, true, false);

    [PartiallyImaginedApi(Module, "strcat_s", ApiCategory.Memory, "ucrtbase!strcat_s")]
    private static ulong StrAppendSecure(ApiCallContext ctx) => CopyStringSecure(ctx, 1, false, true);

    [PartiallyImaginedApi(Module, "strncat_s", ApiCategory.Memory, "ucrtbase!strncat_s")]
    private static ulong StrBoundedAppendSecure(ApiCallContext ctx) => CopyStringSecure(ctx, 1, true, true);

    [PartiallyImaginedApi(Module, "strcmp", ApiCategory.Memory, "ucrtbase!strcmp")]
    private static ulong StrCompare(ApiCallContext ctx) => Compare(ctx, 1, ulong.MaxValue);

    [PartiallyImaginedApi(Module, "strncmp", ApiCategory.Memory, "ucrtbase!strncmp")]
    private static ulong StrBoundedCompare(ApiCallContext ctx) => Compare(ctx, 1, ctx.Arg(2));

    [PartiallyImaginedApi(Module, "strspn", ApiCategory.Memory, "ucrtbase!strspn")]
    private static ulong StrSpan(ApiCallContext ctx) => ScanSet(ctx, 1, true, false);

    [PartiallyImaginedApi(Module, "strcspn", ApiCategory.Memory, "ucrtbase!strcspn")]
    private static ulong StrComplementSpan(ApiCallContext ctx) => ScanSet(ctx, 1, false, false);

    [PartiallyImaginedApi(Module, "strpbrk", ApiCategory.Memory, "ucrtbase!strpbrk")]
    private static ulong StrFindSet(ApiCallContext ctx) => ScanSet(ctx, 1, false, true);

    [PartiallyImaginedApi(Module, "strtok_s", ApiCategory.Memory, "ucrtbase!strtok_s")]
    private static ulong StrTokenize(ApiCallContext ctx) => Tokenize(ctx, 1);

    [PartiallyImaginedApi(Module, "_strrev", ApiCategory.Memory, "ucrtbase!_strrev")]
    private static ulong StrReverse(ApiCallContext ctx) => Reverse(ctx, 1);

    [PartiallyImaginedApi(Module, "_strset", ApiCategory.Memory, "ucrtbase!_strset")]
    private static ulong StrSet(ApiCallContext ctx) => Set(ctx, 1, false, false);

    [PartiallyImaginedApi(Module, "_strnset", ApiCategory.Memory, "ucrtbase!_strnset")]
    private static ulong StrBoundedSet(ApiCallContext ctx) => Set(ctx, 1, true, false);

    [PartiallyImaginedApi(Module, "_strset_s", ApiCategory.Memory, "ucrtbase!_strset_s")]
    private static ulong StrSetSecure(ApiCallContext ctx) => Set(ctx, 1, false, true);

    [PartiallyImaginedApi(Module, "_strnset_s", ApiCategory.Memory, "ucrtbase!_strnset_s")]
    private static ulong StrBoundedSetSecure(ApiCallContext ctx) => Set(ctx, 1, true, true);

    [PartiallyImaginedApi(Module, "__wcsncnt", ApiCategory.Memory, "ucrtbase!__wcsncnt")]
    private static ulong WcsCount(ApiCallContext ctx) => GetLength(ctx, 2, ctx.Arg(1));

    [PartiallyImaginedApi(Module, "wcslen", ApiCategory.Memory, "ucrtbase!wcslen")]
    private static ulong WcsLength(ApiCallContext ctx) => GetLength(ctx, 2, ulong.MaxValue);

    [PartiallyImaginedApi(Module, "wcsnlen", ApiCategory.Memory, "ucrtbase!wcsnlen")]
    private static ulong WcsBoundedLength(ApiCallContext ctx) => GetLength(ctx, 2, ctx.Arg(1));

    [PartiallyImaginedApi(Module, "_wcsdup", ApiCategory.Memory, "ucrtbase!_wcsdup", "msvcrt!_wcsdup",
        "api-ms-win-crt-private-l1-1-0!_o__wcsdup", "ucrtbase!_o__wcsdup")]
    private static ulong WcsDuplicate(ApiCallContext ctx)
    {
        var source = ctx.Arg(0);
        if (source == 0) return 0;
        if (!CrtMemory.Length(ctx, source, 2, ulong.MaxValue, out var length))
        {
            CrtLocaleData.SetErrno(22);
            ctx.Runtime.Trace.Warn($"{ctx.Name}: cannot read wide string at 0x{source:X}", ctx.Process.Id, ctx.Thread.Id);
            return 0;
        }
        if (length > (ulong.MaxValue - 31) / 2 - 1)
        {
            CrtLocaleData.SetErrno(12);
            ctx.Runtime.Trace.Warn($"{ctx.Name}: wide string allocation size exceeds CRT limit", ctx.Process.Id, ctx.Thread.Id);
            return 0;
        }
        var size = (length + 1) * 2;
        var copy = ctx.Process.Heap.Allocate(size, false, ctx.Thread.Id);
        if (copy == 0)
        {
            CrtLocaleData.SetErrno(12);
            return 0;
        }
        if (CrtMemory.Copy(ctx, copy, source, size)) return copy;
        ctx.Process.Heap.Free(copy, ctx.Thread.Id);
        CrtLocaleData.SetErrno(22);
        ctx.Runtime.Trace.Warn($"{ctx.Name}: cannot duplicate wide string at 0x{source:X}", ctx.Process.Id, ctx.Thread.Id);
        return 0;
    }

    [PartiallyImaginedApi(Module, "wcscpy", ApiCategory.Memory, "ucrtbase!wcscpy")]
    private static ulong WcsCopy(ApiCallContext ctx) => CopyString(ctx, 2, false, false);

    [PartiallyImaginedApi(Module, "wcsncpy", ApiCategory.Memory, "ucrtbase!wcsncpy")]
    private static ulong WcsBoundedCopy(ApiCallContext ctx) => CopyString(ctx, 2, true, false);

    [PartiallyImaginedApi(Module, "wcscat", ApiCategory.Memory, "ucrtbase!wcscat")]
    private static ulong WcsAppend(ApiCallContext ctx) => CopyString(ctx, 2, false, true);

    [PartiallyImaginedApi(Module, "wcsncat", ApiCategory.Memory, "ucrtbase!wcsncat")]
    private static ulong WcsBoundedAppend(ApiCallContext ctx) => CopyString(ctx, 2, true, true);

    [PartiallyImaginedApi(Module, "wcscpy_s", ApiCategory.Memory, "ucrtbase!wcscpy_s")]
    private static ulong WcsCopySecure(ApiCallContext ctx) => CopyStringSecure(ctx, 2, false, false);

    [PartiallyImaginedApi(Module, "wcsncpy_s", ApiCategory.Memory, "ucrtbase!wcsncpy_s")]
    private static ulong WcsBoundedCopySecure(ApiCallContext ctx) => CopyStringSecure(ctx, 2, true, false);

    [PartiallyImaginedApi(Module, "wcscat_s", ApiCategory.Memory, "ucrtbase!wcscat_s")]
    private static ulong WcsAppendSecure(ApiCallContext ctx) => CopyStringSecure(ctx, 2, false, true);

    [PartiallyImaginedApi(Module, "wcsncat_s", ApiCategory.Memory, "ucrtbase!wcsncat_s")]
    private static ulong WcsBoundedAppendSecure(ApiCallContext ctx) => CopyStringSecure(ctx, 2, true, true);

    [PartiallyImaginedApi(Module, "wcscmp", ApiCategory.Memory, "ucrtbase!wcscmp")]
    private static ulong WcsCompare(ApiCallContext ctx) => Compare(ctx, 2, ulong.MaxValue);

    [PartiallyImaginedApi(Module, "_wcsicmp", ApiCategory.Memory, "ucrtbase!_wcsicmp", "msvcrt!_wcsicmp",
        "api-ms-win-crt-private-l1-1-0!_o__wcsicmp", "ucrtbase!_o__wcsicmp")]
    private static ulong WcsCompareIgnoreCase(ApiCallContext ctx) => CompareIgnoreCase(ctx, 2, ulong.MaxValue);

    [PartiallyImaginedApi(Module, "_stricmp", ApiCategory.Memory, "ucrtbase!_stricmp", "msvcrt!_stricmp",
        "api-ms-win-crt-private-l1-1-0!_o__stricmp", "ucrtbase!_o__stricmp")]
    private static ulong StrCompareIgnoreCase(ApiCallContext ctx) => CompareIgnoreCase(ctx, 1, ulong.MaxValue);

    [PartiallyImaginedApi(Module, "_wcsnicmp", ApiCategory.Memory, "ucrtbase!_wcsnicmp", "msvcrt!_wcsnicmp",
        "api-ms-win-crt-private-l1-1-0!_o__wcsnicmp", "ucrtbase!_o__wcsnicmp")]
    private static ulong WcsBoundedCompareIgnoreCase(ApiCallContext ctx) => CompareIgnoreCase(ctx, 2, ctx.Arg(2));

    private static ulong CompareIgnoreCase(ApiCallContext ctx, int width, ulong limit)
    {
        Span<byte> a = stackalloc byte[1024], b = stackalloc byte[1024];
        var left = new CrtMemory.Reader(ctx, ctx.Arg(0), width, a);
        var right = new CrtMemory.Reader(ctx, ctx.Arg(1), width, b);
        for (ulong i = 0; i < limit; i++)
        {
            if (!left.Next(out var x) || !right.Next(out var y))
            {
                ctx.Runtime.Trace.Warn($"{ctx.Name}: cannot compare strings at 0x{ctx.Arg(0):X} and 0x{ctx.Arg(1):X}",
                    ctx.Process.Id, ctx.Thread.Id);
                return 0x7fffffff;
            }
            // CRT starts in the C locale; only ASCII letters change case.
            var l = x is >= 'A' and <= 'Z' ? x + 32 : x;
            var r = y is >= 'A' and <= 'Z' ? y + 32 : y;
            if (l != r) return unchecked((uint)(l - r));
            if (x == 0) return 0;
        }
        return 0;
    }

    [PartiallyImaginedApi("api-ms-win-crt-private-l1-1-0.dll", "memcmp", ApiCategory.Memory, "ucrtbase!memcmp", "msvcrt!memcmp")]
    private static ulong MemCompare(ApiCallContext ctx)
    {
        Span<byte> a = stackalloc byte[1024], b = stackalloc byte[1024];
        if (!CrtMemory.Range(ctx.Arg(0), ctx.Arg(2)) || !CrtMemory.Range(ctx.Arg(1), ctx.Arg(2)))
            return ctx.FailFast(5);
        for (ulong i = 0; i < ctx.Arg(2);)
        {
            var n = (int)Math.Min((ulong)a.Length, ctx.Arg(2) - i);
            if (!ctx.Memory.TryRead(ctx.Arg(0) + i, a[..n]) || !ctx.Memory.TryRead(ctx.Arg(1) + i, b[..n]))
                return ctx.FailFast(5);
            for (var j = 0; j < n; j++)
                if (a[j] != b[j]) return unchecked((uint)(a[j] - b[j]));
            i += (ulong)n;
        }
        return 0;
    }

    [PartiallyImaginedApi(Module, "towlower", ApiCategory.Memory, "ucrtbase!towlower", "msvcrt!towlower",
        "api-ms-win-crt-private-l1-1-0!_o_towlower", "ucrtbase!_o_towlower")]
    private static ulong ToWideLower(ApiCallContext ctx)
    {
        var c = (ushort)ctx.Arg32(0);
        return c is >= 'A' and <= 'Z' ? (ulong)(c + 32) : c;
    }

    [PartiallyImaginedApi(Module, "towupper", ApiCategory.Memory, "ucrtbase!towupper", "msvcrt!towupper",
        "api-ms-win-crt-private-l1-1-0!_o_towupper", "ucrtbase!_o_towupper")]
    private static ulong ToWideUpper(ApiCallContext ctx)
    {
        var c = (ushort)ctx.Arg32(0);
        return c is >= 'a' and <= 'z' ? (ulong)(c - 32) : c;
    }

    [PartiallyImaginedApi(Module, "iswalpha", ApiCategory.Memory, "ucrtbase!iswalpha", "msvcrt!iswalpha",
        "api-ms-win-crt-private-l1-1-0!_o_iswalpha", "ucrtbase!_o_iswalpha")]
    private static ulong IsWideAlpha(ApiCallContext ctx) => char.IsLetter((char)ctx.Arg32(0)) ? 1UL : 0UL;

    [PartiallyImaginedApi(Module, "iswalnum", ApiCategory.Memory, "ucrtbase!iswalnum", "msvcrt!iswalnum",
        "api-ms-win-crt-private-l1-1-0!_o_iswalnum", "ucrtbase!_o_iswalnum")]
    private static ulong IsWideAlphaNumeric(ApiCallContext ctx) => char.IsLetterOrDigit((char)ctx.Arg32(0)) ? 1UL : 0UL;

    [PartiallyImaginedApi(Module, "iswdigit", ApiCategory.Memory, "ucrtbase!iswdigit", "msvcrt!iswdigit",
        "api-ms-win-crt-private-l1-1-0!_o_iswdigit", "ucrtbase!_o_iswdigit")]
    private static ulong IsWideDigit(ApiCallContext ctx) => (char)ctx.Arg32(0) is >= '0' and <= '9' ? 1UL : 0UL;

    [PartiallyImaginedApi(Module, "iswspace", ApiCategory.Memory, "ucrtbase!iswspace", "msvcrt!iswspace",
        "api-ms-win-crt-private-l1-1-0!_o_iswspace", "ucrtbase!_o_iswspace")]
    private static ulong IsWideSpace(ApiCallContext ctx) => char.IsWhiteSpace((char)ctx.Arg32(0)) ? 1UL : 0UL;

    [PartiallyImaginedApi(Module, "iswlower", ApiCategory.Memory, "ucrtbase!iswlower", "msvcrt!iswlower",
        "api-ms-win-crt-private-l1-1-0!_o_iswlower", "ucrtbase!_o_iswlower")]
    private static ulong IsWideLower(ApiCallContext ctx) => char.IsLower((char)ctx.Arg32(0)) ? 1UL : 0UL;

    [PartiallyImaginedApi(Module, "iswupper", ApiCategory.Memory, "ucrtbase!iswupper", "msvcrt!iswupper",
        "api-ms-win-crt-private-l1-1-0!_o_iswupper", "ucrtbase!_o_iswupper")]
    private static ulong IsWideUpper(ApiCallContext ctx) => char.IsUpper((char)ctx.Arg32(0)) ? 1UL : 0UL;

    [PartiallyImaginedApi(Module, "iswblank", ApiCategory.Memory, "ucrtbase!iswblank", "msvcrt!iswblank",
        "api-ms-win-crt-private-l1-1-0!_o_iswblank", "ucrtbase!_o_iswblank")]
    private static ulong IsWideBlank(ApiCallContext ctx) => (char)ctx.Arg32(0) is ' ' or '\t' ? 1UL : 0UL;

    [PartiallyImaginedApi(Module, "iswcntrl", ApiCategory.Memory, "ucrtbase!iswcntrl", "msvcrt!iswcntrl",
        "api-ms-win-crt-private-l1-1-0!_o_iswcntrl", "ucrtbase!_o_iswcntrl")]
    private static ulong IsWideControl(ApiCallContext ctx) => char.IsControl((char)ctx.Arg32(0)) ? 1UL : 0UL;

    [PartiallyImaginedApi(Module, "iswprint", ApiCategory.Memory, "ucrtbase!iswprint", "msvcrt!iswprint",
        "api-ms-win-crt-private-l1-1-0!_o_iswprint", "ucrtbase!_o_iswprint")]
    private static ulong IsWidePrintable(ApiCallContext ctx) => !char.IsControl((char)ctx.Arg32(0)) ? 1UL : 0UL;

    [PartiallyImaginedApi(Module, "iswgraph", ApiCategory.Memory, "ucrtbase!iswgraph", "msvcrt!iswgraph",
        "api-ms-win-crt-private-l1-1-0!_o_iswgraph", "ucrtbase!_o_iswgraph")]
    private static ulong IsWideGraphical(ApiCallContext ctx)
    {
        var c = (char)ctx.Arg32(0);
        return c > ' ' && !char.IsControl(c) ? 1UL : 0UL;
    }

    [PartiallyImaginedApi(Module, "iswpunct", ApiCategory.Memory, "ucrtbase!iswpunct", "msvcrt!iswpunct",
        "api-ms-win-crt-private-l1-1-0!_o_iswpunct", "ucrtbase!_o_iswpunct")]
    private static ulong IsWidePunctuation(ApiCallContext ctx)
    {
        var c = (char)ctx.Arg32(0);
        return c <= 0x7f && (char.IsPunctuation(c) || char.IsSymbol(c)) ? 1UL : 0UL;
    }

    [PartiallyImaginedApi(Module, "iswxdigit", ApiCategory.Memory, "ucrtbase!iswxdigit", "msvcrt!iswxdigit",
        "api-ms-win-crt-private-l1-1-0!_o_iswxdigit", "ucrtbase!_o_iswxdigit")]
    private static ulong IsWideHexDigit(ApiCallContext ctx) => (char)ctx.Arg32(0) is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F' ? 1UL : 0UL;

    [PartiallyImaginedApi(Module, "iswascii", ApiCategory.Memory, "ucrtbase!iswascii", "msvcrt!iswascii",
        "api-ms-win-crt-private-l1-1-0!_o_iswascii", "ucrtbase!_o_iswascii")]
    private static ulong IsWideAscii(ApiCallContext ctx) => ctx.Arg32(0) <= 0x7f ? 1UL : 0UL;

    [PartiallyImaginedApi(Module, "iswctype", ApiCategory.Memory, "ucrtbase!iswctype", "msvcrt!iswctype",
        "api-ms-win-crt-private-l1-1-0!_o_iswctype", "ucrtbase!_o_iswctype")]
    private static ulong IsWideType(ApiCallContext ctx)
    {
        var c = (ushort)ctx.Arg32(0);
        var type = ctx.Arg32(1);
        var result = type switch
        {
            0x0001 => char.IsUpper((char)c),                 // _UPPER
            0x0002 => char.IsLower((char)c),                 // _LOWER
            0x0004 => c is >= '0' and <= '9',                // _DIGIT
            0x0008 => char.IsWhiteSpace((char)c),            // _SPACE
            0x0010 => char.IsPunctuation((char)c) || char.IsSymbol((char)c), // _PUNCT
            0x0020 => char.IsControl((char)c),               // _CONTROL
            0x0040 => char.IsLetter((char)c),                // _ALPHA
            0x0080 => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F', // _HEX
            0x0100 => c is ' ' or '\t',                      // _BLANK
            0x0200 => char.IsLetterOrDigit((char)c),         // _ALNUM
            0x0400 => !char.IsControl((char)c),              // _GRAPH
            0x0800 => !char.IsControl((char)c),              // _PRINT
            _ => false
        };
        return result ? 1UL : 0UL;
    }

    [PartiallyImaginedApi(Module, "wcschr", ApiCategory.Memory, "ucrtbase!wcschr", "msvcrt!wcschr",
        "api-ms-win-crt-private-l1-1-0!wcschr")]
    private static ulong WcsFindChar(ApiCallContext ctx)
    {
        Span<byte> bytes = stackalloc byte[1024];
        var r = new CrtMemory.Reader(ctx, ctx.Arg(0), 2, bytes);
        for (var p = ctx.Arg(0); r.Next(out var c); p += 2)
        {
            if (c == (ushort)ctx.Arg32(1)) return p;
            if (c == 0) return 0;
        }
        ctx.Runtime.Trace.Warn($"{ctx.Name}: cannot search wide string at 0x{ctx.Arg(0):X}", ctx.Process.Id, ctx.Thread.Id);
        return 0;
    }

    [PartiallyImaginedApi(Module, "wcsrchr", ApiCategory.Memory, "ucrtbase!wcsrchr", "msvcrt!wcsrchr",
        "api-ms-win-crt-private-l1-1-0!wcsrchr")]
    private static ulong WcsFindLastChar(ApiCallContext ctx)
    {
        Span<byte> bytes = stackalloc byte[1024];
        var r = new CrtMemory.Reader(ctx, ctx.Arg(0), 2, bytes);
        ulong found = 0;
        for (var p = ctx.Arg(0); r.Next(out var c); p += 2)
        {
            if (c == (ushort)ctx.Arg32(1)) found = p;
            if (c == 0) return found;
        }
        ctx.Runtime.Trace.Warn($"{ctx.Name}: cannot search wide string at 0x{ctx.Arg(0):X}", ctx.Process.Id, ctx.Thread.Id);
        return found;
    }

    [PartiallyImaginedApi(Module, "wcsncmp", ApiCategory.Memory, "ucrtbase!wcsncmp")]
    private static ulong WcsBoundedCompare(ApiCallContext ctx) => Compare(ctx, 2, ctx.Arg(2));

    [PartiallyImaginedApi(Module, "wcsspn", ApiCategory.Memory, "ucrtbase!wcsspn")]
    private static ulong WcsSpan(ApiCallContext ctx) => ScanSet(ctx, 2, true, false);

    [PartiallyImaginedApi(Module, "wcscspn", ApiCategory.Memory, "ucrtbase!wcscspn")]
    private static ulong WcsComplementSpan(ApiCallContext ctx) => ScanSet(ctx, 2, false, false);

    [PartiallyImaginedApi(Module, "wcspbrk", ApiCategory.Memory, "ucrtbase!wcspbrk")]
    private static ulong WcsFindSet(ApiCallContext ctx) => ScanSet(ctx, 2, false, true);

    [PartiallyImaginedApi(Module, "wcstok_s", ApiCategory.Memory, "ucrtbase!wcstok_s")]
    private static ulong WcsTokenize(ApiCallContext ctx) => Tokenize(ctx, 2);

    [PartiallyImaginedApi(Module, "_wcsrev", ApiCategory.Memory, "ucrtbase!_wcsrev")]
    private static ulong WcsReverse(ApiCallContext ctx) => Reverse(ctx, 2);

    [PartiallyImaginedApi(Module, "_wcsset", ApiCategory.Memory, "ucrtbase!_wcsset")]
    private static ulong WcsSet(ApiCallContext ctx) => Set(ctx, 2, false, false);

    [PartiallyImaginedApi(Module, "_wcsnset", ApiCategory.Memory, "ucrtbase!_wcsnset")]
    private static ulong WcsBoundedSet(ApiCallContext ctx) => Set(ctx, 2, true, false);

    [PartiallyImaginedApi(Module, "_wcsset_s", ApiCategory.Memory, "ucrtbase!_wcsset_s")]
    private static ulong WcsSetSecure(ApiCallContext ctx) => Set(ctx, 2, false, true);

    [PartiallyImaginedApi(Module, "_wcsnset_s", ApiCategory.Memory, "ucrtbase!_wcsnset_s")]
    private static ulong WcsBoundedSetSecure(ApiCallContext ctx) => Set(ctx, 2, true, true);

    [PartiallyImaginedApi(Module, "memcpy_s", ApiCategory.Memory, "ucrtbase!memcpy_s")]
    private static ulong MemcpySecure(ApiCallContext ctx) => CopyMemorySecure(ctx, 1, false);

    [PartiallyImaginedApi(Module, "memmove_s", ApiCategory.Memory, "ucrtbase!memmove_s")]
    private static ulong MemmoveSecure(ApiCallContext ctx) => CopyMemorySecure(ctx, 1, true);

    [PartiallyImaginedApi(Module, "wmemcpy_s", ApiCategory.Memory, "ucrtbase!wmemcpy_s")]
    private static ulong WmemcpySecure(ApiCallContext ctx) => CopyMemorySecure(ctx, 2, false);

    [PartiallyImaginedApi(Module, "wmemmove_s", ApiCategory.Memory, "ucrtbase!wmemmove_s")]
    private static ulong WmemmoveSecure(ApiCallContext ctx) => CopyMemorySecure(ctx, 2, true);

    private static ulong IsSymbol(uint c, bool digit) =>
        c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '_' || (digit && c is >= '0' and <= '9') ? 1UL : 0UL;

    private static ulong GetLength(ApiCallContext ctx, int width, ulong limit) =>
        CrtMemory.Length(ctx, ctx.Arg(0), width, limit, out var n) ? n : 0;

    private static ulong Compare(ApiCallContext ctx, int width, ulong limit)
    {
        Span<byte> a = stackalloc byte[1024], b = stackalloc byte[1024];
        var left = new CrtMemory.Reader(ctx, ctx.Arg(0), width, a);
        var right = new CrtMemory.Reader(ctx, ctx.Arg(1), width, b);
        for (ulong i = 0; i < limit; i++)
        {
            if (!left.Next(out var x) || !right.Next(out var y)) return 0;
            if (x != y) return unchecked((uint)(x - y));
            if (x == 0) return 0;
        }
        return 0;
    }

    private static ulong CopyString(ApiCallContext ctx, int width, bool bounded, bool append)
    {
        var dest = ctx.Arg(0);
        var source = ctx.Arg(1);
        var count = bounded ? ctx.Arg(2) : ulong.MaxValue;
        var prefix = 0UL;
        if (bounded && count == 0) return dest;
        if (append && !CrtMemory.Length(ctx, dest, width, ulong.MaxValue, out prefix)) return 0;
        if (!CrtMemory.Length(ctx, source, width, count, out var n)) return 0;
        var w = (ulong)width;
        if (prefix > (ulong)long.MaxValue / w || n >= (ulong)long.MaxValue / w - prefix ||
            !CrtMemory.Range(dest, (prefix + n + 1) * w)) return 0;
        if (!CrtMemory.Copy(ctx, dest + prefix * w, source, n * w)) return 0;
        if (bounded && !append)
        {
            if (count > (ulong)long.MaxValue / w || !CrtMemory.Fill(ctx, dest + n * w, count - n, 0, width)) return 0;
        }
        else if (!CrtMemory.WriteChar(ctx, dest + (prefix + n) * w, 0, width)) return 0;
        return dest;
    }

    private static ulong CopyStringSecure(ApiCallContext ctx, int width, bool bounded, bool append)
    {
        var dest = ctx.Arg(0);
        var capacity = ctx.Arg(1);
        var source = ctx.Arg(2);
        var count = bounded ? ctx.Arg(3) : ulong.MaxValue;
        var w = (ulong)width;
        if (bounded && count == 0 && dest == 0 && capacity == 0) return 0;
        if (dest == 0 || capacity == 0 || capacity > (ulong)long.MaxValue / w || !CrtMemory.Range(dest, capacity * w))
            return CrtMemory.EInvalid;
        var prefix = 0UL;
        if (append && (!CrtMemory.Length(ctx, dest, width, capacity, out prefix) || prefix == capacity))
            return CrtMemory.ClearError(ctx, dest, capacity, width, CrtMemory.EInvalid);
        if (bounded && count == 0)
            return append || CrtMemory.WriteChar(ctx, dest, 0, width) ? 0 : CrtMemory.EInvalid;
        if (source == 0) return CrtMemory.ClearError(ctx, dest, capacity, width, CrtMemory.EInvalid);
        var available = capacity - prefix;
        var limit = bounded ? Math.Min(count, available) : available;
        if (!CrtMemory.Length(ctx, source, width, limit, out var n))
            return CrtMemory.ClearError(ctx, dest, capacity, width, CrtMemory.EInvalid);
        var truncated = n == available;
        if (truncated && (!bounded || count != ulong.MaxValue))
            return CrtMemory.ClearError(ctx, dest, capacity, width, CrtMemory.ERange);
        var readCount = n + (n < limit ? 1UL : 0UL);
        if (CrtMemory.Overlaps(dest, (prefix + Math.Min(n, available - 1) + 1) * w, source, readCount * w))
            return CrtMemory.ClearError(ctx, dest, capacity, width, CrtMemory.EInvalid);
        if (truncated) n--;
        if (!CrtMemory.Copy(ctx, dest + prefix * w, source, n * w) ||
            !CrtMemory.WriteChar(ctx, dest + (prefix + n) * w, 0, width))
            return CrtMemory.ClearError(ctx, dest, capacity, width, CrtMemory.EInvalid);
        return truncated ? CrtMemory.Truncated : 0;
    }

    private static ulong CopyMemorySecure(ApiCallContext ctx, int width, bool move)
    {
        var dest = ctx.Arg(0);
        var capacity = ctx.Arg(1);
        var source = ctx.Arg(2);
        var count = ctx.Arg(3);
        if (count == 0) return 0;
        var w = (ulong)width;
        if (dest == 0 || capacity > (ulong)long.MaxValue / w || !CrtMemory.Range(dest, capacity * w)) return CrtMemory.EInvalid;
        var error = source == 0 ? CrtMemory.EInvalid : count > capacity ? CrtMemory.ERange : 0;
        if (error == 0 && (!CrtMemory.Range(source, count * w) ||
            (!move && CrtMemory.Overlaps(dest, count * w, source, count * w)))) error = CrtMemory.EInvalid;
        if (error != 0)
        {
            if (!move) CrtMemory.Fill(ctx, dest, capacity, 0, width);
            return error;
        }
        return CrtMemory.Copy(ctx, dest, source, count * w) ? 0 : CrtMemory.EInvalid;
    }

    private static ulong Set(ApiCallContext ctx, int width, bool bounded, bool secure)
    {
        var dest = ctx.Arg(0);
        var capacity = secure ? ctx.Arg(1) : ulong.MaxValue;
        var count = bounded ? ctx.Arg(secure ? 3 : 2) : ulong.MaxValue;
        if (!secure && bounded && count == 0) return dest;
        if (secure && bounded && count == 0 && dest == 0 && capacity == 0) return 0;
        if (secure && (dest == 0 || capacity == 0 || capacity > (ulong)long.MaxValue / (ulong)width)) return CrtMemory.EInvalid;
        if (!CrtMemory.Length(ctx, dest, width, capacity, out var n) || (secure && n == capacity))
            return secure ? CrtMemory.ClearError(ctx, dest, capacity, width, CrtMemory.EInvalid) : 0;
        var ok = CrtMemory.Fill(ctx, dest, Math.Min(n, count), (ushort)ctx.Arg32(secure ? 2 : 1), width);
        return secure ? (ok ? 0 : CrtMemory.EInvalid) : (ok ? dest : 0);
    }

    private static ulong Reverse(ApiCallContext ctx, int width)
    {
        var dest = ctx.Arg(0);
        if (!CrtMemory.Length(ctx, dest, width, ulong.MaxValue, out var length)) return 0;
        Span<byte> a = stackalloc byte[1024], b = stackalloc byte[1024];
        var w = (ulong)width;
        for (ulong done = 0; done < length / 2;)
        {
            var n = Math.Min((ulong)(a.Length / width), length / 2 - done);
            var bytes = (int)(n * w);
            var left = dest + done * w;
            var right = dest + (length - done - n) * w;
            if (!ctx.Memory.TryRead(left, a[..bytes]) || !ctx.Memory.TryRead(right, b[..bytes])) return 0;
            ReverseChars(a[..bytes], width);
            ReverseChars(b[..bytes], width);
            if (!ctx.Memory.TryWrite(right, a[..bytes]) || !ctx.Memory.TryWrite(left, b[..bytes])) return 0;
            done += n;
        }
        return dest;
    }

    private static void ReverseChars(Span<byte> bytes, int width)
    {
        for (var i = 0; i < bytes.Length / 2; i += width)
            for (var j = 0; j < width; j++)
            {
                var k = bytes.Length - width - i + j;
                (bytes[i + j], bytes[k]) = (bytes[k], bytes[i + j]);
            }
    }

    private static ulong CopyUntil(ApiCallContext ctx)
    {
        var dest = ctx.Arg(0);
        var source = ctx.Arg(1);
        var count = ctx.Arg(3);
        if (!CrtMemory.Range(dest, count) || !CrtMemory.Range(source, count)) return 0;
        Span<byte> bytes = stackalloc byte[1024];
        for (ulong done = 0; done < count;)
        {
            var n = (int)Math.Min(Math.Min((ulong)bytes.Length, count - done), 4096 - ((source + done) & 4095));
            if (!ctx.Memory.TryRead(source + done, bytes[..n])) return 0;
            var found = bytes[..n].IndexOf((byte)ctx.Arg32(2));
            if (found >= 0) n = found + 1;
            if (!ctx.Memory.TryWrite(dest + done, bytes[..n])) return 0;
            done += (ulong)n;
            if (found >= 0) return dest + done;
        }
        return 0;
    }

    private static bool ReadSet(ApiCallContext ctx, ulong address, int width, out HashSet<ushort> set)
    {
        set = new();
        Span<byte> bytes = stackalloc byte[1024];
        var r = new CrtMemory.Reader(ctx, address, width, bytes);
        while (r.Next(out var c))
        {
            if (c == 0) return true;
            set.Add(c);
        }
        return false;
    }

    private static ulong ScanSet(ApiCallContext ctx, int width, bool accept, bool pointer)
    {
        if (!ReadSet(ctx, ctx.Arg(1), width, out var set)) return 0;
        Span<byte> bytes = stackalloc byte[1024];
        var r = new CrtMemory.Reader(ctx, ctx.Arg(0), width, bytes);
        for (ulong n = 0; r.Next(out var c); n++)
        {
            if (c == 0) return pointer ? 0 : n;
            if (set.Contains(c) != accept) return pointer ? ctx.Arg(0) + n * (ulong)width : n;
        }
        return 0;
    }

    private static ulong Tokenize(ApiCallContext ctx, int width)
    {
        if (ctx.Arg(2) == 0 || !ReadSet(ctx, ctx.Arg(1), width, out var set)) return 0;
        var source = ctx.Arg(0);
        if (source == 0 && !CrtMemory.Read(ctx, ctx.Arg(2), out source)) return 0;
        if (source == 0) return 0;
        Span<byte> bytes = stackalloc byte[1024];
        var r = new CrtMemory.Reader(ctx, source, width, bytes);
        var token = 0UL;
        for (var p = source; r.Next(out var c); p += (ulong)width)
        {
            if (c == 0)
                return CrtMemory.Write(ctx, ctx.Arg(2), p) ? token : 0;
            if (token == 0)
            {
                if (!set.Contains(c)) token = p;
            }
            else if (set.Contains(c))
            {
                if (!CrtMemory.WriteChar(ctx, p, 0, width) || !CrtMemory.Write(ctx, ctx.Arg(2), p + (ulong)width)) return 0;
                return token;
            }
        }
        return 0;
    }
}
