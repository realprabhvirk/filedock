using System.Diagnostics.CodeAnalysis;
using System.Windows.Input;
using FileDock.Native;

namespace FileDock.Services;

/// <summary>A keyboard shortcut such as Ctrl+Alt+Space, with parsing and display formatting.</summary>
public readonly record struct Shortcut(ModifierKeys Modifiers, Key Key)
{
    public override string ToString()
    {
        string modifiers = FormatModifiers(Modifiers);
        return modifiers.Length == 0 ? KeyName(Key) : modifiers + "+" + KeyName(Key);
    }

    public static string FormatModifiers(ModifierKeys modifiers)
    {
        // Windows documentation order: Win, Ctrl, Alt, Shift.
        var parts = new List<string>(4);
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        return string.Join("+", parts);
    }

    /// <summary>
    /// A usable global shortcut needs Ctrl, Alt or Win (Shift alone would capture ordinary typing)
    /// plus one non-modifier key.
    /// </summary>
    public bool IsValid =>
        (Modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != 0 && !IsModifierKey(Key) && Key != Key.None;

    public static bool TryParse(string? text, [NotNullWhen(true)] out Shortcut? shortcut)
    {
        shortcut = null;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        ModifierKeys modifiers = ModifierKeys.None;
        Key key = Key.None;
        foreach (string raw in text.Split('+'))
        {
            string part = raw.Trim();
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= ModifierKeys.Control; continue;
                case "alt": modifiers |= ModifierKeys.Alt; continue;
                case "shift": modifiers |= ModifierKeys.Shift; continue;
                case "win" or "windows": modifiers |= ModifierKeys.Windows; continue;
            }
            if (key != Key.None || !TryParseKey(part, out key))
                return false;
        }

        var result = new Shortcut(modifiers, key);
        if (!result.IsValid)
            return false;
        shortcut = result;
        return true;
    }

    public static bool IsModifierKey(Key key) => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
        or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System;

    private static string KeyName(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        Key.Return => "Enter",
        Key.Back => "Backspace",
        Key.Next => "PageDown",
        Key.Prior => "PageUp",
        _ => key.ToString(),
    };

    private static bool TryParseKey(string text, out Key key)
    {
        key = Key.None;
        if (text.Length == 1 && char.IsAsciiDigit(text[0]))
        {
            key = Key.D0 + (text[0] - '0');
            return true;
        }
        return Enum.TryParse(text, ignoreCase: true, out key) && Enum.IsDefined(key) && key != Key.None;
    }
}

/// <summary>
/// The single global show/hide shortcut, registered with RegisterHotKey on the dock window.
/// No other keyboard input is observed outside FileDock.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const int HotkeyId = 0xB0D1;
    private IntPtr _hwnd;
    private bool _registered;

    public event Action? Pressed;

    public Shortcut? Active { get; private set; }

    public void Attach(IntPtr hwnd) => _hwnd = hwnd;

    /// <summary>Registers the shortcut, replacing any previous one. Returns a user-facing error on failure.</summary>
    public bool TryRegister(Shortcut shortcut, out string? error)
    {
        Unregister();
        error = null;
        if (_hwnd == IntPtr.Zero)
        {
            error = "FileDock is not ready to register shortcuts yet.";
            return false;
        }

        uint modifiers = Win32.MOD_NOREPEAT;
        if (shortcut.Modifiers.HasFlag(ModifierKeys.Control)) modifiers |= Win32.MOD_CONTROL;
        if (shortcut.Modifiers.HasFlag(ModifierKeys.Alt)) modifiers |= Win32.MOD_ALT;
        if (shortcut.Modifiers.HasFlag(ModifierKeys.Shift)) modifiers |= Win32.MOD_SHIFT;
        if (shortcut.Modifiers.HasFlag(ModifierKeys.Windows)) modifiers |= Win32.MOD_WIN;
        uint vk = (uint)KeyInterop.VirtualKeyFromKey(shortcut.Key);

        if (!Win32.RegisterHotKey(_hwnd, HotkeyId, modifiers, vk))
        {
            int code = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            Log.Warn($"RegisterHotKey failed for {shortcut} (error {code})");
            error = $"{shortcut} is already used by another app. Choose a different shortcut.";
            return false;
        }

        _registered = true;
        Active = shortcut;
        Log.Info($"Registered shortcut {shortcut}");
        return true;
    }

    public void Unregister()
    {
        if (_registered)
            Win32.UnregisterHotKey(_hwnd, HotkeyId);
        _registered = false;
        Active = null;
    }

    /// <summary>Called from the dock window's message hook.</summary>
    public bool HandleMessage(int msg, IntPtr wParam)
    {
        if (msg != Win32.WM_HOTKEY || wParam.ToInt32() != HotkeyId)
            return false;
        Pressed?.Invoke();
        return true;
    }

    public void Dispose() => Unregister();
}
