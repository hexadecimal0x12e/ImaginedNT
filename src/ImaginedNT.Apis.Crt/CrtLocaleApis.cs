using ImaginedNT.Core.Runtime;

namespace ImaginedNT.Apis.Crt;

public static class CrtLocaleApis
{
    private const string Module = "api-ms-win-crt-locale-l1-1-0.dll";

    [PartiallyImaginedApi(Module, "___lc_codepage_func", ApiCategory.Memory,
        "ucrtbase!___lc_codepage_func", "msvcrt!___lc_codepage_func",
        "api-ms-win-crt-private-l1-1-0!_o____lc_codepage_func", "ucrtbase!_o____lc_codepage_func")]
    [PartiallyImaginedApi(Module, "___lc_collate_cp_func", ApiCategory.Memory,
        "ucrtbase!___lc_collate_cp_func", "msvcrt!___lc_collate_cp_func",
        "api-ms-win-crt-private-l1-1-0!_o____lc_collate_cp_func", "ucrtbase!_o____lc_collate_cp_func")]
    [PartiallyImaginedApi(Module, "___lc_locale_name_func", ApiCategory.Memory,
        "ucrtbase!___lc_locale_name_func", "msvcrt!___lc_locale_name_func",
        "api-ms-win-crt-private-l1-1-0!_o____lc_locale_name_func", "ucrtbase!_o____lc_locale_name_func")]
    [PartiallyImaginedApi(Module, "___mb_cur_max_func", ApiCategory.Memory,
        "ucrtbase!___mb_cur_max_func", "msvcrt!___mb_cur_max_func",
        "api-ms-win-crt-private-l1-1-0!_o____mb_cur_max_func", "ucrtbase!_o____mb_cur_max_func")]
    [PartiallyImaginedApi(Module, "___mb_cur_max_l_func", ApiCategory.Memory,
        "ucrtbase!___mb_cur_max_l_func", "msvcrt!___mb_cur_max_l_func",
        "api-ms-win-crt-private-l1-1-0!_o____mb_cur_max_l_func", "ucrtbase!_o____mb_cur_max_l_func")]
    [PartiallyImaginedApi(Module, "__initialize_lconv_for_unsigned_char", ApiCategory.Memory,
        "ucrtbase!__initialize_lconv_for_unsigned_char", "msvcrt!__initialize_lconv_for_unsigned_char",
        "api-ms-win-crt-private-l1-1-0!_o___initialize_lconv_for_unsigned_char", "ucrtbase!_o___initialize_lconv_for_unsigned_char")]
    [PartiallyImaginedApi(Module, "__pctype_func", ApiCategory.Memory,
        "ucrtbase!__pctype_func", "msvcrt!__pctype_func",
        "api-ms-win-crt-private-l1-1-0!_o___pctype_func", "ucrtbase!_o___pctype_func")]
    [PartiallyImaginedApi(Module, "__pwctype_func", ApiCategory.Memory,
        "ucrtbase!__pwctype_func", "msvcrt!__pwctype_func",
        "api-ms-win-crt-private-l1-1-0!_o___pwctype_func", "ucrtbase!_o___pwctype_func")]
    [PartiallyImaginedApi(Module, "_configthreadlocale", ApiCategory.Memory,
        "ucrtbase!_configthreadlocale", "msvcrt!_configthreadlocale",
        "api-ms-win-crt-private-l1-1-0!_o__configthreadlocale", "ucrtbase!_o__configthreadlocale")]
    [PartiallyImaginedApi(Module, "_create_locale", ApiCategory.Memory,
        "ucrtbase!_create_locale", "msvcrt!_create_locale",
        "api-ms-win-crt-private-l1-1-0!_o__create_locale", "ucrtbase!_o__create_locale")]
    [PartiallyImaginedApi(Module, "_free_locale", ApiCategory.Memory,
        "ucrtbase!_free_locale", "msvcrt!_free_locale",
        "api-ms-win-crt-private-l1-1-0!_o__free_locale", "ucrtbase!_o__free_locale")]
    [PartiallyImaginedApi(Module, "_get_current_locale", ApiCategory.Memory,
        "ucrtbase!_get_current_locale", "msvcrt!_get_current_locale",
        "api-ms-win-crt-private-l1-1-0!_o__get_current_locale", "ucrtbase!_o__get_current_locale")]
    [PartiallyImaginedApi(Module, "_getmbcp", ApiCategory.Memory,
        "ucrtbase!_getmbcp", "msvcrt!_getmbcp",
        "api-ms-win-crt-private-l1-1-0!_o__getmbcp", "ucrtbase!_o__getmbcp")]
    [PartiallyImaginedApi(Module, "_lock_locales", ApiCategory.Synchronization,
        "ucrtbase!_lock_locales", "msvcrt!_lock_locales",
        "api-ms-win-crt-private-l1-1-0!_o__lock_locales", "ucrtbase!_o__lock_locales")]
    [PartiallyImaginedApi(Module, "_setmbcp", ApiCategory.Memory,
        "ucrtbase!_setmbcp", "msvcrt!_setmbcp",
        "api-ms-win-crt-private-l1-1-0!_o__setmbcp", "ucrtbase!_o__setmbcp")]
    [PartiallyImaginedApi(Module, "_unlock_locales", ApiCategory.Synchronization,
        "ucrtbase!_unlock_locales", "msvcrt!_unlock_locales",
        "api-ms-win-crt-private-l1-1-0!_o__unlock_locales", "ucrtbase!_o__unlock_locales")]
    [PartiallyImaginedApi(Module, "_wcreate_locale", ApiCategory.Memory,
        "ucrtbase!_wcreate_locale", "msvcrt!_wcreate_locale",
        "api-ms-win-crt-private-l1-1-0!_o__wcreate_locale", "ucrtbase!_o__wcreate_locale")]
    [PartiallyImaginedApi(Module, "_wsetlocale", ApiCategory.Memory,
        "ucrtbase!_wsetlocale", "msvcrt!_wsetlocale",
        "api-ms-win-crt-private-l1-1-0!_o__wsetlocale", "ucrtbase!_o__wsetlocale")]
    [PartiallyImaginedApi(Module, "localeconv", ApiCategory.Memory,
        "ucrtbase!localeconv", "msvcrt!localeconv",
        "api-ms-win-crt-private-l1-1-0!_o_localeconv", "ucrtbase!_o_localeconv")]
    [PartiallyImaginedApi(Module, "setlocale", ApiCategory.Memory,
        "ucrtbase!setlocale", "msvcrt!setlocale",
        "api-ms-win-crt-private-l1-1-0!_o_setlocale", "ucrtbase!_o_setlocale")]
    private static ulong Invoke(ApiCallContext ctx) => CrtProcessState.LocaleCall(ctx);
}
