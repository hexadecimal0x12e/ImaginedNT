using ImaginedNT.Core.Runtime;

namespace ImaginedNT.Apis.Crt;

public static class CrtStartupApis
{
    private const string Module = "api-ms-win-crt-runtime-l1-1-0.dll";

    [PartiallyImaginedApi(Module, "_set_app_type", ApiCategory.Process, "ucrtbase!_set_app_type",
        "api-ms-win-crt-private-l1-1-0!_o__set_app_type", "ucrtbase!_o__set_app_type")]
    private static ulong SetAppType(ApiCallContext ctx) => 0;

    [PartiallyImaginedApi(Module, "_configure_narrow_argv", ApiCategory.Process, "ucrtbase!_configure_narrow_argv",
        "api-ms-win-crt-private-l1-1-0!_o__configure_narrow_argv", "ucrtbase!_o__configure_narrow_argv")]
    private static ulong ConfigureNarrowArguments(ApiCallContext ctx) => CrtProcessState.ConfigureArguments(ctx, 1);

    [PartiallyImaginedApi(Module, "_configure_wide_argv", ApiCategory.Process, "ucrtbase!_configure_wide_argv",
        "api-ms-win-crt-private-l1-1-0!_o__configure_wide_argv", "ucrtbase!_o__configure_wide_argv")]
    private static ulong ConfigureWideArguments(ApiCallContext ctx) => CrtProcessState.ConfigureArguments(ctx, 2);

    [PartiallyImaginedApi(Module, "__p___argc", ApiCategory.Process, "ucrtbase!__p___argc",
        "api-ms-win-crt-private-l1-1-0!_o___p___argc", "ucrtbase!_o___p___argc")]
    private static ulong ArgumentCountPointer(ApiCallContext ctx) => CrtProcessState.GetArgumentPointer(ctx, 0);

    [PartiallyImaginedApi(Module, "__p___argv", ApiCategory.Process, "ucrtbase!__p___argv",
        "api-ms-win-crt-private-l1-1-0!_o___p___argv", "ucrtbase!_o___p___argv")]
    private static ulong NarrowArgumentsPointer(ApiCallContext ctx) => CrtProcessState.GetArgumentPointer(ctx, 8);

    [PartiallyImaginedApi(Module, "__p___wargv", ApiCategory.Process, "ucrtbase!__p___wargv",
        "api-ms-win-crt-private-l1-1-0!_o___p___wargv", "ucrtbase!_o___p___wargv")]
    private static ulong WideArgumentsPointer(ApiCallContext ctx) => CrtProcessState.GetArgumentPointer(ctx, 16);

    [PartiallyImaginedApi(Module, "_initialize_narrow_environment", ApiCategory.Process, "ucrtbase!_initialize_narrow_environment",
        "api-ms-win-crt-private-l1-1-0!_o__initialize_narrow_environment", "ucrtbase!_o__initialize_narrow_environment")]
    private static ulong InitializeNarrowEnvironment(ApiCallContext ctx) =>
        CrtProcessState.GetInitialEnvironment(ctx, 1) != 0 ? 0UL : uint.MaxValue;

    [PartiallyImaginedApi(Module, "_initialize_wide_environment", ApiCategory.Process, "ucrtbase!_initialize_wide_environment",
        "api-ms-win-crt-private-l1-1-0!_o__initialize_wide_environment", "ucrtbase!_o__initialize_wide_environment")]
    private static ulong InitializeWideEnvironment(ApiCallContext ctx) =>
        CrtProcessState.GetInitialEnvironment(ctx, 2) != 0 ? 0UL : uint.MaxValue;

    [PartiallyImaginedApi(Module, "_get_initial_narrow_environment", ApiCategory.Process, "ucrtbase!_get_initial_narrow_environment",
        "api-ms-win-crt-private-l1-1-0!_o__get_initial_narrow_environment", "ucrtbase!_o__get_initial_narrow_environment")]
    private static ulong InitialNarrowEnvironment(ApiCallContext ctx) => CrtProcessState.GetInitialEnvironment(ctx, 1);

    [PartiallyImaginedApi(Module, "_get_initial_wide_environment", ApiCategory.Process, "ucrtbase!_get_initial_wide_environment",
        "api-ms-win-crt-private-l1-1-0!_o__get_initial_wide_environment", "ucrtbase!_o__get_initial_wide_environment")]
    private static ulong InitialWideEnvironment(ApiCallContext ctx) => CrtProcessState.GetInitialEnvironment(ctx, 2);
}
