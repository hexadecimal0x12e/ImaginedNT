using System.Runtime.InteropServices;

namespace ImaginedNT.Core.Runtime;

public static partial class PackageApis
{
    // These conversions only operate on caller data and do not inspect host package identity.
    [VirtualApi("kernel32", "PackageFullNameFromId", ApiCategory.Package)]
    private static ulong PackageFullNameFromId(ApiCallContext c) =>
        (uint)ConvertPackageFullNameFromId((nint)c.Arg(0), (nint)c.Arg(1), (nint)c.Arg(2));
    [DllImport("kernel32.dll", EntryPoint = "PackageFullNameFromId", ExactSpelling = true)]
    private static extern int ConvertPackageFullNameFromId(nint a0, nint a1, nint a2);

    [VirtualApi("kernel32", "PackageFamilyNameFromId", ApiCategory.Package)]
    private static ulong PackageFamilyNameFromId(ApiCallContext c) =>
        (uint)ConvertPackageFamilyNameFromId((nint)c.Arg(0), (nint)c.Arg(1), (nint)c.Arg(2));
    [DllImport("kernel32.dll", EntryPoint = "PackageFamilyNameFromId", ExactSpelling = true)]
    private static extern int ConvertPackageFamilyNameFromId(nint a0, nint a1, nint a2);

    [VirtualApi("kernel32", "PackageFamilyNameFromFullName", ApiCategory.Package)]
    private static ulong PackageFamilyNameFromFullName(ApiCallContext c) =>
        (uint)ConvertPackageFamilyNameFromFullName((nint)c.Arg(0), (nint)c.Arg(1), (nint)c.Arg(2));
    [DllImport("kernel32.dll", EntryPoint = "PackageFamilyNameFromFullName", ExactSpelling = true)]
    private static extern int ConvertPackageFamilyNameFromFullName(nint a0, nint a1, nint a2);

    [VirtualApi("kernel32", "PackageNameAndPublisherIdFromFamilyName", ApiCategory.Package)]
    private static ulong PackageNameAndPublisherIdFromFamilyName(ApiCallContext c) =>
        (uint)ConvertPackageNameAndPublisherIdFromFamilyName((nint)c.Arg(0), (nint)c.Arg(1), (nint)c.Arg(2), (nint)c.Arg(3), (nint)c.Arg(4));
    [DllImport("kernel32.dll", EntryPoint = "PackageNameAndPublisherIdFromFamilyName", ExactSpelling = true)]
    private static extern int ConvertPackageNameAndPublisherIdFromFamilyName(nint a0, nint a1, nint a2, nint a3, nint a4);

    [VirtualApi("kernel32", "FormatApplicationUserModelId", ApiCategory.Package)]
    private static ulong FormatApplicationUserModelId(ApiCallContext c) =>
        (uint)ConvertFormatApplicationUserModelId((nint)c.Arg(0), (nint)c.Arg(1), (nint)c.Arg(2), (nint)c.Arg(3));
    [DllImport("kernel32.dll", EntryPoint = "FormatApplicationUserModelId", ExactSpelling = true)]
    private static extern int ConvertFormatApplicationUserModelId(nint a0, nint a1, nint a2, nint a3);

    [VirtualApi("kernel32", "ParseApplicationUserModelId", ApiCategory.Package)]
    private static ulong ParseApplicationUserModelId(ApiCallContext c) =>
        (uint)ConvertParseApplicationUserModelId((nint)c.Arg(0), (nint)c.Arg(1), (nint)c.Arg(2), (nint)c.Arg(3), (nint)c.Arg(4));
    [DllImport("kernel32.dll", EntryPoint = "ParseApplicationUserModelId", ExactSpelling = true)]
    private static extern int ConvertParseApplicationUserModelId(nint a0, nint a1, nint a2, nint a3, nint a4);

    [VirtualApi("kernel32", "VerifyPackageFullName", ApiCategory.Package)]
    private static ulong VerifyPackageFullName(ApiCallContext c) =>
        (uint)ConvertVerifyPackageFullName((nint)c.Arg(0));
    [DllImport("kernelbase.dll", EntryPoint = "VerifyPackageFullName", ExactSpelling = true)]
    private static extern int ConvertVerifyPackageFullName(nint a0);

    [VirtualApi("kernel32", "VerifyPackageFamilyName", ApiCategory.Package)]
    private static ulong VerifyPackageFamilyName(ApiCallContext c) =>
        (uint)ConvertVerifyPackageFamilyName((nint)c.Arg(0));
    [DllImport("kernelbase.dll", EntryPoint = "VerifyPackageFamilyName", ExactSpelling = true)]
    private static extern int ConvertVerifyPackageFamilyName(nint a0);

    [VirtualApi("kernel32", "VerifyPackageId", ApiCategory.Package)]
    private static ulong VerifyPackageId(ApiCallContext c) =>
        (uint)ConvertVerifyPackageId((nint)c.Arg(0));
    [DllImport("kernelbase.dll", EntryPoint = "VerifyPackageId", ExactSpelling = true)]
    private static extern int ConvertVerifyPackageId(nint a0);

    [VirtualApi("kernel32", "VerifyApplicationUserModelId", ApiCategory.Package)]
    private static ulong VerifyApplicationUserModelId(ApiCallContext c) =>
        (uint)ConvertVerifyApplicationUserModelId((nint)c.Arg(0));
    [DllImport("kernelbase.dll", EntryPoint = "VerifyApplicationUserModelId", ExactSpelling = true)]
    private static extern int ConvertVerifyApplicationUserModelId(nint a0);

    [VirtualApi("kernel32", "VerifyPackageRelativeApplicationId", ApiCategory.Package)]
    private static ulong VerifyPackageRelativeApplicationId(ApiCallContext c) =>
        (uint)ConvertVerifyPackageRelativeApplicationId((nint)c.Arg(0));
    [DllImport("kernelbase.dll", EntryPoint = "VerifyPackageRelativeApplicationId", ExactSpelling = true)]
    private static extern int ConvertVerifyPackageRelativeApplicationId(nint a0);

}
