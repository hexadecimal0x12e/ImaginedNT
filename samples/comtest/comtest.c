// Minimal COM client for ImaginedNT's managed COM layer.
//
// This is a CRT-free PE with hand-written COM vtables, so it exercises the real COM ABI
// (IUnknown slots, interface pointers, out parameters, BSTRs) without pulling in the UCRT.
#include <windows.h>

#define COINIT_MULTITHREADED 0x0
#define CLSCTX_INPROC_SERVER 0x1

// Must match ImaginedNT.Apis.Com.ImaginedComTest.
static const CLSID CLSID_ImaginedComTest =
    {0xE7A1B2C3, 0x4D5E, 0x6F70, {0x81, 0x92, 0xA3, 0xB4, 0xC5, 0xD6, 0xE7, 0xF8}};
static const IID IID_IImaginedComTest =
    {0x4D2C9A10, 0x7B3E, 0x4F1A, {0x9C, 0x22, 0x5D, 0x77, 0x1B, 0x8E, 0x4A, 0x61}};
static const IID IID_IUnknownValue =
    {0x00000000, 0x0000, 0x0000, {0xC0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46}};

typedef struct IImaginedComTest IImaginedComTest;
typedef struct IImaginedComTestVtbl
{
    HRESULT (__stdcall *QueryInterface)(IImaginedComTest *, const IID *, void **);
    ULONG   (__stdcall *AddRef)(IImaginedComTest *);
    ULONG   (__stdcall *Release)(IImaginedComTest *);
    HRESULT (__stdcall *Add)(IImaginedComTest *, int a, int b, int *result);
    HRESULT (__stdcall *GetValue)(IImaginedComTest *, int *value);
    HRESULT (__stdcall *SetValue)(IImaginedComTest *, int value);
    HRESULT (__stdcall *GetName)(IImaginedComTest *, BSTR *name);
} IImaginedComTestVtbl;

struct IImaginedComTest
{
    const IImaginedComTestVtbl *lpVtbl;
};

__declspec(dllimport) HRESULT __stdcall CoInitializeEx(void *reserved, DWORD coInit);
__declspec(dllimport) void __stdcall CoUninitialize(void);
__declspec(dllimport) HRESULT __stdcall CoCreateInstance(const CLSID *clsid, void *outer, DWORD context,
                                                        const IID *iid, void **object);
__declspec(dllimport) void __stdcall SysFreeString(BSTR value);
__declspec(dllimport) void __stdcall ExitProcess(UINT code);

static int NameEquals(const wchar_t *value, const wchar_t *expected)
{
    if (value == 0) return 0;
    while (*expected != 0)
    {
        if (*value++ != *expected++) return 0;
    }
    return *value == 0;
}

void NtEntry(void)
{
    HRESULT hr = CoInitializeEx(0, COINIT_MULTITHREADED);
    if (FAILED(hr)) ExitProcess(1);

    int exitCode = 2;
    IImaginedComTest *object = 0;
    BSTR name = 0;

    hr = CoCreateInstance(&CLSID_ImaginedComTest, 0, CLSCTX_INPROC_SERVER, &IID_IImaginedComTest,
                          (void **)&object);
    if (FAILED(hr) || object == 0) goto done;

    int result = 0;
    hr = object->lpVtbl->Add(object, 2, 3, &result);
    if (FAILED(hr) || result != 5) { exitCode = 3; goto done; }

    hr = object->lpVtbl->SetValue(object, 40);
    if (FAILED(hr)) { exitCode = 4; goto done; }

    int value = 0;
    hr = object->lpVtbl->GetValue(object, &value);
    if (FAILED(hr) || value != 40) { exitCode = 5; goto done; }

    hr = object->lpVtbl->GetName(object, &name);
    if (FAILED(hr) || !NameEquals(name, L"ImaginedNT")) { exitCode = 6; goto done; }
    SysFreeString(name);
    name = 0;

    void *unknown = 0;
    hr = object->lpVtbl->QueryInterface(object, &IID_IUnknownValue, &unknown);
    if (FAILED(hr) || unknown == 0) { exitCode = 7; goto done; }
    ((IImaginedComTest *)unknown)->lpVtbl->Release((IImaginedComTest *)unknown);

    ULONG references = object->lpVtbl->AddRef(object);
    if (references < 2) { exitCode = 8; goto done; }
    references = object->lpVtbl->Release(object);
    if (references != 1) { exitCode = 9; goto done; }

    exitCode = 42;

done:
    if (name != 0) SysFreeString(name);
    if (object != 0) object->lpVtbl->Release(object);
    CoUninitialize();
    ExitProcess((UINT)exitCode);
}
