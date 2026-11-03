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
//   * One tap per Explorer and tap build, for Explorer's whole life: when IsleBar ends the tap only restores the box and
//     waits; the next IsleBar run with the same tap names itself the new owner (shared memory + "wake" event) instead of
//     attaching again. Attaching a second time to the same Explorer crashed it now and then (about 1 in 10, in
//     Windows.UI.Xaml, during AdviseVisualTreeChange - 8 times on 10-02/03); the first attach after Explorer starts never
//     did. An IsleBar update that ships a different tap still wakes the old one while this Explorer lives; the new tap
//     goes in on the next Explorer start.
//   * Only the main taskbar's search box (Shell_TrayWnd). With the taskbar on all displays the secondary taskbars live in
//     this same Explorer, but no pill covers their boxes, so they are left alone (see HostOf).
//   * Every entry point catches everything: an exception escaping into explorer would take the
//     taskbar down.

#include <windows.h>
#include <ocidl.h>
#include <xamlom.h>
#include <windows.ui.xaml.hosting.desktopwindowxamlsource.h>
#undef GetCurrentTime   // windows.h macro clashes with a XAML method name

#include <winrt/base.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.UI.Core.h>
#include <winrt/Windows.UI.Xaml.h>
#include <winrt/Windows.UI.Xaml.Hosting.h>

#include <algorithm>
#include <atomic>
#include <mutex>
#include <thread>
#include <string>
#include <vector>

namespace
{
    // {28318384-B9C8-4AB9-8FBE-7BBE5571F408}
    constexpr CLSID CLSID_IsleBarTap = { 0x28318384, 0xb9c8, 0x4ab9, { 0x8f, 0xbe, 0x7b, 0xbe, 0x55, 0x71, 0xf4, 0x08 } };

    constexpr wchar_t ShowEventName[] = L"Local\\IsleBar.NativeSearchBox.Show";

    // Names must match NativeSearchBox.cs. Per Explorer process and tap build ("tag": the content hash IsleBar names the staged
    // copy by, passed in the initialization data), so a bar shipping a different tap never wakes an older one still loaded here:
    //   "Local\\IsleBar.Tap.<explorer pid>.<tag>.Owner"  a DWORD: the IsleBar pid that owns the hiding now (0: nobody - restore)
    //   "Local\\IsleBar.Tap.<explorer pid>.<tag>.Wake"   IsleBar sets it after writing the pid
    //   "Local\\IsleBar.Tap.<explorer pid>.Current"      the tag of the tap that took the latest owner, so a bar with another tap
    //                                                   knows which one to wake
    // Taps from before tags used "Local\\IsleBar.Tap.<explorer pid>.Owner" / ".Wake"; the bar wakes those the same way.
    constexpr size_t TagChars = 32;   // size of Current in wchar_t, NUL included

    std::wstring TapPrefix()
    {
        return L"Local\\IsleBar.Tap." + std::to_wstring(GetCurrentProcessId()) + L".";
    }

    std::wstring TapObjectName(const std::wstring& tag, const wchar_t* suffix)
    {
        return TapPrefix() + tag + L"." + suffix;
    }

    // Letters and digits only (it becomes part of kernel object names), short enough for Current.
    std::wstring ParseTag(const wchar_t* text)
    {
        std::wstring tag;
        for (; text != nullptr && *text != L'\0' && tag.size() < TagChars - 1; ++text)
        {
            const wchar_t c = *text;
            if ((c >= L'0' && c <= L'9') || (c >= L'A' && c <= L'Z') || (c >= L'a' && c <= L'z'))
            {
                tag.push_back(c);
            }
        }

        return tag.empty() ? L"untagged" : tag;
    }

    constexpr std::wstring_view XamlSourceType = L"Windows.UI.Xaml.Hosting.DesktopWindowXamlSource";

    enum class Host
    {
        Main,        // Shell_TrayWnd: the pill covers this box
        Secondary,   // Shell_SecondaryTrayWnd: nothing covers it, leave it visible
        Unknown,     // couldn't tell: hide it, as before the check existed, rather than lose the main box
    };

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

                // "<owner pid> <tag>" (NativeSearchBox.cs)
                BSTR init = nullptr;
                wchar_t* rest = nullptr;
                if (SUCCEEDED(m_diag->GetInitializationData(&init)) && init != nullptr)
                {
                    m_owner = wcstoul(init, &rest, 10);
                }

                m_tag = ParseTag(rest);
                SysFreeString(init);   // after ParseTag: rest points into it
                Log(L"attached, owner pid " + std::to_wstring(m_owner) + L", tap " + m_tag);

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
        HRESULT STDMETHODCALLTYPE OnVisualTreeChange(ParentChildRelation relation, VisualElement element, VisualMutationType mutation) noexcept override
        {
            try
            {
                if (!m_diag)
                {
                    return S_OK;
                }

                if (mutation == Remove)
                {
                    std::lock_guard lock(m_gate);
                    std::erase(m_sources, element.Handle);   // a closed island (taskbar gone with its display)
                    return S_OK;
                }

                if (mutation != Add)
                {
                    return S_OK;
                }

                ++m_seen;
                const std::wstring_view type = element.Type ? element.Type : L"";
                if (DiscoveryLogging())
                {
                    const std::wstring_view n = element.Name ? element.Name : L"";
                    if (n.find(L"Search") != std::wstring_view::npos || type.find(L"Search") != std::wstring_view::npos || relation.Parent == 0)
                    {
                        Log(L"seen " + std::wstring(type) + L" / " + std::wstring(n) + (relation.Parent == 0 ? L" (root)" : L""));
                    }
                }

                if (IsXamlSource(relation, element, type))
                {
                    std::lock_guard lock(m_gate);
                    if (std::find(m_sources.begin(), m_sources.end(), element.Handle) == m_sources.end())
                    {
                        m_sources.push_back(element.Handle);
                    }

                    return S_OK;
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

                std::wstring hostClass;
                const Host host = HostOf(ui, hostClass);
                const std::wstring what = std::wstring(type) + L" / " + (element.Name ? element.Name : L"") + L" on "
                    + (hostClass.empty() ? std::wstring(L"unknown host") : hostClass);

                std::lock_guard lock(m_gate);
                // forget boxes that are gone, and this one if it was seen before (it is added again on every re-layout)
                bool known = false;
                std::erase_if(m_found, [&ui, &known](const auto& weak) {
                    auto box = weak.get();
                    known = known || (box && box == ui);
                    return !box || box == ui;
                });

                if (host == Host::Secondary)
                {
                    // Hidden earlier while its island wasn't known yet (Unknown hides): give it back now that it is.
                    if (known && m_hiding)
                    {
                        ui.Opacity(1.0);
                        Log(L"gave back " + what);
                    }

                    return S_OK;
                }

                m_found.push_back(winrt::make_weak(ui));
                if (!m_hiding)
                {
                    return S_OK;
                }

                ui.Opacity(0.0);
                Log(L"hid " + what);
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
        // A XAML island: Live Visual Tree shows each DesktopWindowXamlSource as a root with the island's content under it.
        // Matched by type name, and for roots also by interface in case the reported name ever differs.
        bool IsXamlSource(const ParentChildRelation& relation, const VisualElement& element, std::wstring_view type) const
        {
            if (type == XamlSourceType)
            {
                return true;
            }

            if (relation.Parent != 0)
            {
                return false;
            }

            try
            {
                winrt::Windows::Foundation::IInspectable inspectable;
                winrt::check_hresult(m_diag->GetIInspectableFromHandle(element.Handle, reinterpret_cast<::IInspectable**>(winrt::put_abi(inspectable))));
                return inspectable.try_as<IDesktopWindowXamlSourceNative>() != nullptr;
            }
            catch (...)
            {
                return false;
            }
        }

        // Which taskbar window an element is drawn in. Explorer puts each taskbar's XAML in its own XAML island
        // (DesktopWindowXamlSource) inside that taskbar's window: Shell_TrayWnd for the main one, Shell_SecondaryTrayWnd for
        // each other display when the taskbar shows on all displays. All of them live in this one Explorer, and the box's
        // type and name are the same on every taskbar, so the element alone can't tell. What can, using documented APIs only:
        //   * every island has its own XamlRoot (UIElement.XamlRoot), so the island whose Content shares the box's XamlRoot
        //     is the one the box is in (the diagnostics tree reports the islands as elements; IsXamlSource collects them);
        //   * IDesktopWindowXamlSourceNative::get_WindowHandle gives that island's window, a child of the taskbar window,
        //     so its top-level ancestor (GetAncestor GA_ROOT) is the taskbar window, and the class name says which one.
        // Runs on the box's UI thread (diagnostics callbacks come on the tree's own thread). Islands of other taskbars may
        // belong to other threads: touching them throws RPC_E_WRONG_THREAD, and they can't hold this box anyway, so skip.
        Host HostOf(const winrt::Windows::UI::Xaml::UIElement& ui, std::wstring& hostClass)
        {
            try
            {
                std::vector<InstanceHandle> sources;
                {
                    std::lock_guard lock(m_gate);
                    sources = m_sources;
                }

                const auto root = ui.XamlRoot();
                if (!root)
                {
                    return Host::Unknown;
                }

                // compare COM identities, not whichever interface pointer each call happened to return
                const auto rootId = root.as<winrt::Windows::Foundation::IUnknown>();
                for (const InstanceHandle handle : sources)
                {
                    try
                    {
                        winrt::Windows::Foundation::IInspectable inspectable;
                        winrt::check_hresult(m_diag->GetIInspectableFromHandle(handle, reinterpret_cast<::IInspectable**>(winrt::put_abi(inspectable))));
                        const auto source = inspectable.try_as<winrt::Windows::UI::Xaml::Hosting::DesktopWindowXamlSource>();
                        if (!source)
                        {
                            continue;
                        }

                        const auto content = source.Content();
                        const auto contentRoot = content ? content.XamlRoot() : nullptr;
                        if (!contentRoot || contentRoot.as<winrt::Windows::Foundation::IUnknown>() != rootId)
                        {
                            continue;
                        }

                        HWND island = nullptr;
                        winrt::check_hresult(source.as<IDesktopWindowXamlSourceNative>()->get_WindowHandle(&island));
                        const HWND top = island != nullptr ? GetAncestor(island, GA_ROOT) : nullptr;
                        wchar_t name[64]{};
                        if (top == nullptr || GetClassNameW(top, name, 64) == 0)
                        {
                            return Host::Unknown;
                        }

                        hostClass = name;
                        if (hostClass == L"Shell_TrayWnd")
                        {
                            return Host::Main;
                        }

                        return hostClass == L"Shell_SecondaryTrayWnd" ? Host::Secondary : Host::Unknown;
                    }
                    catch (...)
                    {
                    }
                }
            }
            catch (...)
            {
            }

            return Host::Unknown;
        }

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
        // one listener per Explorer and tap build, so they can't pile up (E_UNEXPECTED after a few hundred, 10-02).
        void StartOwnerWatch()
        {
            std::thread([self = get_strong()]() noexcept {
                volatile DWORD* ownerPid = nullptr;
                wchar_t* currentTag = nullptr;
                try
                {
                    constexpr DWORD CurrentBytes = static_cast<DWORD>(TagChars * sizeof(wchar_t));
                    winrt::handle mapping{ CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, sizeof(DWORD), TapObjectName(self->m_tag, L"Owner").c_str()) };
                    winrt::handle wake{ CreateEventW(nullptr, FALSE, FALSE, TapObjectName(self->m_tag, L"Wake").c_str()) };
                    winrt::handle show{ CreateEventW(nullptr, TRUE, FALSE, ShowEventName) };
                    // held open for as long as this watcher runs, so Current outlives the bar that reads it
                    winrt::handle current{ CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, CurrentBytes, (TapPrefix() + L"Current").c_str()) };
                    ownerPid = mapping ? static_cast<volatile DWORD*>(MapViewOfFile(mapping.get(), FILE_MAP_ALL_ACCESS, 0, 0, sizeof(DWORD))) : nullptr;
                    currentTag = current ? static_cast<wchar_t*>(MapViewOfFile(current.get(), FILE_MAP_ALL_ACCESS, 0, 0, CurrentBytes)) : nullptr;
                    if (ownerPid != nullptr)
                    {
                        *ownerPid = self->m_owner;
                    }

                    // this tap answers to the owner now: a bar with another tap build wakes this one rather than attach its own
                    const auto claimCurrent = [&currentTag, &self]() {
                        if (currentTag != nullptr)
                        {
                            wcsncpy_s(currentTag, TagChars, self->m_tag.c_str(), _TRUNCATE);
                        }
                    };
                    claimCurrent();

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
                            // a new IsleBar run: watch it instead, and hide again unless it asked for "show" in the meantime.
                            // Owner 0 (no bar): restore and wait.
                            const DWORD pid = ownerPid != nullptr ? *ownerPid : 0;
                            owner.attach(pid != 0 ? OpenProcess(SYNCHRONIZE, FALSE, pid) : nullptr);
                            if (owner)
                            {
                                claimCurrent();
                            }

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

                if (currentTag != nullptr)
                {
                    UnmapViewOfFile(currentTag);
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
        std::wstring m_tag;      // this tap build, part of its object names (set in SetSite before the watcher starts)
        std::mutex m_gate;
        bool m_hiding = false;   // hidden only while the watcher holds a live owner
        std::vector<winrt::weak_ref<winrt::Windows::UI::Xaml::UIElement>> m_found;   // main-taskbar (or unknown-host) boxes
        std::vector<InstanceHandle> m_sources;   // XAML islands seen in the tree (one per taskbar, a few others)
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
