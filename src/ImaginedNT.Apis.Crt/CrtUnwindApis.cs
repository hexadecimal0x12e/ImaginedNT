using ImaginedNT.Core.Runtime;

// Unwind entry points must run on the guest stack, after the native forwarder's observer has returned.
[assembly: PartiallyImaginedNativeApi("api-ms-win-crt-private-l1-1-0.dll", "_local_unwind", ApiCategory.Process, "ucrtbase!_local_unwind")]
[assembly: PartiallyImaginedNativeApi("api-ms-win-crt-private-l1-1-0.dll", "__C_specific_handler", ApiCategory.Process, "ucrtbase!__C_specific_handler")]
[assembly: PartiallyImaginedNativeApi("api-ms-win-crt-private-l1-1-0.dll", "_CxxThrowException", ApiCategory.Process,
    "ucrtbase!_CxxThrowException", "vcruntime140!_CxxThrowException")]
[assembly: PartiallyImaginedNativeApi("api-ms-win-crt-private-l1-1-0.dll", "__CxxFrameHandler3", ApiCategory.Process,
    "ucrtbase!__CxxFrameHandler3", "vcruntime140!__CxxFrameHandler3", "msvcrt!__CxxFrameHandler3")]
[assembly: PartiallyImaginedNativeApi("api-ms-win-crt-private-l1-1-0.dll", "__CxxFrameHandler4", ApiCategory.Process,
    "ucrtbase!__CxxFrameHandler4", "vcruntime140_1!__CxxFrameHandler4")]
[assembly: PartiallyImaginedNativeApi("api-ms-win-crt-private-l1-1-0.dll", "__C_specific_handler_noexcept", ApiCategory.Process,
    "ucrtbase!__C_specific_handler_noexcept", "vcruntime140!__C_specific_handler_noexcept")]
[assembly: PartiallyImaginedNativeApi("api-ms-win-crt-private-l1-1-0.dll", "_o___std_type_info_destroy_list", ApiCategory.Memory,
    "ucrtbase!_o___std_type_info_destroy_list", "ucrtbase!__std_type_info_destroy_list", "vcruntime140!__std_type_info_destroy_list")]
[assembly: PartiallyImaginedNativeApi("api-ms-win-crt-runtime-l1-1-0.dll", "_seh_filter_dll", ApiCategory.Process,
    "ucrtbase!_seh_filter_dll", "api-ms-win-crt-private-l1-1-0!_o__seh_filter_dll", "ucrtbase!_o__seh_filter_dll")]
