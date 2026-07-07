using System;
using System.Collections.Generic;
using System.Text;
using System.Windows.Input;

namespace RoeSnip.Overlay;

using TextBox = System.Windows.Controls.TextBox;

/// <summary>The guaranteed fallback tier (item 3b of UX round 3) for typing into an in-progress
/// text annotation: SessionKeyboardHook routes every relevant keydown here whenever a text edit is
/// active AND the overlay window does not currently hold real OS foreground/focus (every tier of
/// ForegroundActivator failed) — in that state nothing typed would ever reach the TextBox through
/// the normal WPF/IME input pipeline, so this applies the keystroke directly against the TextBox's
/// Text/CaretIndex/Selection instead. Only ever consulted from that one fallback state; whenever
/// the window truly has focus, keys reach the TextBox the ordinary way, which supports far more
/// than this class attempts to (IME composition/candidate windows, dead-key sequences, etc.).
///
/// Printable characters are translated via ToUnicodeEx against a *synthetic* keyboard-state array
/// built purely from the Shift/CapsLock flags SessionKeyboardHook already reads off the low-level
/// hook struct (see <see cref="BuildSyntheticKeyState"/>) rather than the real GetKeyboardState
/// (whose accuracy for a thread that isn't actually processing the message queue that owns
/// keyboard focus is unreliable in exactly the scenario this fallback exists for) — deterministic
/// and independently testable without any live keyboard state.
///
/// Public (rather than internal) purely so its pure classification/translation-support helpers are
/// unit-testable without an InternalsVisibleTo edit, matching the rest of Overlay/*'s pure-helper
/// convention (see BoundedColorList) — only SessionKeyboardHook calls <see cref="Forward"/> in
/// practice.</summary>
public static class TextEditKeyForwarder
{
    /// <summary>Keys this fallback never treats as a text-insertion candidate — pure modifiers,
    /// function/system keys, and every key it already special-cases in <see cref="Apply"/>
    /// (Enter/Escape/Backspace/Delete/arrows/Home/End) so they can never also fall through into
    /// ToUnicodeEx translation.</summary>
    public static bool IsPrintableCandidate(Key key)
    {
        if (key is >= Key.F1 and <= Key.F24)
        {
            return false;
        }
        return !NonCandidateKeys.Contains(key);
    }

    private static readonly HashSet<Key> NonCandidateKeys = new()
    {
        Key.None, Key.Enter, Key.Escape, Key.Tab, Key.Back, Key.Delete,
        Key.Left, Key.Right, Key.Up, Key.Down, Key.Home, Key.End, Key.PageUp, Key.PageDown,
        Key.Insert, Key.PrintScreen, Key.Pause, Key.CapsLock, Key.NumLock, Key.Scroll,
        Key.LeftShift, Key.RightShift, Key.LeftCtrl, Key.RightCtrl, Key.LeftAlt, Key.RightAlt,
        Key.LWin, Key.RWin, Key.Apps, Key.Sleep,
    };

    /// <summary>Builds the 256-byte virtual-key-state array ToUnicodeEx expects, from just the two
    /// modifier flags that actually change what a key produces within this fallback's supported
    /// range (Shift and Caps Lock — Ctrl+&lt;letter&gt; combos are explicitly out of scope, see
    /// <see cref="Apply"/>). Pure and deterministic, independent of any live OS keyboard state.</summary>
    public static byte[] BuildSyntheticKeyState(bool shiftDown, bool capsLockOn)
    {
        var state = new byte[256];
        if (shiftDown)
        {
            state[OverlayInputInterop.VK_SHIFT] = 0x80; // high bit = currently down
        }
        if (capsLockOn)
        {
            state[OverlayInputInterop.VK_CAPITAL] = 0x01; // low bit = toggled on
        }
        return state;
    }

    /// <summary>Entry point called from SessionKeyboardHook's hook callback — marshals to the UI
    /// thread's dispatcher (mirroring the rest of SessionKeyboardHook's dispatching convention; this
    /// runs on the low-level hook's own call stack) and applies the key directly against the active
    /// text editor. A no-op if the edit has already ended by the time the dispatched call runs.</summary>
    public static void Forward(OverlayWindow window, Key key, uint vkCode, uint scanCode, bool shift, bool capsLockOn, bool ctrl)
    {
        window.Dispatcher.BeginInvoke(new Action(() => Apply(window, key, vkCode, scanCode, shift, capsLockOn, ctrl)));
    }

    private static void Apply(OverlayWindow window, Key key, uint vkCode, uint scanCode, bool shift, bool capsLockOn, bool ctrl)
    {
        if (window.ActiveTextEditor is not { } editor)
        {
            return; // edit already committed/cancelled before this dispatched call ran
        }

        switch (key)
        {
            case Key.Escape:
                window.ProcessKeyCommand(Key.Escape, ModifierKeys.None);
                return;
            case Key.Enter:
                window.ProcessKeyCommand(Key.Enter, ModifierKeys.None);
                return;
            case Key.Back:
                RemoveBackward(editor);
                return;
            case Key.Delete:
                RemoveForward(editor);
                return;
            case Key.Left:
                SetCaret(editor, editor.CaretIndex - 1);
                return;
            case Key.Right:
                SetCaret(editor, editor.CaretIndex + 1);
                return;
            case Key.Home:
                SetCaret(editor, 0);
                return;
            case Key.End:
                SetCaret(editor, editor.Text.Length);
                return;
        }

        if (ctrl || !IsPrintableCandidate(key))
        {
            return; // Ctrl+<letter> combos and anything else unclassified aren't this fallback's job
        }

        string? text = Translate(vkCode, scanCode, shift, capsLockOn);
        if (!string.IsNullOrEmpty(text))
        {
            InsertText(editor, text);
        }
    }

    private static string? Translate(uint vkCode, uint scanCode, bool shift, bool capsLockOn)
    {
        byte[] keyState = BuildSyntheticKeyState(shift, capsLockOn);
        var buffer = new StringBuilder(8);
        int result = OverlayInputInterop.ToUnicodeEx(
            vkCode, scanCode, keyState, buffer, buffer.Capacity, 0, OverlayInputInterop.GetKeyboardLayout(0));
        return result > 0 ? buffer.ToString(0, result) : null;
    }

    private static void InsertText(TextBox editor, string text)
    {
        if (editor.SelectionLength > 0)
        {
            int start = editor.SelectionStart;
            editor.SelectedText = text;
            editor.CaretIndex = start + text.Length;
            editor.SelectionLength = 0;
        }
        else
        {
            int idx = editor.CaretIndex;
            editor.Text = editor.Text.Insert(idx, text);
            editor.CaretIndex = idx + text.Length;
        }
    }

    private static void RemoveBackward(TextBox editor)
    {
        if (editor.SelectionLength > 0)
        {
            RemoveSelection(editor);
            return;
        }
        if (editor.CaretIndex > 0)
        {
            int idx = editor.CaretIndex;
            editor.Text = editor.Text.Remove(idx - 1, 1);
            editor.CaretIndex = idx - 1;
        }
    }

    private static void RemoveForward(TextBox editor)
    {
        if (editor.SelectionLength > 0)
        {
            RemoveSelection(editor);
            return;
        }
        if (editor.CaretIndex < editor.Text.Length)
        {
            editor.Text = editor.Text.Remove(editor.CaretIndex, 1);
        }
    }

    private static void RemoveSelection(TextBox editor)
    {
        int start = editor.SelectionStart;
        editor.SelectedText = string.Empty;
        editor.CaretIndex = start;
        editor.SelectionLength = 0;
    }

    private static void SetCaret(TextBox editor, int index)
    {
        editor.CaretIndex = Math.Clamp(index, 0, editor.Text.Length);
        editor.SelectionLength = 0;
    }
}
