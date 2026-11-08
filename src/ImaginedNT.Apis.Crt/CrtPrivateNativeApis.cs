using ImaginedNT.Core.Runtime;

[assembly: PartiallyImaginedNativeApi("api-ms-win-crt-private-l1-1-0.dll", "_o_ceilf", ApiCategory.System, "ucrtbase!_o_ceilf")]

// Preserve the CRT's native thread-local errno storage.
[assembly: PartiallyImaginedNativeApi("api-ms-win-crt-runtime-l1-1-0.dll", "_errno", ApiCategory.Thread,
    "ucrtbase!_errno", "api-ms-win-crt-private-l1-1-0!_o__errno", "ucrtbase!_o__errno")]
