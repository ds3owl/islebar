using IsleBar.Core.SystemWatch;
using Windows.Foundation.Metadata;
using Windows.UI.Shell;

namespace IsleBar.App.SystemWatch;

/// <summary>
/// Shows "Focus session" (moon icon) while a Windows focus session (Clock app / notification center "Focus") is on. Removes it when off.
/// <see cref="FocusSessionManager"/> exists from Windows 11 22H2, so on earlier versions it quietly does nothing.
/// It's a state, so it shows even if focus was already on at startup. Re-reads once every 5 s in case events don't arrive.
/// Whether this API works in an unpackaged (non-MSIX) app needs checking on PC — if not, the exception is swallowed and it stays off.
/// </summary>
internal sealed class FocusWatcher : NoticeWatcher
{
    private const string Key = "focus";
    private const string SettingsUri = "ms-settings:quiethours";
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private readonly object _gate = new();
    private FocusSessionManager? _manager;
    private Timer? _poll;
    private DateTimeOffset? _since;

    public FocusWatcher(Func<Core.Localization.LanguageStrings> strings)
        : base(strings)
    {
    }

    public override void Start() => Guard(() =>
    {
        lock (_gate)
        {
            if (_manager is not null
                || !ApiInformation.IsTypePresent("Windows.UI.Shell.FocusSessionManager")
                || !FocusSessionManager.IsSupported)
            {
                return;
            }

            _manager = FocusSessionManager.GetDefault();
            _manager.IsFocusActiveChanged += OnChanged;
        }

        _poll = new Timer(_ => Guard(Refresh), null, TimeSpan.Zero, PollInterval);
    });

    protected override void Stop()
    {
        lock (_gate)
        {
            if (_manager is not null)
            {
                _manager.IsFocusActiveChanged -= OnChanged;
                _manager = null;
            }
        }

        _poll?.Dispose();
    }

    private void OnChanged(FocusSessionManager sender, object args) => Guard(Refresh);

    private void Refresh()
    {
        lock (_gate)
        {
            if (_manager is null)
            {
                return;
            }

            if (!_manager.IsFocusActive)
            {
                _since = null;
                Board.Clear(Key);
                return;
            }

            _since ??= DateTimeOffset.UtcNow;
            Board.Hold(Key, SystemNotice.Make(Text.NoticeFocus, NoticeGlyphs.Focus, _since.Value, open: SettingsUri));
        }
    }
}
