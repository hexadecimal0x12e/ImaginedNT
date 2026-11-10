using ImaginedNT.Core.Com;

namespace ImaginedNT.Apis.Com;

[VirtualComInterface(WmiIds.Locator, "IWbemLocator")]
public interface IWmiLocator
{
    [VirtualComMethod(WmiIds.Locator, 3)]
    ulong ConnectServer(ComCallContext c);
}

[VirtualComInterface(WmiIds.Services, "IWbemServices")]
public interface IWmiServices
{
    [VirtualComMethod(WmiIds.Services, 6)]
    ulong GetObject(ComCallContext c);
    [VirtualComMethod(WmiIds.Services, 18)]
    ulong CreateInstanceEnum(ComCallContext c);
    [VirtualComMethod(WmiIds.Services, 20)]
    ulong ExecQuery(ComCallContext c);
    [VirtualComMethod(WmiIds.Services, 24)]
    ulong ExecMethod(ComCallContext c);

    [VirtualComMethod(WmiIds.Services, 3, "OpenNamespace")]
    [VirtualComMethod(WmiIds.Services, 4, "CancelAsyncCall")]
    [VirtualComMethod(WmiIds.Services, 5, "QueryObjectSink")]
    [VirtualComMethod(WmiIds.Services, 7, "GetObjectAsync")]
    [VirtualComMethod(WmiIds.Services, 8, "PutClass")]
    [VirtualComMethod(WmiIds.Services, 9, "PutClassAsync")]
    [VirtualComMethod(WmiIds.Services, 10, "DeleteClass")]
    [VirtualComMethod(WmiIds.Services, 11, "DeleteClassAsync")]
    [VirtualComMethod(WmiIds.Services, 12, "CreateClassEnum")]
    [VirtualComMethod(WmiIds.Services, 13, "CreateClassEnumAsync")]
    [VirtualComMethod(WmiIds.Services, 14, "PutInstance")]
    [VirtualComMethod(WmiIds.Services, 15, "PutInstanceAsync")]
    [VirtualComMethod(WmiIds.Services, 16, "DeleteInstance")]
    [VirtualComMethod(WmiIds.Services, 17, "DeleteInstanceAsync")]
    [VirtualComMethod(WmiIds.Services, 19, "CreateInstanceEnumAsync")]
    [VirtualComMethod(WmiIds.Services, 21, "ExecQueryAsync")]
    [VirtualComMethod(WmiIds.Services, 22, "ExecNotificationQuery")]
    [VirtualComMethod(WmiIds.Services, 23, "ExecNotificationQueryAsync")]
    [VirtualComMethod(WmiIds.Services, 25, "ExecMethodAsync")]
    ulong NotSupported(ComCallContext c);
}

[VirtualComInterface(WmiIds.Enumerator, "IEnumWbemClassObject")]
public interface IWmiEnumerator
{
    [VirtualComMethod(WmiIds.Enumerator, 3)]
    ulong Reset(ComCallContext c);
    [VirtualComMethod(WmiIds.Enumerator, 4)]
    ulong Next(ComCallContext c);
    [VirtualComMethod(WmiIds.Enumerator, 5)]
    ulong NextAsync(ComCallContext c);
    [VirtualComMethod(WmiIds.Enumerator, 6)]
    ulong Clone(ComCallContext c);
    [VirtualComMethod(WmiIds.Enumerator, 7)]
    ulong Skip(ComCallContext c);
}

[VirtualComInterface(WmiIds.ClassObject, "IWbemClassObject")]
public interface IWmiClassObject
{
    [VirtualComMethod(WmiIds.ClassObject, 4)]
    ulong Get(ComCallContext c);
    [VirtualComMethod(WmiIds.ClassObject, 5)]
    ulong Put(ComCallContext c);
    [VirtualComMethod(WmiIds.ClassObject, 7)]
    ulong GetNames(ComCallContext c);
    [VirtualComMethod(WmiIds.ClassObject, 12)]
    ulong Clone(ComCallContext c);
    [VirtualComMethod(WmiIds.ClassObject, 15)]
    ulong SpawnInstance(ComCallContext c);
    [VirtualComMethod(WmiIds.ClassObject, 19)]
    ulong GetMethod(ComCallContext c);

    [VirtualComMethod(WmiIds.ClassObject, 3, "GetQualifierSet")]
    [VirtualComMethod(WmiIds.ClassObject, 6, "Delete")]
    [VirtualComMethod(WmiIds.ClassObject, 8, "BeginEnumeration")]
    [VirtualComMethod(WmiIds.ClassObject, 9, "Next")]
    [VirtualComMethod(WmiIds.ClassObject, 10, "EndEnumeration")]
    [VirtualComMethod(WmiIds.ClassObject, 11, "GetPropertyQualifierSet")]
    [VirtualComMethod(WmiIds.ClassObject, 13, "GetObjectText")]
    [VirtualComMethod(WmiIds.ClassObject, 14, "SpawnDerivedClass")]
    [VirtualComMethod(WmiIds.ClassObject, 16, "CompareTo")]
    [VirtualComMethod(WmiIds.ClassObject, 17, "GetPropertyOrigin")]
    [VirtualComMethod(WmiIds.ClassObject, 18, "InheritsFrom")]
    [VirtualComMethod(WmiIds.ClassObject, 20, "PutMethod")]
    [VirtualComMethod(WmiIds.ClassObject, 21, "DeleteMethod")]
    [VirtualComMethod(WmiIds.ClassObject, 22, "BeginMethodEnumeration")]
    [VirtualComMethod(WmiIds.ClassObject, 23, "NextMethod")]
    [VirtualComMethod(WmiIds.ClassObject, 24, "EndMethodEnumeration")]
    [VirtualComMethod(WmiIds.ClassObject, 25, "GetMethodQualifierSet")]
    [VirtualComMethod(WmiIds.ClassObject, 26, "GetMethodOrigin")]
    ulong NotSupported(ComCallContext c);
}

internal static class WmiIds
{
    public const string ClientSecurity = "0000013d-0000-0000-c000-000000000046";
    public const string LocatorClass = "4590f811-1d3a-11d0-891f-00aa004b2e24";
    public const string Locator = "dc12a687-737f-11cf-884d-00aa004b2e24";
    public const string Services = "9556dc99-828c-11cf-a37e-00aa003240c7";
    public const string Enumerator = "027947e1-d731-11ce-a357-000000000001";
    public const string ClassObject = "dc12a681-737f-11cf-884d-00aa004b2e24";
    public const string ServicesClass = "15ea3b56-1994-49c0-b401-790328db2041";
    public const string EnumeratorClass = "5c0da647-b4ba-424c-a2b9-f725b986dd03";
    public const string ObjectClass = "cc32863c-729b-4507-a42b-052535ac9ff2";

    public const int NotSupported = unchecked((int)0x8004100C);
    public const int NotFound = unchecked((int)0x80041002);
    public const int InvalidClass = unchecked((int)0x80041010);
    public const int InvalidQuery = unchecked((int)0x80041017);
}

[VirtualComInterface(WmiIds.ClientSecurity, "IClientSecurity")]
public interface IWmiClientSecurity
{
    [VirtualComMethod(WmiIds.ClientSecurity, 3)]
    ulong QueryBlanket(ComCallContext c);
    [VirtualComMethod(WmiIds.ClientSecurity, 4)]
    ulong SetBlanket(ComCallContext c);
    [VirtualComMethod(WmiIds.ClientSecurity, 5)]
    ulong CopyProxy(ComCallContext c);
}
