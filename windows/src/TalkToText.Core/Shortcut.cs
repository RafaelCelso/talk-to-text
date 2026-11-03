using System.Globalization;
using System.Text.Json.Serialization;
using TalkToText.Abstractions;

namespace TalkToText.Core;

/// <summary>How the shortcut drives recording.</summary>
public enum ActivationMode
{
    /// <summary>Record while the shortcut is held; stop on release.</summary>
    Hold,

    /// <summary>Press once to start, press again to stop.</summary>
    Toggle,
}

/// <summary>
/// One or two keys that start dictation, as Windows virtual-key codes.
/// </summary>
/// <remarks>
/// <para>
/// The generic modifier codes — <see cref="Keys.Shift"/>, <see cref="Keys.Control"/>,
/// <see cref="Keys.Alt"/> and <see cref="Keys.Windows"/> — match either side of the keyboard.
/// Side-specific codes such as <see cref="Keys.RightControl"/> match only that key, which is
/// what a single-key shortcut wants: Right Ctrl alone leaves Left Ctrl free for Ctrl+C.
/// </para>
/// <para>
/// Platform-neutral on purpose. The codes are just numbers here; only the Windows hook knows
/// what produces them, so everything that interprets them is testable anywhere.
/// </para>
/// </remarks>
/// <param name="First">The first key.</param>
/// <param name="Second">The second key, or 0 for a single-key shortcut.</param>
public readonly record struct Shortcut(int First, int Second = 0)
{
    /// <summary>Ctrl + Win: types nothing and collides with no common shortcut.</summary>
    public static Shortcut Default { get; } = new(Keys.Control, Keys.Windows);

    /// <summary>The keys, in order, without the empty slot.</summary>
    [JsonIgnore]
    public IReadOnlyList<int> KeyList => Second == 0 ? [First] : [First, Second];

    /// <summary>Whether this shortcut includes a key that is generic for <paramref name="key"/>.</summary>
    public bool Contains(int key) => KeyList.Any(k => Matches(k, key));

    /// <summary>
    /// Whether releasing this shortcut could trigger a Windows action of its own — Win opens
    /// the Start menu and Alt focuses the menu bar when released untouched.
    /// </summary>
    [JsonIgnore]
    public bool NeedsReleaseMask => KeyList.Any(k => Matches(Keys.Windows, k) || Matches(Keys.Alt, k));

    /// <summary>Readable form, e.g. "Ctrl + Win".</summary>
    public override string ToString() => string.Join(" + ", KeyList.Select(Keys.Name));

    /// <summary>
    /// Whether a physical key event for <paramref name="pressed"/> satisfies
    /// <paramref name="shortcutKey"/>.
    /// </summary>
    public static bool Matches(int shortcutKey, int pressed) => shortcutKey == pressed || shortcutKey switch
    {
        Keys.Shift => pressed is Keys.LeftShift or Keys.RightShift,
        Keys.Control => pressed is Keys.LeftControl or Keys.RightControl,
        Keys.Alt => pressed is Keys.LeftAlt or Keys.RightAlt,
        Keys.Windows => pressed is Keys.RightWindows,
        _ => false,
    };
}

/// <summary>Virtual-key codes the app names or treats specially.</summary>
public static class Keys
{
    /// <summary>Either Shift.</summary>
    public const int Shift = 0x10;

    /// <summary>Either Ctrl.</summary>
    public const int Control = 0x11;

    /// <summary>Either Alt.</summary>
    public const int Alt = 0x12;

    /// <summary>Either Windows key. Shares its code with the left key.</summary>
    public const int Windows = 0x5B;

    /// <summary>Right Windows key.</summary>
    public const int RightWindows = 0x5C;

    /// <summary>Left Shift.</summary>
    public const int LeftShift = 0xA0;

    /// <summary>Right Shift.</summary>
    public const int RightShift = 0xA1;

    /// <summary>Left Ctrl.</summary>
    public const int LeftControl = 0xA2;

    /// <summary>Right Ctrl.</summary>
    public const int RightControl = 0xA3;

    /// <summary>Left Alt.</summary>
    public const int LeftAlt = 0xA4;

    /// <summary>Right Alt (AltGr on many layouts).</summary>
    public const int RightAlt = 0xA5;

    /// <summary>Escape — cancels recording a new shortcut.</summary>
    public const int Escape = 0x1B;

    /// <summary>Whether <paramref name="key"/> is Shift, Ctrl, Alt or Win on either side.</summary>
    public static bool IsModifier(int key) =>
        Shortcut.Matches(Shift, key) || Shortcut.Matches(Control, key)
        || Shortcut.Matches(Alt, key) || Shortcut.Matches(Windows, key);

    /// <summary>The side-agnostic code for a modifier; any other key unchanged.</summary>
    public static int Generic(int key) =>
        Shortcut.Matches(Shift, key) ? Shift
        : Shortcut.Matches(Control, key) ? Control
        : Shortcut.Matches(Alt, key) ? Alt
        : Shortcut.Matches(Windows, key) ? Windows
        : key;

    /// <summary>A short display name, in Portuguese.</summary>
    public static string Name(int key) => key switch
    {
        Shift or LeftShift => "Shift",
        RightShift => "Shift direito",
        Control or LeftControl => "Ctrl",
        RightControl => "Ctrl direito",
        Alt or LeftAlt => "Alt",
        RightAlt => "Alt direito",
        Windows or RightWindows => "Win",
        0x14 => "Caps Lock",
        0x20 => "Espaço",
        0x09 => "Tab",
        0x0D => "Enter",
        0x08 => "Backspace",
        Escape => "Esc",
        >= 0x70 and <= 0x87 => "F" + (key - 0x6F).ToString(CultureInfo.InvariantCulture),
        >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A => ((char)key).ToString(),
        _ => "Tecla 0x" + key.ToString("X2", CultureInfo.InvariantCulture),
    };
}

/// <summary>
/// Turns a stream of physical key transitions into shortcut presses and releases.
/// </summary>
/// <remarks>
/// <para>
/// Fires only when <i>exactly</i> the shortcut's keys are down: holding Shift and then
/// pressing Ctrl + Win is a different chord and must not start dictation.
/// </para>
/// <para>
/// A key that is not part of the shortcut, pressed while it is held, raises
/// <see cref="Interrupted"/>: the user was reaching for a longer Windows shortcut such as
/// Ctrl + Win + D (new virtual desktop), and whatever was recorded should be thrown away.
/// </para>
/// </remarks>
public sealed class ShortcutDetector
{
    private readonly HashSet<int> _down = [];
    private bool _active;
    private bool _interrupted;

    /// <summary>The shortcut being watched for.</summary>
    public Shortcut Shortcut { get; set; } = Shortcut.Default;

    /// <summary>All of the shortcut's keys just became held.</summary>
    public event EventHandler? Activated;

    /// <summary>One of the shortcut's keys was released after <see cref="Activated"/>.</summary>
    public event EventHandler? Deactivated;

    /// <summary>Another key was pressed while the shortcut was held.</summary>
    public event EventHandler? Interrupted;

    /// <summary>Feeds one physical key transition.</summary>
    public void Handle(KeyTransition transition)
    {
        var key = transition.VirtualKey;

        if (transition.IsDown)
        {
            // Windows re-sends key-down while a key is held; only the first counts.
            if (!_down.Add(key)) return;

            if (_active && !Shortcut.Contains(key))
            {
                _active = false;
                _interrupted = true;
                Interrupted?.Invoke(this, EventArgs.Empty);
                return;
            }

            if (!_active && !_interrupted && IsExactlyHeld())
            {
                _active = true;
                Activated?.Invoke(this, EventArgs.Empty);
            }

            return;
        }

        _down.Remove(key);

        if (_active && Shortcut.Contains(key))
        {
            _active = false;
            Deactivated?.Invoke(this, EventArgs.Empty);
        }

        // An interrupted chord stays dead until every key is up, so letting go of the extra
        // key does not re-arm it mid-gesture.
        if (_down.Count == 0) _interrupted = false;
    }

    /// <summary>Forgets every held key — after the hook restarts, its state is unknown.</summary>
    public void Reset()
    {
        _down.Clear();
        _active = false;
        _interrupted = false;
    }

    private bool IsExactlyHeld() =>
        Shortcut.KeyList.All(k => _down.Any(d => Shortcut.Matches(k, d)))
        && _down.All(Shortcut.Contains);
}

/// <summary>
/// Captures a new shortcut from what the user presses: up to two keys, finished when all of
/// them are released.
/// </summary>
/// <remarks>
/// A two-key shortcut records its modifiers side-agnostically — "Ctrl + Win" should work
/// with either Ctrl. A single key keeps its side, because Right Ctrl alone is useful
/// precisely because it leaves Left Ctrl alone.
/// </remarks>
public sealed class ShortcutRecorder
{
    private readonly List<int> _keys = [];
    private readonly HashSet<int> _down = [];

    /// <summary>Raised once the keys are released, with the shortcut they form.</summary>
    public event EventHandler<Shortcut>? Completed;

    /// <summary>Raised when Escape is pressed first.</summary>
    public event EventHandler? Cancelled;

    /// <summary>Keys pressed so far, for showing progress while recording.</summary>
    public IReadOnlyList<int> Pressed => _keys;

    /// <summary>Feeds one physical key transition.</summary>
    public void Handle(KeyTransition transition)
    {
        var key = transition.VirtualKey;

        if (transition.IsDown)
        {
            if (!_down.Add(key)) return;

            if (_keys.Count == 0 && key == Keys.Escape)
            {
                Cancelled?.Invoke(this, EventArgs.Empty);
                return;
            }

            if (_keys.Count < 2 && !_keys.Contains(key)) _keys.Add(key);
            return;
        }

        if (!_down.Remove(key) || _down.Count > 0 || _keys.Count == 0) return;

        // Left Ctrl + Right Ctrl would collapse to "Ctrl + Ctrl", which one Ctrl satisfies.
        var shortcut = _keys.Count == 1
            ? new Shortcut(_keys[0])
            : Keys.Generic(_keys[0]) == Keys.Generic(_keys[1])
                ? new Shortcut(_keys[0], _keys[1])
                : new Shortcut(Keys.Generic(_keys[0]), Keys.Generic(_keys[1]));

        _keys.Clear();
        Completed?.Invoke(this, shortcut);
    }
}
