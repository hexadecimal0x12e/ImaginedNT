using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using ImaginedNT.Core.Nt;
using ImaginedNT.Core.Objects;

namespace ImaginedNT.Core.Runtime;

public static class ImaginedRegistry
{
    private const uint QueryValue = 0x0001;
    private const uint SetValue = 0x0002;
    private const uint CreateSubKey = 0x0004;
    private const uint EnumerateSubKeys = 0x0008;
    private const uint Delete = 0x00010000;
    private const uint KeyRead = 0x00020019;
    private const uint KeyAllAccess = 0x000F003F;
    private const uint MoreData = 234;
    private const uint NoMoreItems = 259;
    private const uint KeyDeleted = 1018;
    private static readonly Lazy<Encoding> Ansi = new(() =>
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding((int)GetACP());
    });

    [DllImport("kernel32.dll")]
    private static extern uint GetACP();

    private sealed class RegistryKeyObject(RegistryStore store, RegistryKeyData key, string path) : NtObject
    {
        public RegistryStore Store { get; } = store;
        public RegistryKeyData Key { get; } = key;
        public string Path { get; } = path;
        public override ObjectType ObjectType => ObjectType.RegistryKey;
        public override string? Info => Path;
    }

    private static RegistryStore GetStore(ImaginedRuntime runtime, string sandboxRoot) =>
        RegistryStore.Get(runtime, sandboxRoot);

    public static RegistryKeySnapshot? GetSnapshot(ImaginedRuntime runtime, string sandboxRoot, string path)
    {
        var parts = path.Split('\\', 2, StringSplitOptions.RemoveEmptyEntries);
        var root = RegistryStore.RootNames.FirstOrDefault(p =>
            string.Equals(p.Value, parts.FirstOrDefault(), StringComparison.OrdinalIgnoreCase));
        if (root.Value is null) return null;
        var store = GetStore(runtime, sandboxRoot);
        lock (store.Gate)
        {
            var key = RegistryStore.Find(store.Root(root.Key), parts.Length > 1 ? parts[1] : "");
            if (key is null) return null;
            return new(path, key.SubKeys.Keys.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
                key.Values.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(p => new RegistryValueSnapshot(p.Key, p.Value.Type, p.Value.Data.ToArray())).ToArray());
        }
    }

    private static bool IsRoot(ulong handle) =>
        (handle == (uint)handle || handle == unchecked((ulong)(long)(int)handle)) &&
        RegistryStore.RootNames.ContainsKey((uint)handle);

    private static ulong WithKey(ApiCallContext ctx, uint access, Func<RegistryKeyObject, uint> action)
    {
        try
        {
            RegistryKeyObject key;
            if (IsRoot(ctx.Arg(0)))
            {
                var store = GetStore(ctx.Runtime, ctx.Process.FileSystem.Root);
                key = new(store, store.Root(ctx.Arg32(0)), RegistryStore.RootNames[ctx.Arg32(0)]);
            }
            else
            {
                var status = ctx.Handles.TryGet(ctx.Arg(0), access, out var obj);
                if (status == NtStatus.AccessDenied) return Win32Error.AccessDenied;
                if (status != NtStatus.Success || obj is not RegistryKeyObject k) return Win32Error.InvalidHandle;
                key = k;
            }

            lock (key.Store.Gate)
            {
                if (key.Key.Deleted) return KeyDeleted;
                if ((access & (SetValue | CreateSubKey | Delete)) != 0 && !ctx.Process.FileSystem.AllowWrites)
                    return Win32Error.AccessDenied;
                return action(key);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        {
            ctx.Runtime.Trace.Error($"{ctx.Name}: registry persistence failed: {ex}", ctx.Process.Id, ctx.Thread.Id);
            return ex switch
            {
                UnauthorizedAccessException => Win32Error.AccessDenied,
                JsonException or InvalidDataException => 1009, // ERROR_BADDB
                _ => 1016 // ERROR_REGISTRY_IO_FAILED
            };
        }
    }

    private static uint NormalizeAccess(ref uint access)
    {
        if ((access & 0x0300) == 0x0300) return Win32Error.InvalidParameter;
        if ((access & 0x0200) != 0) return Win32Error.NotSupported;
        access &= ~0x0100u; // This runtime has a single 64-bit registry view.
        if ((access & 0x12000000) != 0) access = (access & ~0x12000000u) | KeyAllAccess;
        if ((access & 0x80000000) != 0) access = (access & ~0x80000000u) | KeyRead;
        if ((access & 0x40000000) != 0) access = (access & ~0x40000000u) | 0x00020006;
        if ((access & 0x20000000) != 0) access = (access & ~0x20000000u) | KeyRead;
        return (access & ~KeyAllAccess) == 0 ? 0u : Win32Error.InvalidParameter;
    }

    private static bool Unicode(ApiCallContext ctx) => ctx.Name.EndsWith('W');
    private static Encoding TextEncoding(ApiCallContext ctx) => Unicode(ctx) ? Encoding.Unicode : Ansi.Value;

    private static string ReadString(ApiCallContext ctx, ulong address)
    {
        if (address == 0) return "";
        if (Unicode(ctx)) return ctx.ReadUnicodeString(address);
        var bytes = new List<byte>();
        for (var i = 0; i < 32768; i++)
        {
            var b = ctx.Read<byte>(address + (ulong)i);
            if (b == 0) break;
            bytes.Add(b);
        }
        return Ansi.Value.GetString(bytes.ToArray());
    }

    private static string ReadPath(ApiCallContext ctx, int index) => ReadString(ctx, ctx.Arg(index));
    private static bool ValidPath(string path) => !path.StartsWith('\\') &&
        path.Split('\\', StringSplitOptions.RemoveEmptyEntries).All(p => p.Length <= 255);

    private static ulong NewHandle(ApiCallContext ctx, RegistryKeyObject parent, RegistryKeyData key,
        string path, uint access, bool inherit = false)
    {
        var suffix = string.Join("\\", path.Split('\\', StringSplitOptions.RemoveEmptyEntries));
        using var obj = new RegistryKeyObject(parent.Store, key,
            suffix.Length == 0 ? parent.Path : parent.Path + "\\" + suffix);
        return ctx.Handles.Create(obj, access, name: obj.Path, inheritable: inherit);
    }

    [VirtualApi("advapi32.dll", "RegOpenKeyExW", ApiCategory.Registry, "kernelbase!RegOpenKeyExW")]
    [VirtualApi("advapi32.dll", "RegOpenKeyExA", ApiCategory.Registry, "kernelbase!RegOpenKeyExA")]
    [VirtualApi("advapi32.dll", "RegOpenKeyW", ApiCategory.Registry, "kernelbase!RegOpenKeyW")]
    [VirtualApi("advapi32.dll", "RegOpenKeyA", ApiCategory.Registry, "kernelbase!RegOpenKeyA")]
    private static ulong RegOpenKey(ApiCallContext ctx)
    {
        var extended = ctx.Name.Contains("Ex", StringComparison.Ordinal);
        var result = ctx.Arg(extended ? 4 : 2);
        if (result == 0) return Win32Error.InvalidParameter;
        ctx.Write(result, 0UL);
        if (extended && ctx.Arg32(2) != 0) return Win32Error.NotSupported;
        var access = extended ? ctx.Arg32(3) : KeyAllAccess;
        var error = NormalizeAccess(ref access);
        if (error != 0) return error;
        var path = ReadPath(ctx, 1);
        if (!ValidPath(path)) return Win32Error.InvalidParameter;
        return WithKey(ctx, 0, key =>
        {
            var target = RegistryStore.Find(key.Key, path);
            if (target is null) return Win32Error.FileNotFound;
            ctx.Write(result, NewHandle(ctx, key, target, path, access));
            return 0;
        });
    }

    [VirtualApi("advapi32.dll", "RegCreateKeyExW", ApiCategory.Registry, "kernelbase!RegCreateKeyExW")]
    [VirtualApi("advapi32.dll", "RegCreateKeyExA", ApiCategory.Registry, "kernelbase!RegCreateKeyExA")]
    [VirtualApi("advapi32.dll", "RegCreateKeyW", ApiCategory.Registry, "kernelbase!RegCreateKeyW")]
    [VirtualApi("advapi32.dll", "RegCreateKeyA", ApiCategory.Registry, "kernelbase!RegCreateKeyA")]
    private static ulong RegCreateKey(ApiCallContext ctx)
    {
        var extended = ctx.Name.Contains("Ex", StringComparison.Ordinal);
        var result = ctx.Arg(extended ? 7 : 2);
        if (result == 0) return Win32Error.InvalidParameter;
        ctx.Write(result, 0UL);
        if (extended && ctx.Arg32(2) != 0) return Win32Error.InvalidParameter;
        if (extended && ctx.Arg32(4) != 0) return Win32Error.NotSupported;
        var access = extended ? ctx.Arg32(5) : KeyAllAccess;
        var error = NormalizeAccess(ref access);
        if (error != 0) return error;
        var path = ReadPath(ctx, 1);
        if (!ValidPath(path)) return Win32Error.InvalidParameter;
        var className = extended ? ReadPath(ctx, 3) : "";
        return WithKey(ctx, CreateSubKey, key =>
        {
            var target = key.Store.Create(key.Key, path, className, out var created);
            var inherit = extended && ctx.Arg(6) != 0 && ctx.Read<int>(ctx.Arg(6) + 16) != 0;
            ctx.Write(result, NewHandle(ctx, key, target, path, access, inherit));
            if (extended) ctx.WriteUInt32IfNotNull(ctx.Arg(8), created ? 1u : 2u);
            return 0;
        });
    }

    [VirtualApi("advapi32.dll", "RegCloseKey", ApiCategory.Registry, "kernelbase!RegCloseKey")]
    private static ulong RegCloseKey(ApiCallContext ctx)
    {
        if (IsRoot(ctx.Arg(0))) return 0;
        if (!ctx.Handles.TryGet(ctx.Arg(0), out var obj) || obj is not RegistryKeyObject)
            return Win32Error.InvalidHandle;
        return ctx.Handles.Close(ctx.Arg(0)) == NtStatus.Success ? 0UL : Win32Error.InvalidHandle;
    }

    private static bool IsStringType(uint type) => type is 1 or 2 or 7;

    private static byte[] ValueBytes(ApiCallContext ctx, RegistryValueData value) =>
        !Unicode(ctx) && IsStringType(value.Type)
            ? Ansi.Value.GetBytes(Encoding.Unicode.GetString(value.Data)) : value.Data;

    private static uint WriteValue(ApiCallContext ctx, RegistryValueData value, ulong type, ulong data, ulong size)
    {
        if (data != 0 && size == 0) return Win32Error.InvalidParameter;
        var bytes = ValueBytes(ctx, value);
        var capacity = size == 0 ? 0 : ctx.Read<uint>(size);
        ctx.WriteUInt32IfNotNull(type, value.Type);
        ctx.WriteUInt32IfNotNull(size, (uint)bytes.Length);
        if (data == 0) return 0;
        if (capacity < bytes.Length) return MoreData;
        ctx.WriteBytes(data, bytes);
        return 0;
    }

    [VirtualApi("advapi32.dll", "RegSetValueExW", ApiCategory.Registry, "kernelbase!RegSetValueExW")]
    [VirtualApi("advapi32.dll", "RegSetValueExA", ApiCategory.Registry, "kernelbase!RegSetValueExA")]
    [VirtualApi("advapi32.dll", "RegSetKeyValueW", ApiCategory.Registry, "kernelbase!RegSetKeyValueW")]
    [VirtualApi("advapi32.dll", "RegSetKeyValueA", ApiCategory.Registry, "kernelbase!RegSetKeyValueA")]
    private static ulong RegSetValueEx(ApiCallContext ctx)
    {
        var subkey = ctx.Name.Contains("KeyValue", StringComparison.Ordinal);
        if ((!subkey && ctx.Arg32(2) != 0) || ctx.Arg32(5) > int.MaxValue || (ctx.Arg(4) == 0 && ctx.Arg32(5) != 0))
            return Win32Error.InvalidParameter;
        var path = subkey ? ReadPath(ctx, 1) : "";
        if (!ValidPath(path)) return Win32Error.InvalidParameter;
        var name = ReadPath(ctx, subkey ? 2 : 1);
        var type = ctx.Arg32(3);
        byte[] bytes = ctx.Arg32(5) == 0 ? [] : ctx.ReadBytes(ctx.Arg(4), (int)ctx.Arg32(5));
        if (IsStringType(type))
        {
            if (Unicode(ctx) && (bytes.Length & 1) != 0) return Win32Error.InvalidParameter;
            if (!Unicode(ctx)) bytes = Encoding.Unicode.GetBytes(Ansi.Value.GetString(bytes));
        }
        return WithKey(ctx, SetValue, key =>
        {
            var target = key.Store.Create(key.Key, path, "", out _);
            key.Store.SetValue(target, name, type, bytes);
            return 0;
        });
    }

    [VirtualApi("advapi32.dll", "RegQueryValueExW", ApiCategory.Registry, "kernelbase!RegQueryValueExW")]
    [VirtualApi("advapi32.dll", "RegQueryValueExA", ApiCategory.Registry, "kernelbase!RegQueryValueExA")]
    private static ulong RegQueryValueEx(ApiCallContext ctx)
    {
        if (ctx.Arg(2) != 0 || (ctx.Arg(4) != 0 && ctx.Arg(5) == 0)) return Win32Error.InvalidParameter;
        var name = ReadPath(ctx, 1);
        return WithKey(ctx, QueryValue, key => key.Key.Values.TryGetValue(name, out var value)
            ? WriteValue(ctx, value, ctx.Arg(3), ctx.Arg(4), ctx.Arg(5)) : Win32Error.FileNotFound);
    }

    [VirtualApi("advapi32.dll", "RegGetValueW", ApiCategory.Registry, "kernelbase!RegGetValueW")]
    [VirtualApi("advapi32.dll", "RegGetValueA", ApiCategory.Registry, "kernelbase!RegGetValueA")]
    private static ulong RegGetValue(ApiCallContext ctx)
    {
        var flags = ctx.Arg32(3);
        if (ctx.Arg(5) != 0 && ctx.Arg(6) == 0) return Win32Error.InvalidParameter;
        var capacity = ctx.Arg(6) == 0 ? 0 : ctx.Read<uint>(ctx.Arg(6));
        var path = ReadPath(ctx, 1);
        var name = ReadPath(ctx, 2);
        var result = WithKey(ctx, QueryValue, key =>
        {
            if (!ValidPath(path) || (flags & ~0x3003FFFFu) != 0 || (flags & 0x00030000) == 0x00030000 ||
                ((flags & 0xFFFF) == 4 && (flags & 0x10000000) == 0))
                return Win32Error.InvalidParameter;
            if ((flags & 0x00020000) != 0) return Win32Error.NotSupported;
            var target = RegistryStore.Find(key.Key, path);
            if (target is null || !target.Values.TryGetValue(name, out var value)) return Win32Error.FileNotFound;
            if (IsStringType(value.Type))
            {
                var text = Encoding.Unicode.GetString(value.Data);
                var type = value.Type;
                if (type == 2 && (flags & 0x10000000) == 0)
                {
                    text = text.Split('\0')[0];
                    text = ctx.Process.ExpandEnvironmentVariables(text);
                    type = 1;
                }
                var terminator = type == 7 ? "\0\0" : "\0";
                while (!text.EndsWith(terminator, StringComparison.Ordinal)) text += '\0';
                value = new() { Type = type, Data = Encoding.Unicode.GetBytes(text) };
            }
            var typeFlag = value.Type switch
            {
                0 => 1u,
                1 => 2u,
                2 => 4u,
                3 => 8u,
                4 => 16u,
                7 => 32u,
                11 => 64u,
                _ => 0u
            };
            var types = flags & 0xFFFF;
            ctx.WriteUInt32IfNotNull(ctx.Arg(4), value.Type);
            if (types is not 0 and not 0xFFFF && (types & typeFlag) == 0) return 1630; // ERROR_UNSUPPORTED_TYPE
            if (value.Type == 3 && ((types == 0x18 && value.Data.Length != 4) || (types == 0x48 && value.Data.Length != 8)))
                return 1629; // ERROR_DATATYPE_MISMATCH
            return WriteValue(ctx, value, ctx.Arg(4), ctx.Arg(5), ctx.Arg(6));
        });
        if (result != 0 && (flags & 0x20000000) != 0 && ctx.Arg(5) != 0)
        {
            var zeros = new byte[4096];
            for (ulong i = 0; i < capacity; i += (ulong)zeros.Length)
                ctx.WriteBytes(ctx.Arg(5) + i, zeros.AsSpan(0, (int)Math.Min((ulong)zeros.Length, capacity - i)));
        }
        return result;
    }

    [VirtualApi("advapi32.dll", "RegDeleteValueW", ApiCategory.Registry, "kernelbase!RegDeleteValueW")]
    [VirtualApi("advapi32.dll", "RegDeleteValueA", ApiCategory.Registry, "kernelbase!RegDeleteValueA")]
    [VirtualApi("advapi32.dll", "RegDeleteKeyValueW", ApiCategory.Registry, "kernelbase!RegDeleteKeyValueW")]
    [VirtualApi("advapi32.dll", "RegDeleteKeyValueA", ApiCategory.Registry, "kernelbase!RegDeleteKeyValueA")]
    private static ulong RegDeleteValue(ApiCallContext ctx)
    {
        var subkey = ctx.Name.Contains("KeyValue", StringComparison.Ordinal);
        var path = subkey ? ReadPath(ctx, 1) : "";
        var name = ReadPath(ctx, subkey ? 2 : 1);
        if (!ValidPath(path)) return Win32Error.InvalidParameter;
        return WithKey(ctx, SetValue, key =>
        {
            var target = RegistryStore.Find(key.Key, path);
            if (target is null || !target.Values.ContainsKey(name)) return Win32Error.FileNotFound;
            key.Store.DeleteValue(target, name);
            return 0;
        });
    }

    [VirtualApi("advapi32.dll", "RegDeleteKeyW", ApiCategory.Registry, "kernelbase!RegDeleteKeyW")]
    [VirtualApi("advapi32.dll", "RegDeleteKeyA", ApiCategory.Registry, "kernelbase!RegDeleteKeyA")]
    [VirtualApi("advapi32.dll", "RegDeleteKeyExW", ApiCategory.Registry, "kernelbase!RegDeleteKeyExW")]
    [VirtualApi("advapi32.dll", "RegDeleteKeyExA", ApiCategory.Registry, "kernelbase!RegDeleteKeyExA")]
    [VirtualApi("advapi32.dll", "RegDeleteTreeW", ApiCategory.Registry, "kernelbase!RegDeleteTreeW")]
    [VirtualApi("advapi32.dll", "RegDeleteTreeA", ApiCategory.Registry, "kernelbase!RegDeleteTreeA")]
    private static ulong RegDeleteKey(ApiCallContext ctx)
    {
        var recursive = ctx.Name.Contains("Tree", StringComparison.Ordinal);
        if (ctx.Name.Contains("Ex", StringComparison.Ordinal))
        {
            var view = ctx.Arg32(2);
            if ((view & ~0x0300u) != 0 || ctx.Arg32(3) != 0) return Win32Error.InvalidParameter;
            var error = NormalizeAccess(ref view);
            if (error != 0) return error;
        }
        var path = ReadPath(ctx, 1);
        if (!ValidPath(path)) return Win32Error.InvalidParameter;
        return WithKey(ctx, recursive ? Delete | EnumerateSubKeys | QueryValue | SetValue : CreateSubKey, key =>
        {
            var parts = path.Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                if (!recursive) return Win32Error.InvalidParameter;
                key.Store.Clear(key.Key);
                return 0;
            }
            var parent = RegistryStore.Find(key.Key, string.Join("\\", parts[..^1]));
            if (parent is null || !parent.SubKeys.TryGetValue(parts[^1], out var child)) return Win32Error.FileNotFound;
            if (!recursive && child.SubKeys.Count != 0) return Win32Error.AccessDenied;
            key.Store.DeleteKey(parent, parts[^1]);
            return 0;
        });
    }

    private static uint StringLength(ApiCallContext ctx, string value) =>
        (uint)(Unicode(ctx) ? value.Length : Ansi.Value.GetByteCount(value));

    private static uint WriteString(ApiCallContext ctx, string value, ulong buffer, ulong size)
    {
        if (size == 0) return buffer == 0 ? 0u : Win32Error.InvalidParameter;
        var capacity = ctx.Read<uint>(size);
        var length = StringLength(ctx, value);
        ctx.Write(size, length);
        if (buffer == 0) return 0;
        if (capacity <= length) return MoreData;
        ctx.WriteBytes(buffer, TextEncoding(ctx).GetBytes(value + '\0'));
        return 0;
    }

    [VirtualApi("advapi32.dll", "RegEnumKeyExW", ApiCategory.Registry, "kernelbase!RegEnumKeyExW")]
    [VirtualApi("advapi32.dll", "RegEnumKeyExA", ApiCategory.Registry, "kernelbase!RegEnumKeyExA")]
    private static ulong RegEnumKeyEx(ApiCallContext ctx)
    {
        if (ctx.Arg(2) == 0 || ctx.Arg(3) == 0 || ctx.Arg(4) != 0 || (ctx.Arg(5) != 0 && ctx.Arg(6) == 0))
            return Win32Error.InvalidParameter;
        return WithKey(ctx, EnumerateSubKeys, key =>
        {
            var children = key.Key.SubKeys.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).ToArray();
            if (ctx.Arg32(1) >= children.Length) return NoMoreItems;
            var child = children[(int)ctx.Arg32(1)];
            var error = WriteString(ctx, child.Key, ctx.Arg(2), ctx.Arg(3));
            if (error != 0) return error;
            error = WriteString(ctx, child.Value.ClassName, ctx.Arg(5), ctx.Arg(6));
            if (error != 0) return error;
            if (ctx.Arg(7) != 0) ctx.Write(ctx.Arg(7), child.Value.LastWriteTime);
            return 0;
        });
    }

    [VirtualApi("advapi32.dll", "RegEnumValueW", ApiCategory.Registry, "kernelbase!RegEnumValueW")]
    [VirtualApi("advapi32.dll", "RegEnumValueA", ApiCategory.Registry, "kernelbase!RegEnumValueA")]
    private static ulong RegEnumValue(ApiCallContext ctx)
    {
        if (ctx.Arg(2) == 0 || ctx.Arg(3) == 0 || ctx.Arg(4) != 0 || (ctx.Arg(6) != 0 && ctx.Arg(7) == 0))
            return Win32Error.InvalidParameter;
        return WithKey(ctx, QueryValue, key =>
        {
            var values = key.Key.Values.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).ToArray();
            if (ctx.Arg32(1) >= values.Length) return NoMoreItems;
            var value = values[(int)ctx.Arg32(1)];
            var error = WriteString(ctx, value.Key, ctx.Arg(2), ctx.Arg(3));
            if (error != 0) return error;
            return WriteValue(ctx, value.Value, ctx.Arg(5), ctx.Arg(6), ctx.Arg(7));
        });
    }

    [VirtualApi("advapi32.dll", "RegQueryInfoKeyW", ApiCategory.Registry, "kernelbase!RegQueryInfoKeyW")]
    [VirtualApi("advapi32.dll", "RegQueryInfoKeyA", ApiCategory.Registry, "kernelbase!RegQueryInfoKeyA")]
    private static ulong RegQueryInfoKey(ApiCallContext ctx)
    {
        if (ctx.Arg(3) != 0 || (ctx.Arg(1) != 0 && ctx.Arg(2) == 0)) return Win32Error.InvalidParameter;
        return WithKey(ctx, QueryValue, key =>
        {
            ctx.WriteUInt32IfNotNull(ctx.Arg(4), (uint)key.Key.SubKeys.Count);
            ctx.WriteUInt32IfNotNull(ctx.Arg(5), key.Key.SubKeys.Keys.Select(n => StringLength(ctx, n)).DefaultIfEmpty().Max());
            ctx.WriteUInt32IfNotNull(ctx.Arg(6), key.Key.SubKeys.Values.Select(k => StringLength(ctx, k.ClassName)).DefaultIfEmpty().Max());
            ctx.WriteUInt32IfNotNull(ctx.Arg(7), (uint)key.Key.Values.Count);
            ctx.WriteUInt32IfNotNull(ctx.Arg(8), key.Key.Values.Keys.Select(n => StringLength(ctx, n)).DefaultIfEmpty().Max());
            ctx.WriteUInt32IfNotNull(ctx.Arg(9), key.Key.Values.Values.Select(v => (uint)ValueBytes(ctx, v).Length).DefaultIfEmpty().Max());
            ctx.WriteUInt32IfNotNull(ctx.Arg(10), 0);
            if (ctx.Arg(11) != 0) ctx.Write(ctx.Arg(11), key.Key.LastWriteTime);
            return WriteString(ctx, key.Key.ClassName, ctx.Arg(1), ctx.Arg(2));
        });
    }

    [VirtualApi("advapi32.dll", "RegFlushKey", ApiCategory.Registry, "kernelbase!RegFlushKey")]
    private static ulong RegFlushKey(ApiCallContext ctx) => WithKey(ctx, QueryValue, key =>
    {
        if (ctx.Process.FileSystem.AllowWrites) key.Store.Flush();
        return 0;
    });
}
