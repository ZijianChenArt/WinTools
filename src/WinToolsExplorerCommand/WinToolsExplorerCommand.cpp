#include <windows.h>
#include <appmodel.h>
#include <shlobj.h>
#include <shobjidl.h>
#include <shlwapi.h>
#include <shellapi.h>
#include <string>
#include <vector>

#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "shell32.lib")
#pragma comment(lib, "shlwapi.lib")

// 此 CLSID 必须与 Package.appxmanifest 中的 COM 类和 Verb 完全一致。
static const CLSID CLSID_WinToolsOrganizeDesktop =
{ 0x6b6473e0, 0xe489, 0x46ca, { 0xa8, 0xe4, 0x21, 0x0f, 0xc4, 0xdd, 0xb4, 0x33 } };

static HMODULE g_module = nullptr;
static long g_objectCount = 0;

static bool PathsEqual(PCWSTR left, PCWSTR right)
{
    return left && right && _wcsicmp(left, right) == 0;
}

static bool ItemMatchesKnownFolder(IShellItem* item, REFKNOWNFOLDERID folderId)
{
    IShellItem* knownFolder = nullptr;
    if (SUCCEEDED(SHGetKnownFolderItem(folderId, KF_FLAG_DEFAULT, nullptr, IID_PPV_ARGS(&knownFolder))))
    {
        int order = 1;
        const bool equal = SUCCEEDED(item->Compare(knownFolder, SICHINT_CANONICAL, &order)) && order == 0;
        knownFolder->Release();
        if (equal) return true;
    }

    PWSTR itemPath = nullptr;
    PWSTR folderPath = nullptr;
    const bool equal = SUCCEEDED(item->GetDisplayName(SIGDN_FILESYSPATH, &itemPath)) &&
        SUCCEEDED(SHGetKnownFolderPath(folderId, KF_FLAG_DEFAULT, nullptr, &folderPath)) &&
        PathsEqual(itemPath, folderPath);
    CoTaskMemFree(itemPath);
    CoTaskMemFree(folderPath);
    return equal;
}

static bool IsDesktopBackground(IShellItemArray* items)
{
    if (!items) return false;

    DWORD count = 0;
    if (FAILED(items->GetCount(&count)) || count == 0) return false;

    IShellItem* item = nullptr;
    if (FAILED(items->GetItemAt(0, &item))) return false;
    const bool desktop = ItemMatchesKnownFolder(item, FOLDERID_Desktop) ||
        ItemMatchesKnownFolder(item, FOLDERID_PublicDesktop);
    item->Release();
    return desktop;
}

static std::wstring GetModuleDirectory()
{
    std::vector<wchar_t> path(32768);
    const DWORD length = GetModuleFileNameW(g_module, path.data(), static_cast<DWORD>(path.size()));
    if (length == 0 || length >= path.size()) return {};
    PathRemoveFileSpecW(path.data());
    return path.data();
}

static HRESULT ActivateWinTools()
{
    UINT32 length = 0;
    LONG result = GetCurrentPackageFamilyName(&length, nullptr);
    if (result == ERROR_INSUFFICIENT_BUFFER)
    {
        std::vector<wchar_t> family(length);
        if (GetCurrentPackageFamilyName(&length, family.data()) == ERROR_SUCCESS)
        {
            std::wstring appUserModelId(family.data());
            appUserModelId += L"!App";

            IApplicationActivationManager* manager = nullptr;
            HRESULT hr = CoCreateInstance(CLSID_ApplicationActivationManager, nullptr,
                CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&manager));
            if (SUCCEEDED(hr))
            {
                DWORD processId = 0;
                hr = manager->ActivateApplication(appUserModelId.c_str(),
                    L"--organize-desktop", AO_NONE, &processId);
                manager->Release();
                if (SUCCEEDED(hr)) return hr;
            }
        }
    }

    // 调试或包身份暂不可用时的兜底；正式 MSIX 安装使用上面的 AUMID 激活。
    const std::wstring executable = GetModuleDirectory() + L"\\WinTools.exe";
    const auto value = reinterpret_cast<INT_PTR>(ShellExecuteW(nullptr, L"open", executable.c_str(),
        L"--organize-desktop", nullptr, SW_SHOWNORMAL));
    return value > 32 ? S_OK : HRESULT_FROM_WIN32(static_cast<DWORD>(value));
}

class OrganizeDesktopCommand final : public IExplorerCommand
{
public:
    OrganizeDesktopCommand() : _refCount(1) { InterlockedIncrement(&g_objectCount); }

    IFACEMETHODIMP QueryInterface(REFIID iid, void** object) override
    {
        if (!object) return E_POINTER;
        *object = nullptr;
        if (iid == IID_IUnknown || iid == IID_IExplorerCommand)
        {
            *object = static_cast<IExplorerCommand*>(this);
            AddRef();
            return S_OK;
        }
        return E_NOINTERFACE;
    }

    IFACEMETHODIMP_(ULONG) AddRef() override { return InterlockedIncrement(&_refCount); }

    IFACEMETHODIMP_(ULONG) Release() override
    {
        const ULONG count = InterlockedDecrement(&_refCount);
        if (count == 0) delete this;
        return count;
    }

    IFACEMETHODIMP GetTitle(IShellItemArray*, PWSTR* title) override
    {
        if (!title) return E_POINTER;
        return SHStrDupW(L"整理桌面", title);
    }

    IFACEMETHODIMP GetIcon(IShellItemArray*, PWSTR* icon) override
    {
        if (!icon) return E_POINTER;
        const std::wstring executable = GetModuleDirectory() + L"\\WinTools.exe";
        return SHStrDupW(executable.c_str(), icon);
    }

    IFACEMETHODIMP GetToolTip(IShellItemArray*, PWSTR* toolTip) override
    {
        if (!toolTip) return E_POINTER;
        *toolTip = nullptr;
        return E_NOTIMPL;
    }

    IFACEMETHODIMP GetCanonicalName(GUID* name) override
    {
        if (!name) return E_POINTER;
        *name = CLSID_WinToolsOrganizeDesktop;
        return S_OK;
    }

    IFACEMETHODIMP GetState(IShellItemArray* items, BOOL, EXPCMDSTATE* state) override
    {
        if (!state) return E_POINTER;
        *state = IsDesktopBackground(items) ? ECS_ENABLED : ECS_HIDDEN;
        return S_OK;
    }

    IFACEMETHODIMP Invoke(IShellItemArray* items, IBindCtx*) override
    {
        return IsDesktopBackground(items) ? ActivateWinTools() : E_ACCESSDENIED;
    }

    IFACEMETHODIMP GetFlags(EXPCMDFLAGS* flags) override
    {
        if (!flags) return E_POINTER;
        *flags = ECF_DEFAULT;
        return S_OK;
    }

    IFACEMETHODIMP EnumSubCommands(IEnumExplorerCommand** commands) override
    {
        if (!commands) return E_POINTER;
        *commands = nullptr;
        return E_NOTIMPL;
    }

private:
    ~OrganizeDesktopCommand() { InterlockedDecrement(&g_objectCount); }
    long _refCount;
};

class CommandClassFactory final : public IClassFactory
{
public:
    CommandClassFactory() : _refCount(1) { InterlockedIncrement(&g_objectCount); }

    IFACEMETHODIMP QueryInterface(REFIID iid, void** object) override
    {
        if (!object) return E_POINTER;
        *object = nullptr;
        if (iid == IID_IUnknown || iid == IID_IClassFactory)
        {
            *object = static_cast<IClassFactory*>(this);
            AddRef();
            return S_OK;
        }
        return E_NOINTERFACE;
    }

    IFACEMETHODIMP_(ULONG) AddRef() override { return InterlockedIncrement(&_refCount); }

    IFACEMETHODIMP_(ULONG) Release() override
    {
        const ULONG count = InterlockedDecrement(&_refCount);
        if (count == 0) delete this;
        return count;
    }

    IFACEMETHODIMP CreateInstance(IUnknown* outer, REFIID iid, void** object) override
    {
        if (outer) return CLASS_E_NOAGGREGATION;
        auto* command = new (std::nothrow) OrganizeDesktopCommand();
        if (!command) return E_OUTOFMEMORY;
        const HRESULT hr = command->QueryInterface(iid, object);
        command->Release();
        return hr;
    }

    IFACEMETHODIMP LockServer(BOOL lock) override
    {
        if (lock) InterlockedIncrement(&g_objectCount);
        else InterlockedDecrement(&g_objectCount);
        return S_OK;
    }

private:
    ~CommandClassFactory() { InterlockedDecrement(&g_objectCount); }
    long _refCount;
};

extern "C" HRESULT __stdcall DllGetClassObject(REFCLSID clsid, REFIID iid, void** object)
{
    if (clsid != CLSID_WinToolsOrganizeDesktop) return CLASS_E_CLASSNOTAVAILABLE;
    auto* factory = new (std::nothrow) CommandClassFactory();
    if (!factory) return E_OUTOFMEMORY;
    const HRESULT hr = factory->QueryInterface(iid, object);
    factory->Release();
    return hr;
}

extern "C" HRESULT __stdcall DllCanUnloadNow()
{
    return g_objectCount == 0 ? S_OK : S_FALSE;
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        g_module = module;
        DisableThreadLibraryCalls(module);
    }
    return TRUE;
}
