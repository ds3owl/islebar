// IsleBarTap — a XAML "Tree Access Provider" loaded into explorer.exe through the documented
// XAML diagnostics API (InitializeXamlDiagnosticsEx, the channel Visual Studio's Live Visual Tree
// uses). It makes the taskbar's own search box transparent while IsleBar is
// running, so the pill sits in the space the search box keeps reserving and the real box never
// shows through when taskbar icons shift.
//
// Safety rules:
//   * Only Opacity is changed (layout, hit testing and automation stay intact, so the space stays
//     reserved and nothing else in the taskbar moves).
//   * The original look is restored as soon as the IsleBar process exits (crash included) or IsleBar
//     signals the "show" event (setting turned off).
//   * One tap per Explorer, for Explorer's whole life: when IsleBar ends the tap only restores the box and waits;
//     the next IsleBar run names itself the new owner (shared memory + "wake" event) instead of attaching again.
//     Attaching a second time to the same Explorer crashed it now and then (about 1 in 10, in Windows.UI.Xaml,
//     during AdviseVisualTreeChange - 8 times on 10-02/03); the first attach after Explorer starts never did.
//   * Every entry point catches everything: an exception escaping into explorer would take the
//     taskbar down.

#include <windows.h>
#include <ocidl.h>
#include <xamlom.h>
#undef GetCurrentTime   // windows.h macro clashes with a XAML method name

#include <winrt/base.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.UI.Core.h>
#include <winrt/Windows.UI.Xaml.h>

#include <mutex>
#include <thread>
#include <string>
#include <vector>

namespace
{
    // {28318384-B9C8-4AB9-8FBE-7BBE5571F408}
    constexpr CLSID CLSID_IsleBarTap = { 0x28318384, 0xb9c8, 0x4ab9, { 0x8f, 0xbe, 0x7b, 0xbe, 0x55, 0x71, 0xf4, 0x08 } };

    constexpr wchar_t ShowEventName[] = L"Local\\IsleBar.NativeSearchBox.Show";

    // Per Explorer process: "Local\\IsleBar.Tap.<explorer pid>.Owner" (a DWORD: the IsleBar pid that owns the hiding now)
    // and "...Wake" (IsleBar sets it after writing its pid). Names must match NativeSearchBox.cs.
    std::wstring TapObjectName(const wchar_t* suffix)
    {
        return L"Local\\IsleBar.Tap." + std::to_wstring(GetCurrentProcessId()) + L"." + suffix;
    }

    std::atomic<long> g_objects{ 0 };

    void Log(const std::wstring& line)
    {
        wchar_t path[MAX_PATH]{};
        if (GetEnvironmentVariableW(L"LOCALAPPDATA", path, MAX_PATH) == 0)
        {
            return;
        }

        std::wstring file = std::wstring(path) + L"\\IsleBar\\logs\\tap.log";
        HANDLE h = CreateFileW(file.c_str(), FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (h == INVALID_HANDLE_VALUE)
        {
            return;
        }

        SYSTEMTIME t{};
        GetLocalTime(&t);
        wchar_t stamp[32]{};
        swprintf_s(stamp, L"%02d-%02d %02d:%02d:%02d ", t.wMonth, t.wDay, t.wHour, t.wMinute, t.wSecond);
        std::wstring text = stamp + line + L"\r\n";
        int bytes = WideCharToMultiByte(CP_UTF8, 0, text.c_str(), static_cast<int>(text.size()), nullptr, 0, nullptr, nullptr);
        std::string utf8(bytes, '\0');
        WideCharToMultiByte(CP_UTF8, 0, text.c_str(), static_cast<int>(text.size()), utf8.data(), bytes, nullptr, nullptr);
        DWORD written = 0;
        WriteFile(h, utf8.data(), static_cast<DWORD>(utf8.size()), &written, nullptr);
        CloseHandle(h);
    }

    bool IsSearchBox(const VisualElement& element)
    {
        const std::wstring_view name = element.Name ? element.Name : L"";
        const std::wstring_view type = element.Type ? element.Type : L"";
        // Windows 11 24H2: the whole search box is one SearchButtonControl (box, text, gleam image inside).
        // Older builds named it Taskbar.SearchBoxButton.
        (void)name;
        return type == L"SearchUx.SearchUI.SearchButtonControl" || type == L"Taskbar.SearchBoxButton";
    }

    class Tap : public winrt::implements<Tap, IObjectWithSite, IVisualTreeServiceCallback2>
    {
    public:
        Tap() { ++g_objects; }
        ~Tap() { --g_objects; }

        // ---- IObjectWithSite ----
        HRESULT STDMETHODCALLTYPE SetSite(IUnknown* site) noexcept override
        {
            try
            {
                m_diag = nullptr;
                if (site == nullptr)
                {
                    return S_OK;
                }

                winrt::check_hresult(site->QueryInterface(IID_PPV_ARGS(m_diag.put())));

                BSTR init = nullptr;
                if (SUCCEEDED(m_diag->GetInitializationData(&init)) && init != nullptr)
                {
                    m_owner = wcstoul(init, nullptr, 10);
                    SysFreeString(init);
                }

                Log(L"attached, owner pid " + std::to_wstring(m_owner));

                // The diagnostics docs: AdviseVisualTreeChange can't be called from SetSite itself (it needs the UI
                // thread that is calling us right now), so ask for the tree from a worker that holds its own reference.
                StartOwnerWatch();
                std::thread([self = get_strong()]() noexcept { self->SubscribeToTree(); }).detach();
                return S_OK;
            }
            catch (...)
            {
                return winrt::to_hresult();
            }
        }

        HRESULT STDMETHODCALLTYPE GetSite(REFIID riid, void** site) noexcept override
        {
            if (!m_diag)
            {
                *site = nullptr;
                return E_FAIL;
            }

            return m_diag->QueryInterface(riid, site);
        }

        // ---- IVisualTreeServiceCallback ----
        HRESULT STDMETHODCALLTYPE OnVisualTreeChange(ParentChildRelation, VisualElement element, VisualMutationType mutation) noexcept override
        {
            try
            {
                if (mutation != Add || !m_diag)
                {
                    return S_OK;
                }

                ++m_seen;
                if (DiscoveryLogging())
                {
                    const std::wstring_view n = element.Name ? element.Name : L"";
                    const std::wstring_view ty = element.Type ? element.Type : L"";
                    if (n.find(L"Search") != std::wstring_view::npos || ty.find(L"Search") != std::wstring_view::npos)
                    {
                        Log(L"seen " + std::wstring(ty) + L" / " + std::wstring(n));
                    }
                }

                if (!IsSearchBox(element))
                {
                    return S_OK;
                }

                winrt::Windows::Foundation::IInspectable inspectable;
                winrt::check_hresult(m_diag->GetIInspectableFromHandle(element.Handle, reinterpret_cast<::IInspectable**>(winrt::put_abi(inspectable))));
                auto ui = inspectable.try_as<winrt::Windows::UI::Xaml::UIElement>();
                if (!ui)
                {
                    return S_OK;
                }

                std::lock_guard lock(m_gate);
                // forget boxes that are gone, and this one if it was seen before (it is added again on every re-layout)
                std::erase_if(m_found, [&ui](const auto& weak) { auto known = weak.get(); return !known || known == ui; });
                m_found.push_back(winrt::make_weak(ui));
                if (!m_hiding)
                {
                    return S_OK;
                }

                ui.Opacity(0.0);
                Log(std::wstring(L"hid ") + (element.Type ? element.Type : L"?") + L" / " + (element.Name ? element.Name : L"?"));
            }
            catch (...)
            {
                Log(L"hide failed " + std::to_wstring(static_cast<int>(winrt::to_hresult())));
            }

            return S_OK;
        }

        HRESULT STDMETHODCALLTYPE OnElementStateChanged(InstanceHandle, VisualElementState, LPCWSTR) noexcept override
        {
            return S_OK;
        }

    private:
        void SubscribeToTree() noexcept
        {
            try
            {
                auto service = m_diag.as<IVisualTreeService3>();
                winrt::check_hresult(service->AdviseVisualTreeChange(this));
                Log(L"advised");
            }
            catch (...)
            {
                Log(L"advise failed " + std::to_wstring(static_cast<int>(winrt::to_hresult())));
            }
        }

        // For Explorer's whole life: hide while an owner (an IsleBar process) is alive and hasn't signalled "show"; restore when it
        // goes away (exit or crash) or signals "show"; hide again when the next IsleBar run names itself owner. Never unadvised -
        // one listener per Explorer, so they can't pile up (E_UNEXPECTED after a few hundred, 10-02).
        void StartOwnerWatch()
        {
            std::thread([self = get_strong()]() noexcept {
                volatile DWORD* ownerPid = nullptr;
                try
                {
                    winrt::handle mapping{ CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, sizeof(DWORD), TapObjectName(L"Owner").c_str()) };
                    winrt::handle wake{ CreateEventW(nullptr, FALSE, FALSE, TapObjectName(L"Wake").c_str()) };
                    winrt::handle show{ CreateEventW(nullptr, TRUE, FALSE, ShowEventName) };
                    ownerPid = mapping ? static_cast<volatile DWORD*>(MapViewOfFile(mapping.get(), FILE_MAP_ALL_ACCESS, 0, 0, sizeof(DWORD))) : nullptr;
                    if (ownerPid != nullptr)
                    {
                        *ownerPid = self->m_owner;
                    }

                    winrt::handle owner{ self->m_owner != 0 ? OpenProcess(SYNCHRONIZE, FALSE, self->m_owner) : nullptr };
                    const bool showAtStart = show && WaitForSingleObject(show.get(), 0) == WAIT_OBJECT_0;
                    self->SetHiding(owner && !showAtStart);

                    while (true)
                    {
                        std::vector<HANDLE> waits;
                        if (wake)
                        {
                            waits.push_back(wake.get());
                        }

                        if (owner)
                        {
                            waits.push_back(owner.get());
                            if (show)
                            {
                                waits.push_back(show.get());
                            }
                        }

                        if (waits.empty())
                        {
                            break;   // nothing can ever wake us: stay restored
                        }

                        const DWORD hit = WaitForMultipleObjects(static_cast<DWORD>(waits.size()), waits.data(), FALSE, INFINITE);
                        if (hit >= WAIT_OBJECT_0 + waits.size())
                        {
                            break;
                        }

                        const HANDLE signalled = waits[hit - WAIT_OBJECT_0];
                        if (signalled == wake.get())
                        {
                            // a new IsleBar run: watch it instead, and hide again unless it asked for "show" in the meantime
                            const DWORD pid = ownerPid != nullptr ? *ownerPid : 0;
                            owner.attach(pid != 0 ? OpenProcess(SYNCHRONIZE, FALSE, pid) : nullptr);
                            const bool showWanted = show && WaitForSingleObject(show.get(), 0) == WAIT_OBJECT_0;
                            Log(L"owner now pid " + std::to_wstring(pid));
                            self->SetHiding(owner && !showWanted);
                        }
                        else
                        {
                            owner.close();   // owner gone, or "show": restore and wait for the next owner
                            self->SetHiding(false);
                        }
                    }
                }
                catch (...)
                {
                    Log(L"owner watch failed");
                }

                if (ownerPid != nullptr)
                {
                    UnmapViewOfFile(const_cast<DWORD*>(ownerPid));
                }

                self->SetHiding(false);
            }).detach();
        }

        // Sets the found search boxes' opacity on their own UI thread (0 hidden, 1 shown).
        void SetHiding(bool hide) noexcept
        {
            std::vector<winrt::weak_ref<winrt::Windows::UI::Xaml::UIElement>> found;
            {
                std::lock_guard lock(m_gate);
                if (m_hiding == hide)
                {
                    return;
                }

                m_hiding = hide;
                found = m_found;
            }

            const double opacity = hide ? 0.0 : 1.0;
            for (auto& weak : found)
            {
                try
                {
                    auto ui = weak.get();
                    auto dispatcher = ui ? ui.Dispatcher() : nullptr;   // a taskbar thread shutting down has none — never call through null
                    if (dispatcher)
                    {
                        dispatcher.RunAsync(winrt::Windows::UI::Core::CoreDispatcherPriority::Normal, [ui, opacity]() {
                            try
                            {
                                ui.Opacity(opacity);
                            }
                            catch (...)
                            {
                            }
                        });
                    }
                }
                catch (...)
                {
                }
            }

            Log(std::wstring(hide ? L"hidden again" : L"restored") + L" (elements seen " + std::to_wstring(m_seen.load()) + L")");
        }

        static bool DiscoveryLogging()
        {
            wchar_t v[4]{};
            return GetEnvironmentVariableW(L"ISLEBAR_TAP_DISCOVER", v, 4) > 0;
        }

        winrt::com_ptr<IXamlDiagnostics> m_diag;
        std::atomic<long> m_seen{ 0 };
        DWORD m_owner = 0;
        std::mutex m_gate;
        bool m_hiding = false;   // hidden only while the watcher holds a live owner
        std::vector<winrt::weak_ref<winrt::Windows::UI::Xaml::UIElement>> m_found;
    };

    // One class, one factory: hands out a new Tap for every request explorer's diagnostics host makes.
    struct TapFactory : winrt::implements<TapFactory, IClassFactory>
    {
        HRESULT STDMETHODCALLTYPE CreateInstance(IUnknown* outer, REFIID riid, void** result) noexcept override
        {
            if (result == nullptr)
            {
                return E_POINTER;
            }

            *result = nullptr;
            if (outer != nullptr)
            {
                return CLASS_E_NOAGGREGATION;   // a Tap is never part of another object
            }

            try
            {
                return winrt::make_self<Tap>()->QueryInterface(riid, result);
            }
            catch (...)
            {
                return winrt::to_hresult();
            }
        }

        HRESULT STDMETHODCALLTYPE LockServer(BOOL) noexcept override
        {
            return S_OK;   // lifetime is tracked by the live Tap count instead (DllCanUnloadNow)
        }
    };
}

_Check_return_ STDAPI DllGetClassObject(_In_ REFCLSID clsid, _In_ REFIID riid, _Outptr_ LPVOID* result)
{
    if (result == nullptr)
    {
        return E_POINTER;
    }

    *result = nullptr;
    if (!IsEqualCLSID(clsid, CLSID_IsleBarTap))
    {
        return CLASS_E_CLASSNOTAVAILABLE;
    }

    try
    {
        return winrt::make_self<TapFactory>()->QueryInterface(riid, result);
    }
    catch (...)
    {
        return winrt::to_hresult();
    }
}

__control_entrypoint(DllExport) STDAPI DllCanUnloadNow()
{
    // Stay loaded while any tap is alive — explorer keeps calling into our callbacks.
    return g_objects.load() == 0 ? S_OK : S_FALSE;
}
