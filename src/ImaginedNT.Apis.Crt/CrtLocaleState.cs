using System.Buffers.Binary;
using ImaginedNT.Core.Memory;
using ImaginedNT.Core.Nt;
using ImaginedNT.Core.Runtime;

namespace ImaginedNT.Apis.Crt;

internal sealed partial class CrtProcessState
{
    private readonly object _localeGate = new();
    private int _localeOwner, _localeRecursion;
    private CrtLocaleData? _globalLocale;
    private readonly Dictionary<int, CrtLocaleData> _threadLocales = new();
    private readonly Dictionary<ulong, CrtLocaleData> _localeHandles = new();
    private bool _unsignedLocaleChar;

    internal static ulong LockLocales(ApiCallContext ctx)
    {
        var state = States.GetValue(ctx.Process, static _ => new CrtProcessState());
        lock (state._localeGate)
        {
            while (state._localeRecursion != 0 && state._localeOwner != ctx.Thread.Id)
                Monitor.Wait(state._localeGate);
            state._localeOwner = ctx.Thread.Id;
            state._localeRecursion++;
        }
        return 0;
    }

    internal static ulong UnlockLocales(ApiCallContext ctx)
    {
        var state = States.GetValue(ctx.Process, static _ => new CrtProcessState());
        lock (state._localeGate)
        {
            if (state._localeRecursion == 0 || state._localeOwner != ctx.Thread.Id)
            {
                ctx.Runtime.Trace.Warn($"{ctx.Name}: locale lock is not owned by the calling thread", ctx.Process.Id, ctx.Thread.Id);
                return 0;
            }
            if (--state._localeRecursion == 0)
            {
                state._localeOwner = 0;
                Monitor.PulseAll(state._localeGate);
            }
        }
        return 0;
    }

    internal static ulong LocaleCall(ApiCallContext ctx)
    {
        var name = ctx.Name.StartsWith("_o_", StringComparison.Ordinal) ? ctx.Name[3..] : ctx.Name;
        if (name == "_lock_locales") return LockLocales(ctx);
        if (name == "_unlock_locales") return UnlockLocales(ctx);
        LockLocales(ctx);
        try
        {
            return States.GetValue(ctx.Process, static _ => new CrtProcessState()).InvokeLocale(ctx, name);
        }
        catch (Exception ex)
        {
            CrtLocaleData.SetErrno(ex is OutOfMemoryException ? 12 : 22);
            ctx.Runtime.Trace.Warn($"{ctx.Name}: {ex}", ctx.Process.Id, ctx.Thread.Id);
            return name is "_configthreadlocale" or "_setmbcp" ? uint.MaxValue : 0;
        }
        finally
        {
            UnlockLocales(ctx);
        }
    }

    private ulong InvokeLocale(ApiCallContext ctx, string name)
    {
        _globalLocale ??= CrtLocaleData.Create(null, 0, "C", null)
            ?? throw new InvalidOperationException("Cannot initialize the C locale");
        var local = _threadLocales.TryGetValue(ctx.Thread.Id, out var current);
        current ??= _globalLocale;
        switch (name)
        {
            case "_configthreadlocale":
                var previous = local ? 1UL : 2UL;
                switch (ctx.ArgInt32(0))
                {
                    case 0: return previous;
                    case 1:
                        if (!local)
                        {
                            current.References++;
                            _threadLocales.Add(ctx.Thread.Id, current);
                        }
                        return previous;
                    case 2:
                        if (_threadLocales.Remove(ctx.Thread.Id)) ReleaseLocale(ctx, current);
                        return previous;
                    default: return LocaleError(ctx, "invalid thread locale mode", uint.MaxValue);
                }
            case "_free_locale":
                if (ctx.Arg(0) == 0) return 0;
                if (!_localeHandles.Remove(ctx.Arg(0), out var freed))
                    return LocaleError(ctx, "invalid locale handle", 0);
                ctx.Memory.TryFree(ctx.Arg(0), 0, FreeType.Release);
                ReleaseLocale(ctx, freed);
                return 0;
            case "___mb_cur_max_l_func":
                if (ctx.Arg(0) == 0) return (ulong)current.MbCurMax;
                return _localeHandles.TryGetValue(ctx.Arg(0), out var specific)
                    ? (ulong)specific.MbCurMax : LocaleError(ctx, "invalid locale handle", 0);
            case "___lc_codepage_func": return current.CodePage;
            case "___lc_collate_cp_func": return current.CollateCodePage;
            case "___mb_cur_max_func": return (ulong)current.MbCurMax;
            case "_getmbcp": return (ulong)current.MultibyteCodePage;
            case "__initialize_lconv_for_unsigned_char":
                _unsignedLocaleChar = true;
                foreach (var data in _threadLocales.Values.Concat(_localeHandles.Values).Append(_globalLocale).Distinct())
                    UpdateUnsignedLconv(ctx, data);
                return 0;
            case "_get_current_locale": return CreateLocaleHandle(ctx, current, addReference: true);
            case "_create_locale":
            case "_wcreate_locale":
            case "setlocale":
            case "_wsetlocale":
                var category = ctx.ArgInt32(0);
                if (category is < 0 or > 5) return LocaleError(ctx, "invalid locale category", 0);
                var width = name.StartsWith("_w", StringComparison.Ordinal) ? 2 : 1;
                var create = name is "_create_locale" or "_wcreate_locale";
                if (ctx.Arg(1) == 0)
                {
                    if (create) return LocaleError(ctx, "null locale name", 0);
                    return LocalePointer(ctx, current, width == 1 ? current.NarrowCategories[category] : current.WideCategories[category]);
                }
                if (!CrtMemory.ReadText(ctx, ctx.Arg(1), width, out var value, ansi: true, maxLength: 32766))
                    return LocaleError(ctx, "cannot read locale name", 0);
                var updated = CrtLocaleData.Create(create ? null : current, category, value, null, localeObject: create);
                if (updated is null) return LocaleError(ctx, $"unsupported locale '{value}'", 0);
                if (create) return CreateLocaleHandle(ctx, updated, addReference: false);
                var result = LocalePointer(ctx, updated, width == 1 ? updated.NarrowCategories[category] : updated.WideCategories[category]);
                if (result == 0) return 0;
                SetCurrentLocale(ctx, current, updated, local);
                return result;
            case "_setmbcp":
                var changed = CrtLocaleData.Create(current, 0, null, ctx.ArgInt32(0));
                if (changed is null) return LocaleError(ctx, "invalid multibyte code page", uint.MaxValue);
                SetCurrentLocale(ctx, current, changed, local);
                return 0;
            case "__pctype_func": return LocalePointer(ctx, current, current.Pctype);
            case "__pwctype_func": return LocalePointer(ctx, current, current.Wctype);
            case "___lc_locale_name_func": return LocalePointer(ctx, current, current.LocaleNames);
            case "localeconv": return LocalePointer(ctx, current, current.Lconv);
            default: throw new InvalidOperationException($"Unknown locale API {name}");
        }
    }

    private void SetCurrentLocale(ApiCallContext ctx, CrtLocaleData old, CrtLocaleData data, bool local)
    {
        if (local) _threadLocales[ctx.Thread.Id] = data;
        else _globalLocale = data;
        ReleaseLocale(ctx, old);
    }

    private static void ReleaseLocale(ApiCallContext ctx, CrtLocaleData data)
    {
        if (--data.References == 0 && data.Address != 0)
        {
            ctx.Memory.TryFree(data.Address, 0, FreeType.Release);
            data.Address = 0;
        }
    }

    private ulong CreateLocaleHandle(ApiCallContext ctx, CrtLocaleData data, bool addReference)
    {
        if (LocalePointer(ctx, data, 0) == 0) return 0;
        var status = ctx.Memory.TryAllocate(0, 16, MemoryProtection.ReadWrite,
            AllocationType.Reserve | AllocationType.Commit, out var address, out _, "CRT locale handle");
        if (status != NtStatus.Success)
        {
            if (!addReference) ReleaseLocale(ctx, data);
            return LocaleError(ctx, $"cannot allocate locale handle: {NtStatus.NameOf(status)}", 0);
        }
        Span<byte> handle = stackalloc byte[16];
        handle.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(handle, data.Address);
        BinaryPrimitives.WriteUInt64LittleEndian(handle[8..], data.Address + (ulong)data.Mbcinfo);
        if (!ctx.Memory.TryWrite(address, handle))
        {
            ctx.Memory.TryFree(address, 0, FreeType.Release);
            if (!addReference) ReleaseLocale(ctx, data);
            return LocaleError(ctx, "cannot write locale handle", 0);
        }
        if (addReference) data.References++;
        _localeHandles.Add(address, data);
        return address;
    }

    private ulong LocalePointer(ApiCallContext ctx, CrtLocaleData data, int offset)
    {
        if (data.Address == 0)
        {
            var status = ctx.Memory.TryAllocate(0, (ulong)data.Bytes.Length, MemoryProtection.ReadWrite,
                AllocationType.Reserve | AllocationType.Commit, out var address, out _, "CRT locale data");
            if (status != NtStatus.Success) return LocaleError(ctx, $"cannot allocate locale data: {NtStatus.NameOf(status)}", 0);
            var bytes = (byte[])data.Bytes.Clone();
            foreach (var (field, target) in data.Pointers)
                BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(field), address + (ulong)target);
            if (!ctx.Memory.TryWrite(address, bytes))
            {
                ctx.Memory.TryFree(address, 0, FreeType.Release);
                return LocaleError(ctx, "cannot write locale data", 0);
            }
            data.Address = address;
            UpdateUnsignedLconv(ctx, data);
        }
        return data.Address + (ulong)offset;
    }

    private void UpdateUnsignedLconv(ApiCallContext ctx, CrtLocaleData data)
    {
        if (!_unsignedLocaleChar || data.Address == 0 || data.Categories[3] != "C") return;
        Span<byte> fields = stackalloc byte[8];
        fields.Fill(255);
        if (!ctx.Memory.TryWrite(data.Address + (ulong)data.Lconv + 80, fields))
            LocaleError(ctx, "cannot update unsigned-char locale conventions", 0);
    }

    private static ulong LocaleError(ApiCallContext ctx, string message, ulong result)
    {
        CrtLocaleData.SetErrno(message.StartsWith("cannot allocate", StringComparison.Ordinal) ? 12 : 22);
        ctx.Runtime.Trace.Warn($"{ctx.Name}: {message}", ctx.Process.Id, ctx.Thread.Id);
        return result;
    }
}
