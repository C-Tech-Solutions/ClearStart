// Which key presses mean "open the start menu": the same rules as src/Interop/WinKeyHook.cs, kept apart from the hook
// so that they can be checked on made-up key presses (WinKeyCheck.cpp).
#pragma once
#include <windows.h>

enum class KeyAction {
    Pass,          // nothing to do with the start menu
    Tap,           // the Windows key alone was let go: open the menu, and let the key itself through
    TapAndSwallow, // Ctrl+Esc: open the menu, and keep the Esc from everything else
};

struct WinKeyWatch {
    bool winDown = false;
    // Another key was pressed, or Ctrl, Shift or Alt was held, while the Windows key was down: a shortcut, not a tap.
    bool chord = false;

    // One key going down or up, as the keyboard sent it (never a key another program typed). control, shift and alt
    // say which of those are held at that moment.
    KeyAction Key(DWORD key, bool down, bool control, bool shift, bool alt) {
        if (key == VK_LWIN || key == VK_RWIN) {
            if (down) {
                // A held key repeats: only the first press counts.
                if (!winDown) {
                    winDown = true;
                    chord = control || shift || alt;
                }
                return KeyAction::Pass;
            }
            const bool tap = winDown && !chord;
            winDown = false;
            return tap ? KeyAction::Tap : KeyAction::Pass;
        }
        if (!down) return KeyAction::Pass;
        if (winDown) {
            chord = true;
            return KeyAction::Pass;
        }
        return key == VK_ESCAPE && control && !shift && !alt ? KeyAction::TapAndSwallow : KeyAction::Pass;
    }
};
