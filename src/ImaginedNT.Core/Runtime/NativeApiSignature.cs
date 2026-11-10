namespace ImaginedNT.Core.Runtime;

/// <summary>Handle locations in an NTSTATUS-returning x64 NT export.</summary>
public sealed record NativeApiSignature(int ArgumentCount, int[] HandleArguments,
    int[] OutputHandleArguments, int[] ObjectAttributesArguments);

public static class NativeApiSignatures
{
    private static readonly Dictionary<string, NativeApiSignature> Signatures = Create();

    public static string Normalize(string name) => name.StartsWith("Zw", StringComparison.Ordinal)
        ? "Nt" + name[2..] : name;

    public static bool TryGet(string name, out NativeApiSignature signature)
    {
        lock (Signatures) return Signatures.TryGetValue(Normalize(name), out signature!);
    }

    public static void Register(string name, NativeApiSignature signature)
    {
        ArgumentNullException.ThrowIfNull(signature);
        if (signature.ArgumentCount is < 0 or > 16 ||
            signature.HandleArguments.Concat(signature.OutputHandleArguments)
                .Concat(signature.ObjectAttributesArguments).Any(i => i < 0 || i >= signature.ArgumentCount))
            throw new ArgumentException("Invalid native API signature", nameof(signature));
        lock (Signatures) Signatures[Normalize(name)] = signature;
    }

    internal static bool TryGetWin32(string module, string name, out NativeApiSignature signature)
    {
        signature = null!;
        var normalized = ApiRegistry.NormalizeModule(module);
        if (normalized != "kernel32" && normalized != "kernelbase" &&
            !normalized.StartsWith("api-ms-win-core-", StringComparison.Ordinal)) return false;
        var count = name switch
        {
            "CloseHandle" or "SetEvent" or "ResetEvent" or "PulseEvent" or "ReleaseMutex" or "FlushFileBuffers" or
                "SetEndOfFile" or "GetFileType" => 1,
            "GetHandleInformation" or "WaitForSingleObject" or "GetFileSize" or "GetFileSizeEx" or
                "GetExitCodeProcess" or "GetExitCodeThread" or "TerminateProcess" => 2,
            "SetHandleInformation" or "WaitForSingleObjectEx" or "ReleaseSemaphore" => 3,
            "SetFilePointer" or "SetFilePointerEx" or "WaitForMultipleObjects" => 4,
            "ReadFile" or "WriteFile" or "WaitForMultipleObjectsEx" => 5,
            "DuplicateHandle" => 7,
            _ => 0
        };
        if (count == 0) return false;
        signature = new(count, name == "DuplicateHandle" ? [0, 1, 2] :
            name.StartsWith("WaitForMultipleObjects", StringComparison.Ordinal) ? [] : [0],
            name == "DuplicateHandle" ? [3] : [], []);
        return true;
    }

    private static Dictionary<string, NativeApiSignature> Create()
    {
        var signatures = new Dictionary<string, NativeApiSignature>(StringComparer.Ordinal);
        void Add(string name, int count, int[]? handles = null, int[]? outputs = null, int[]? attrs = null) =>
            signatures.Add(name, new(count, handles ?? [], outputs ?? [], attrs ?? []));

        Add("NtClose", 1, [0]);
        Add("NtDuplicateObject", 7, [0, 1, 2], [3]);
        Add("NtCreateFile", 11, outputs: [0], attrs: [2]);
        Add("NtOpenFile", 6, outputs: [0], attrs: [2]);
        Add("NtCreateNamedPipeFile", 14, outputs: [0], attrs: [2]);
        Add("NtCreateMailslotFile", 8, outputs: [0], attrs: [2]);
        Add("NtReadFile", 9, [0, 1]);
        Add("NtWriteFile", 9, [0, 1]);
        Add("NtReadFileScatter", 9, [0, 1]);
        Add("NtWriteFileGather", 9, [0, 1]);
        Add("NtDeviceIoControlFile", 10, [0, 1]);
        Add("NtFsControlFile", 10, [0, 1]);
        Add("NtQueryInformationFile", 5, [0]);
        // FileRenameInformation/FileLinkInformation contain another handle and need class-specific conversion.
        Add("NtSetInformationFile", 5, [0]);
        Add("NtQueryVolumeInformationFile", 5, [0]);
        Add("NtSetVolumeInformationFile", 5, [0]);
        Add("NtQueryDirectoryFile", 11, [0, 1]);
        Add("NtQueryDirectoryFileEx", 10, [0, 1]);
        Add("NtFlushBuffersFile", 2, [0]);
        Add("NtFlushBuffersFileEx", 5, [0]);
        Add("NtCancelIoFile", 2, [0]);
        Add("NtCancelIoFileEx", 3, [0]);
        Add("NtLockFile", 10, [0, 1]);
        Add("NtUnlockFile", 5, [0]);
        Add("NtDeleteFile", 1, attrs: [0]);
        Add("NtQueryAttributesFile", 2, attrs: [0]);
        Add("NtQueryFullAttributesFile", 2, attrs: [0]);
        Add("NtCreateEvent", 5, outputs: [0], attrs: [2]);
        Add("NtOpenEvent", 3, outputs: [0], attrs: [2]);
        Add("NtSetEvent", 2, [0]);
        Add("NtResetEvent", 2, [0]);
        Add("NtPulseEvent", 2, [0]);
        Add("NtClearEvent", 1, [0]);
        Add("NtQueryEvent", 5, [0]);
        Add("NtCreateMutant", 4, outputs: [0], attrs: [2]);
        Add("NtOpenMutant", 3, outputs: [0], attrs: [2]);
        Add("NtReleaseMutant", 2, [0]);
        Add("NtQueryMutant", 5, [0]);
        Add("NtCreateSemaphore", 5, outputs: [0], attrs: [2]);
        Add("NtOpenSemaphore", 3, outputs: [0], attrs: [2]);
        Add("NtReleaseSemaphore", 3, [0]);
        Add("NtQuerySemaphore", 5, [0]);
        Add("NtCreateTimer", 4, outputs: [0], attrs: [2]);
        Add("NtOpenTimer", 3, outputs: [0], attrs: [2]);
        Add("NtSetTimer", 7, [0]);
        Add("NtCancelTimer", 2, [0]);
        Add("NtQueryTimer", 5, [0]);
        Add("NtWaitForSingleObject", 3, [0]);
        Add("NtWaitForMultipleObjects", 5);
        Add("NtSignalAndWaitForSingleObject", 4, [0, 1]);
        Add("NtCreateSection", 7, [6], [0], [2]);
        Add("NtOpenSection", 3, outputs: [0], attrs: [2]);
        Add("NtMapViewOfSection", 10, [0, 1]);
        Add("NtUnmapViewOfSection", 2, [0]);
        Add("NtQuerySection", 5, [0]);
        Add("NtAllocateVirtualMemory", 6, [0]);
        Add("NtFreeVirtualMemory", 4, [0]);
        Add("NtProtectVirtualMemory", 5, [0]);
        Add("NtQueryVirtualMemory", 6, [0]);
        Add("NtReadVirtualMemory", 5, [0]);
        Add("NtWriteVirtualMemory", 5, [0]);
        Add("NtFlushInstructionCache", 3, [0]);
        Add("NtOpenProcess", 4, outputs: [0], attrs: [2]);
        Add("NtTerminateProcess", 2, [0]);
        Add("NtOpenThread", 4, outputs: [0], attrs: [2]);
        Add("NtCreateThreadEx", 11, [3], [0], [2]);
        Add("NtQueryInformationProcess", 5, [0]);
        Add("NtQueryInformationThread", 5, [0]);
        Add("NtSuspendThread", 2, [0]);
        Add("NtResumeThread", 2, [0]);
        Add("NtOpenProcessToken", 3, [0], [2]);
        Add("NtOpenProcessTokenEx", 4, [0], [3]);
        Add("NtOpenThreadToken", 4, [0], [3]);
        Add("NtOpenThreadTokenEx", 5, [0], [4]);
        Add("NtDuplicateToken", 6, [0], [5], [2]);
        Add("NtQueryInformationToken", 5, [0]);
        Add("NtCreateKey", 7, outputs: [0], attrs: [2]);
        Add("NtOpenKey", 3, outputs: [0], attrs: [2]);
        Add("NtOpenKeyEx", 4, outputs: [0], attrs: [2]);
        Add("NtDeleteKey", 1, [0]);
        Add("NtFlushKey", 1, [0]);
        Add("NtQueryKey", 5, [0]);
        Add("NtQueryValueKey", 6, [0]);
        Add("NtSetValueKey", 6, [0]);
        Add("NtDeleteValueKey", 2, [0]);
        Add("NtEnumerateKey", 6, [0]);
        Add("NtEnumerateValueKey", 6, [0]);
        Add("NtCreateDirectoryObject", 3, outputs: [0], attrs: [2]);
        Add("NtOpenDirectoryObject", 3, outputs: [0], attrs: [2]);
        Add("NtQueryDirectoryObject", 7, [0]);
        Add("NtCreateSymbolicLinkObject", 4, outputs: [0], attrs: [2]);
        Add("NtOpenSymbolicLinkObject", 3, outputs: [0], attrs: [2]);
        Add("NtQuerySymbolicLinkObject", 3, [0]);
        Add("NtQueryObject", 5, [0]);
        Add("NtQuerySecurityObject", 5, [0]);
        Add("NtSetSecurityObject", 3, [0]);
        Add("NtCreateIoCompletion", 4, outputs: [0], attrs: [2]);
        Add("NtOpenIoCompletion", 3, outputs: [0], attrs: [2]);
        Add("NtSetIoCompletion", 5, [0]);
        Add("NtRemoveIoCompletion", 5, [0]);
        Add("NtQueryIoCompletion", 5, [0]);
        Add("NtCreateJobObject", 3, outputs: [0], attrs: [2]);
        Add("NtOpenJobObject", 3, outputs: [0], attrs: [2]);
        Add("NtAssignProcessToJobObject", 2, [0, 1]);
        Add("NtIsProcessInJob", 2, [0, 1]);
        Add("NtCreatePort", 5, outputs: [0], attrs: [2]);
        Add("NtCreateWaitablePort", 5, outputs: [0], attrs: [2]);
        Add("NtAlpcCreatePort", 3, outputs: [0], attrs: [1]);
        Add("NtCreateKeyedEvent", 4, outputs: [0], attrs: [2]);
        Add("NtOpenKeyedEvent", 3, outputs: [0], attrs: [2]);
        Add("NtWaitForKeyedEvent", 4, [0]);
        Add("NtReleaseKeyedEvent", 4, [0]);
        Add("NtCreateEventPair", 3, outputs: [0], attrs: [2]);
        Add("NtOpenEventPair", 3, outputs: [0], attrs: [2]);
        Add("NtSetHighEventPair", 1, [0]);
        Add("NtSetLowEventPair", 1, [0]);
        Add("NtWaitHighEventPair", 1, [0]);
        Add("NtWaitLowEventPair", 1, [0]);
        Add("NtSetHighWaitLowEventPair", 1, [0]);
        Add("NtSetLowWaitHighEventPair", 1, [0]);
        Add("NtQuerySystemTime", 1);
        Add("NtQueryPerformanceCounter", 2);
        Add("NtDelayExecution", 2);
        Add("NtYieldExecution", 0);
        return signatures;
    }
}
