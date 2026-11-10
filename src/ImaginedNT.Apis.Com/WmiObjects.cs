using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using ImaginedNT.Core.Com;
using ImaginedNT.Core.Diagnostics;
using ImaginedNT.Core.Runtime;

namespace ImaginedNT.Apis.Com;

[VirtualComClass(WmiIds.LocatorClass, "Imagined.WbemLocator")]
public sealed class WmiLocator : IWmiLocator
{
    public ulong ConnectServer(ComCallContext c)
    {
        var path = (c.ReadBstr(1) ?? "root\\cimv2").Replace('/', '\\');
        if (path.StartsWith("\\\\", StringComparison.Ordinal))
        {
            var separator = path.IndexOf('\\', 2);
            if (separator < 0) return c.Failed(WmiIds.NotSupported);
            var server = path[2..separator];
            if (server != "." && !server.Equals("localhost", StringComparison.OrdinalIgnoreCase) &&
                !server.Equals("IMAGINEDNT", StringComparison.OrdinalIgnoreCase))
                return c.Failed(WmiIds.NotSupported);
            path = path[(separator + 1)..];
        }
        if (!path.Equals("root\\cimv2", StringComparison.OrdinalIgnoreCase))
            return c.Failed(unchecked((int)0x8004100E)); // WBEM_E_INVALID_NAMESPACE
        return WmiObject.Write(c, WmiIds.ServicesClass, WmiIds.Services, new WmiServices(), c.Arg(8));
    }
}

[VirtualComClass(WmiIds.ServicesClass, "Imagined.WbemServices")]
public sealed class WmiServices : WmiSecurity, IWmiServices
{
    public ulong ExecQuery(ComCallContext c)
    {
        if (!string.Equals(c.ReadBstr(1), "WQL", StringComparison.OrdinalIgnoreCase))
            return c.Failed(unchecked((int)0x80041018));
        var query = c.ReadBstr(2) ?? string.Empty;
        if (c.Api.Runtime.Options.TraceNativeCalls)
            c.Process.Trace.Emit(TraceKind.Api, $"guest WMI query: {query}", c.Process.Id, c.Thread.Id);
        var match = Regex.Match(query, @"^\s*SELECT\s+[\w*,\s]+\s+FROM\s+(\w+)(?:\s+WHERE\s+(.+?))?\s*$", RegexOptions.IgnoreCase);
        if (!match.Success) return c.Failed(WmiIds.InvalidQuery);
        return Enumerate(c, match.Groups[1].Value, match.Groups[2].Value, c.Arg(5));
    }

    public ulong CreateInstanceEnum(ComCallContext c) => Enumerate(c, c.ReadBstr(1) ?? "", "", c.Arg(4));

    private static ulong Enumerate(ComCallContext c, string className, string filter, ulong output)
    {
        var rows = new List<WmiRecord>();
        if (className.Equals("Win32_Process", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var p in c.Api.Runtime.GetProcessInformation())
                rows.Add(WmiRecord.ForProcess(p));
        }
        else if (className.Equals("Win32_OperatingSystem", StringComparison.OrdinalIgnoreCase))
            rows.Add(new WmiRecord(new(StringComparer.OrdinalIgnoreCase)
            {
                ["__CLASS"] = "Win32_OperatingSystem", ["CSName"] = "IMAGINEDNT", ["Name"] = "ImaginedNT",
                ["Caption"] = "ImaginedNT", ["Version"] = "10.0", ["BuildNumber"] = "0",
                ["NumberOfProcesses"] = (uint)c.Api.Runtime.GetProcessInformation().Count
            }));
        else if (className.Equals("Win32_ComputerSystem", StringComparison.OrdinalIgnoreCase))
            rows.Add(new WmiRecord(new(StringComparer.OrdinalIgnoreCase)
            {
                ["__CLASS"] = "Win32_ComputerSystem", ["Name"] = "IMAGINEDNT", ["Domain"] = "WORKGROUP"
            }));
        else if (!className.Equals("Win32_Service", StringComparison.OrdinalIgnoreCase))
            return c.Failed(WmiIds.InvalidClass);
        if (filter.Length != 0)
        {
            var condition = Regex.Match(filter.Trim().Trim('(', ')').Trim(),
                "^(\\w+)\\s*=\\s*(?:'([^']*)'|\"([^\"]*)\"|(\\d+))$");
            if (!condition.Success) return c.Failed(WmiIds.InvalidQuery);
            var expected = condition.Groups[2].Success ? condition.Groups[2].Value :
                condition.Groups[3].Success ? condition.Groups[3].Value : condition.Groups[4].Value;
            rows.RemoveAll(r => !r.Values.TryGetValue(condition.Groups[1].Value, out var value) ||
                !string.Equals(Convert.ToString(value, CultureInfo.InvariantCulture), expected, StringComparison.OrdinalIgnoreCase));
        }
        return WmiObject.Write(c, WmiIds.EnumeratorClass, WmiIds.Enumerator,
            new WmiEnumerator { Rows = rows.ToArray() }, output);
    }

    public ulong GetObject(ComCallContext c)
    {
        if (c.Arg(5) != 0) c.Memory.Write(c.Arg(5), 0UL);
        var path = c.ReadBstr(1) ?? "";
        if (c.Api.Runtime.Options.TraceNativeCalls)
            c.Process.Trace.Emit(TraceKind.Api, $"guest WMI object: {path}", c.Process.Id, c.Thread.Id);
        if (path.Equals("Win32_Process", StringComparison.OrdinalIgnoreCase))
            return WmiObject.Write(c, WmiIds.ObjectClass, WmiIds.ClassObject,
                new WmiRecordObject { Row = new(new(StringComparer.OrdinalIgnoreCase) { ["__CLASS"] = "Win32_Process" }) }, c.Arg(4));
        var match = Regex.Match(path, "^Win32_Process\\.Handle=\"?(\\d+)\"?$", RegexOptions.IgnoreCase);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var id) ||
            c.Api.Runtime.GetProcessInformation(id) is not { ExitCode: null } p)
            return c.Failed(WmiIds.NotFound);
        return WmiObject.Write(c, WmiIds.ObjectClass, WmiIds.ClassObject,
            new WmiRecordObject { Row = WmiRecord.ForProcess(p) }, c.Arg(4));
    }

    public ulong ExecMethod(ComCallContext c) => c.Failed(WmiIds.NotSupported);
    public ulong NotSupported(ComCallContext c) => c.Failed(WmiIds.NotSupported);
}

[VirtualComClass(WmiIds.EnumeratorClass, "Imagined.EnumWbemClassObject")]
public sealed class WmiEnumerator : WmiSecurity, IWmiEnumerator
{
    internal WmiRecord[] Rows { get; init; } = [];
    private int _position;

    public ulong Reset(ComCallContext c)
    {
        lock (this) _position = 0;
        return c.Succeeded();
    }

    public ulong Next(ComCallContext c)
    {
        if (c.Arg(4) == 0 || c.Arg(3) == 0) return c.Failed(ComHResult.E_POINTER);
        c.Memory.Write(c.Arg(4), 0u);
        if (c.Arg32(2) > 4096) return c.Failed(ComHResult.E_INVALIDARG);
        lock (this)
        {
            var count = Math.Min((int)c.Arg32(2), Rows.Length - _position);
            for (var i = 0; i < count; i++)
            {
                var hr = WmiObject.Write(c, WmiIds.ObjectClass, WmiIds.ClassObject,
                    new WmiRecordObject { Row = Rows[_position++] }, c.Arg(3) + (ulong)i * 8);
                if ((int)(uint)hr < 0) return hr;
                c.Memory.Write(c.Arg(4), (uint)(i + 1));
            }
            return count == c.Arg32(2) ? c.Succeeded() : c.Failed(ComHResult.S_FALSE);
        }
    }

    public ulong Clone(ComCallContext c)
    {
        lock (this)
            return WmiObject.Write(c, WmiIds.EnumeratorClass, WmiIds.Enumerator,
                new WmiEnumerator { Rows = Rows, _position = _position }, c.Arg(1));
    }

    public ulong Skip(ComCallContext c)
    {
        lock (this)
        {
            var count = Math.Min((ulong)c.Arg32(2), (ulong)(Rows.Length - _position));
            _position += (int)count;
            return count == c.Arg32(2) ? c.Succeeded() : c.Failed(ComHResult.S_FALSE);
        }
    }

    public ulong NextAsync(ComCallContext c) => c.Failed(WmiIds.NotSupported);
}

internal sealed class WmiRecord(Dictionary<string, object?> values)
{
    internal Dictionary<string, object?> Values { get; } = values;
    internal static WmiRecord ForProcess(ProcessInformation p)
    {
        var m = p.Memory;
        return new(new(StringComparer.OrdinalIgnoreCase)
        {
            ["__CLASS"] = "Win32_Process", ["__RELPATH"] = $"Win32_Process.Handle=\"{p.Id}\"",
            ["__PATH"] = $"\\\\IMAGINEDNT\\root\\cimv2:Win32_Process.Handle=\"{p.Id}\"",
            ["CSName"] = "IMAGINEDNT",
            ["Name"] = p.Name, ["Caption"] = p.Name,
            ["Description"] = p.Name, ["ExecutablePath"] = p.ImagePath,
            ["CommandLine"] = p.CommandLine, ["Handle"] = p.Id.ToString(CultureInfo.InvariantCulture),
            ["ProcessId"] = (uint)p.Id, ["ParentProcessId"] = (uint)p.ParentId, ["SessionId"] = 0u,
            ["WorkingSetSize"] = m.Process.WorkingSetSize, ["PeakWorkingSetSize"] = m.Process.PeakWorkingSetSize / 1024,
            ["VirtualSize"] = m.VirtualBytes, ["PrivatePageCount"] = m.Process.PrivateUsage,
            ["PageFileUsage"] = m.Process.PagefileUsage / 1024, ["HandleCount"] = (uint)p.HandleCount,
            ["ThreadCount"] = (uint)p.Threads.Count, ["Priority"] = (uint)p.Priority, ["UserModeTime"] = 0UL, ["KernelModeTime"] = 0UL,
            ["CreationDate"] = p.StartTime.UtcDateTime.ToString("yyyyMMddHHmmss.ffffff", CultureInfo.InvariantCulture) + "+000",
            ["ExecutionState"] = null
        });
    }
}

[VirtualComClass(WmiIds.ObjectClass, "Imagined.WbemClassObject")]
public sealed class WmiRecordObject : IWmiClassObject
{
    internal WmiRecord Row { get; init; } = new(new(StringComparer.OrdinalIgnoreCase));

    public ulong Get(ComCallContext c)
    {
        if (c.Arg(3) == 0) return c.Failed(ComHResult.E_POINTER);
        c.Memory.WriteBytes(c.Arg(3), new byte[24]);
        var name = c.ReadString(1) ?? "";
        if (!Row.Values.TryGetValue(name, out var value))
        {
            if (c.Api.Runtime.Options.TraceNativeCalls)
                c.Process.Trace.Emit(TraceKind.Api, $"guest WMI property not found: {name}", c.Process.Id, c.Thread.Id);
            return c.Failed(WmiIds.NotFound);
        }
        var type = value is uint ? 19u : value is ulong ? 21u : 8u;
        if (value is uint n)
        {
            c.Memory.Write(c.Arg(3), (ushort)3); // VT_I4, the WMI representation of CIM_UINT32
            c.Memory.Write(c.Arg(3) + 8, n);
        }
        else if (value is not null)
        {
            c.Memory.Write(c.Arg(3), (ushort)8); // VT_BSTR, including CIM_UINT64 decimal strings
            c.Memory.Write(c.Arg(3) + 8, c.AllocBstr(Convert.ToString(value, CultureInfo.InvariantCulture)));
        }
        else c.Memory.Write(c.Arg(3), (ushort)1); // VT_NULL
        if (c.Arg(4) != 0) c.Memory.Write(c.Arg(4), type);
        if (c.Arg(5) != 0) c.Memory.Write(c.Arg(5), 0u);
        return c.Succeeded();
    }

    public ulong GetNames(ComCallContext c)
    {
        if (c.Arg(4) == 0) return c.Failed(ComHResult.E_POINTER);
        c.Memory.Write(c.Arg(4), 0UL);
        var array = SafeArrayCreateVector(8, 0, (uint)Row.Values.Count);
        if (array == 0) return c.Failed(ComHResult.E_OUTOFMEMORY);
        var i = 0;
        foreach (var name in Row.Values.Keys)
        {
            var text = Marshal.StringToBSTR(name);
            try
            {
                if (SafeArrayPutElement(array, ref i, text) < 0)
                {
                    SafeArrayDestroy(array);
                    return c.Failed(ComHResult.E_OUTOFMEMORY);
                }
            }
            finally { Marshal.FreeBSTR(text); }
            i++;
        }
        c.Memory.Write(c.Arg(4), (ulong)array);
        return c.Succeeded();
    }

    public ulong Put(ComCallContext c)
    {
        if (c.Arg(3) == 0) return c.Failed(ComHResult.E_POINTER);
        var name = c.ReadString(1);
        if (string.IsNullOrEmpty(name)) return c.Failed(ComHResult.E_INVALIDARG);
        var type = c.Memory.Read<ushort>(c.Arg(3));
        object? value;
        switch (type)
        {
            case 0:
            case 1: value = null; break;
            case 3:
            case 19: value = c.Memory.Read<uint>(c.Arg(3) + 8); break;
            case 8: value = ComMarshaling.ReadBstr(c.Memory, c.Memory.Read<ulong>(c.Arg(3) + 8)); break;
            default: return c.Failed(ComHResult.E_INVALIDARG);
        }
        Row.Values[name] = value;
        return c.Succeeded();
    }

    public ulong SpawnInstance(ComCallContext c) => WmiObject.Write(c, WmiIds.ObjectClass, WmiIds.ClassObject,
        new WmiRecordObject { Row = new(new(Row.Values, StringComparer.OrdinalIgnoreCase)) }, c.Arg(2));

    public ulong GetMethod(ComCallContext c)
    {
        var name = c.ReadString(1);
        if (c.Api.Runtime.Options.TraceNativeCalls)
            c.Process.Trace.Emit(TraceKind.Api, $"guest WMI method: {name}", c.Process.Id, c.Thread.Id);
        if (c.Arg(3) != 0) c.Memory.Write(c.Arg(3), 0UL);
        if (c.Arg(4) != 0) c.Memory.Write(c.Arg(4), 0UL);
        if (!string.Equals(name, "Terminate", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(name, "GetOwner", StringComparison.OrdinalIgnoreCase)) return c.Failed(WmiIds.NotFound);
        if (c.Arg(3) != 0)
        {
            var hr = WmiObject.Write(c, WmiIds.ObjectClass, WmiIds.ClassObject,
                new WmiRecordObject { Row = new(new(StringComparer.OrdinalIgnoreCase) { ["Reason"] = 0u }) }, c.Arg(3));
            if ((int)(uint)hr < 0) return hr;
        }
        return c.Arg(4) == 0 ? c.Succeeded() : WmiObject.Write(c, WmiIds.ObjectClass, WmiIds.ClassObject,
            new WmiRecordObject { Row = new(new(StringComparer.OrdinalIgnoreCase) { ["ReturnValue"] = 0u }) }, c.Arg(4));
    }

    public ulong Clone(ComCallContext c) =>
        WmiObject.Write(c, WmiIds.ObjectClass, WmiIds.ClassObject, new WmiRecordObject { Row = Row }, c.Arg(1));
    public ulong NotSupported(ComCallContext c) => c.Failed(WmiIds.NotSupported);

    [DllImport("oleaut32.dll")]
    private static extern nint SafeArrayCreateVector(ushort type, int lower, uint count);
    [DllImport("oleaut32.dll")]
    private static extern int SafeArrayPutElement(nint array, ref int index, nint value);
    [DllImport("oleaut32.dll")]
    private static extern int SafeArrayDestroy(nint array);
}

internal static class WmiObject
{
    internal static ulong Write(ComCallContext c, string classId, string interfaceId, object instance, ulong output)
    {
        if (output == 0) return c.Failed(ComHResult.E_POINTER);
        c.Memory.Write(output, 0UL);
        var hr = c.Api.Runtime.Com.CreateManagedObject(c.Thread, new Guid(classId), new Guid(interfaceId), instance, out var pointer);
        if (hr >= 0) c.Memory.Write(output, pointer);
        return c.Failed(hr);
    }
}

public abstract class WmiSecurity : IWmiClientSecurity
{
    public ulong SetBlanket(ComCallContext c) => c.Succeeded();

    public ulong QueryBlanket(ComCallContext c)
    {
        if (c.Arg(2) != 0) c.Memory.Write(c.Arg(2), 10u);
        if (c.Arg(3) != 0) c.Memory.Write(c.Arg(3), 0u);
        if (c.Arg(4) != 0) c.Memory.Write(c.Arg(4), 0UL);
        if (c.Arg(5) != 0) c.Memory.Write(c.Arg(5), 3u);
        if (c.Arg(6) != 0) c.Memory.Write(c.Arg(6), 3u);
        if (c.Arg(7) != 0) c.Memory.Write(c.Arg(7), 0UL);
        if (c.Arg(8) != 0) c.Memory.Write(c.Arg(8), 0u);
        return c.Succeeded();
    }

    public unsafe ulong CopyProxy(ComCallContext c)
    {
        if (c.Arg(2) == 0) return c.Failed(ComHResult.E_POINTER);
        var pointer = c.Arg(1);
        if (c.Api.Runtime.Com.FindObject(pointer) is not { } obj || !ReferenceEquals(obj.Process, c.Process))
            return c.Failed(ComHResult.E_INVALIDARG);
        var addRef = c.Memory.Read<ulong>(c.Memory.Read<ulong>(pointer) + 8);
        ((delegate* unmanaged<ulong, uint>)(nint)addRef)(pointer);
        c.Memory.Write(c.Arg(2), pointer);
        return c.Succeeded();
    }
}
