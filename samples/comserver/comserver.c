// Minimal in-process COM server used to test CoGetClassObject / DllGetClassObject.
//
// The DLL is a CRT-free PE; it implements IUnknown, IClassFactory and the same
// IImaginedComTest ABI as the managed sample. The runtime can load it as a guest module and
// return its guest pointers to a guest caller, or call DllGetClassObject from managed code.
#include <windows.h>

// Export the COM server entry points without fighting the linkage of the combaseapi.h
// declarations (STDAPI already fixes the calling convention).
#pragma comment(linker, "/EXPORT:DllGetClassObject")
#pragma comment(linker, "/EXPORT:DllCanUnloadNow")

static const CLSID CLSID_ImaginedComServer =
    {0xA1B2C3D4, 0x1122, 0x3344, {0x55, 0x66, 0x77, 0x88, 0x99, 0xAA, 0xBB, 0xCC}};
static const IID IID_IImaginedComTest =
    {0x4D2C9A10, 0x7B3E, 0x4F1A, {0x9C, 0x22, 0x5D, 0x77, 0x1B, 0x8E, 0x4A, 0x61}};
static const IID IID_IUnknownValue =
    {0x00000000, 0x0000, 0x0000, {0xC0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46}};
static const IID IID_IClassFactoryValue =
    {0x00000001, 0x0000, 0x0000, {0xC0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46}};

__declspec(dllimport) BSTR __stdcall SysAllocString(const wchar_t *text);

typedef struct IServerTest IServerTest;
typedef struct IServerTestVtbl
{
    HRESULT (__stdcall *QueryInterface)(IServerTest *, const IID *, void **);
    ULONG   (__stdcall *AddRef)(IServerTest *);
    ULONG   (__stdcall *Release)(IServerTest *);
    HRESULT (__stdcall *Add)(IServerTest *, int a, int b, int *result);
    HRESULT (__stdcall *GetValue)(IServerTest *, int *value);
    HRESULT (__stdcall *SetValue)(IServerTest *, int value);
    HRESULT (__stdcall *GetName)(IServerTest *, BSTR *name);
} IServerTestVtbl;

struct IServerTest
{
    const IServerTestVtbl *lpVtbl;
};

typedef struct IServerFactory IServerFactory;
typedef struct IServerFactoryVtbl
{
    HRESULT (__stdcall *QueryInterface)(IServerFactory *, const IID *, void **);
    ULONG   (__stdcall *AddRef)(IServerFactory *);
    ULONG   (__stdcall *Release)(IServerFactory *);
    HRESULT (__stdcall *CreateInstance)(IServerFactory *, void *outer, const IID *, void **);
    HRESULT (__stdcall *LockServer)(IServerFactory *, BOOL lock);
} IServerFactoryVtbl;

struct IServerFactory
{
    const IServerFactoryVtbl *lpVtbl;
};

static LONG g_factoryRefs;
static LONG g_objectRefs;
static LONG g_lockCount;
static int g_value;
static int g_objectAlive;

static int GuidEquals(const GUID *left, const GUID *right)
{
    int index;
    if (left == 0 || right == 0) return 0;
    if (left->Data1 != right->Data1 || left->Data2 != right->Data2 || left->Data3 != right->Data3)
        return 0;
    for (index = 0; index < 8; index++)
        if (left->Data4[index] != right->Data4[index]) return 0;
    return 1;
}

static HRESULT __stdcall Object_QueryInterface(IServerTest *self, const IID *iid, void **object)
{
    if (object == 0) return E_POINTER;
    *object = 0;
    if (GuidEquals(iid, &IID_IUnknownValue) || GuidEquals(iid, &IID_IImaginedComTest))
    {
        *object = self;
        ++g_objectRefs;
        return S_OK;
    }
    return E_NOINTERFACE;
}

static ULONG __stdcall Object_AddRef(IServerTest *self)
{
    (void)self;
    return (ULONG)++g_objectRefs;
}

static ULONG __stdcall Object_Release(IServerTest *self)
{
    LONG refs;
    (void)self;
    refs = --g_objectRefs;
    if (refs <= 0)
    {
        g_objectRefs = 0;
        g_objectAlive = 0;
    }
    return (ULONG)refs;
}

static HRESULT __stdcall Object_Add(IServerTest *self, int a, int b, int *result)
{
    (void)self;
    if (result == 0) return E_POINTER;
    *result = a + b + 2000;
    return S_OK;
}

static HRESULT __stdcall Object_GetValue(IServerTest *self, int *value)
{
    (void)self;
    if (value == 0) return E_POINTER;
    *value = g_value;
    return S_OK;
}

static HRESULT __stdcall Object_SetValue(IServerTest *self, int value)
{
    (void)self;
    g_value = value;
    return S_OK;
}

static HRESULT __stdcall Object_GetName(IServerTest *self, BSTR *name)
{
    (void)self;
    if (name == 0) return E_POINTER;
    *name = SysAllocString(L"ImaginedComServer");
    return *name == 0 ? E_OUTOFMEMORY : S_OK;
}

static const IServerTestVtbl g_objectVtbl =
{
    Object_QueryInterface,
    Object_AddRef,
    Object_Release,
    Object_Add,
    Object_GetValue,
    Object_SetValue,
    Object_GetName
};

static IServerTest g_object = {&g_objectVtbl};

static HRESULT __stdcall Factory_QueryInterface(IServerFactory *self, const IID *iid, void **object)
{
    if (object == 0) return E_POINTER;
    *object = 0;
    if (GuidEquals(iid, &IID_IUnknownValue) || GuidEquals(iid, &IID_IClassFactoryValue))
    {
        *object = self;
        ++g_factoryRefs;
        return S_OK;
    }
    return E_NOINTERFACE;
}

static ULONG __stdcall Factory_AddRef(IServerFactory *self)
{
    (void)self;
    return (ULONG)++g_factoryRefs;
}

static ULONG __stdcall Factory_Release(IServerFactory *self)
{
    LONG refs;
    (void)self;
    refs = --g_factoryRefs;
    if (refs < 0) g_factoryRefs = 0;
    return (ULONG)(refs < 0 ? 0 : refs);
}

static HRESULT __stdcall Factory_CreateInstance(IServerFactory *self, void *outer, const IID *iid,
                                                void **object)
{
    (void)self;
    if (object == 0) return E_POINTER;
    *object = 0;
    if (outer != 0) return CLASS_E_NOAGGREGATION;
    if (!GuidEquals(iid, &IID_IUnknownValue) && !GuidEquals(iid, &IID_IImaginedComTest))
        return E_NOINTERFACE;
    if (g_objectAlive == 0)
    {
        g_objectAlive = 1;
        g_objectRefs = 1;
        g_value = 500;
    }
    else
    {
        ++g_objectRefs;
    }
    *object = (void *)&g_object;
    return S_OK;
}

static HRESULT __stdcall Factory_LockServer(IServerFactory *self, BOOL lock)
{
    (void)self;
    if (lock) ++g_lockCount;
    else --g_lockCount;
    if (g_lockCount < 0) g_lockCount = 0;
    return S_OK;
}

static const IServerFactoryVtbl g_factoryVtbl =
{
    Factory_QueryInterface,
    Factory_AddRef,
    Factory_Release,
    Factory_CreateInstance,
    Factory_LockServer
};

static IServerFactory g_factory = {&g_factoryVtbl};

STDAPI DllGetClassObject(REFCLSID clsid, REFIID iid, LPVOID *object)
{
    if (object == 0) return E_POINTER;
    *object = 0;
    if (!GuidEquals(clsid, &CLSID_ImaginedComServer)) return CLASS_E_CLASSNOTAVAILABLE;
    if (!GuidEquals(iid, &IID_IUnknownValue) && !GuidEquals(iid, &IID_IClassFactoryValue))
        return E_NOINTERFACE;
    ++g_factoryRefs;
    *object = (LPVOID)&g_factory;
    return S_OK;
}

STDAPI DllCanUnloadNow(void)
{
    return (g_objectRefs == 0 && g_lockCount == 0) ? S_OK : S_FALSE;
}

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID reserved)
{
    (void)instance;
    (void)reason;
    (void)reserved;
    return TRUE;
}
