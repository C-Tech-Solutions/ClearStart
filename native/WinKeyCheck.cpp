// Checks the rules in WinKey.h on made-up key presses. Nothing is typed and no hook is installed. Built beside the
// helper, never shipped.
#include "WinKey.h"
#include <cstdio>

static int failures = 0;

static void Expect(bool ok, const char* what) {
    std::printf("%s: %s\n", ok ? "ok  " : "FAIL", what);
    if (!ok) ++failures;
}

int main() {
    const bool up = false, down = true;
    {
        WinKeyWatch watch;
        Expect(watch.Key(VK_LWIN, down, false, false, false) == KeyAction::Pass, "Win down: nothing yet");
        Expect(watch.Key(VK_LWIN, up, false, false, false) == KeyAction::Tap, "Win up: a tap");
        Expect(watch.Key(VK_RWIN, down, false, false, false) == KeyAction::Pass, "right Win down: nothing yet");
        Expect(watch.Key(VK_RWIN, up, false, false, false) == KeyAction::Tap, "right Win up: a tap");
    }
    {
        WinKeyWatch watch;
        watch.Key(VK_LWIN, down, false, false, false);
        watch.Key(VK_LWIN, down, false, false, false);
        watch.Key(VK_LWIN, down, false, false, false);
        Expect(watch.Key(VK_LWIN, up, false, false, false) == KeyAction::Tap, "Win held until it repeats: still one tap");
    }
    {
        WinKeyWatch watch;
        watch.Key(VK_LWIN, down, false, false, false);
        Expect(watch.Key('E', down, false, false, false) == KeyAction::Pass, "Win+E: the E goes through");
        watch.Key('E', up, false, false, false);
        Expect(watch.Key(VK_LWIN, up, false, false, false) == KeyAction::Pass, "Win+E: no tap");
        watch.Key(VK_LWIN, down, false, false, false);
        Expect(watch.Key(VK_LWIN, up, false, false, false) == KeyAction::Tap, "a tap after Win+E");
    }
    {
        WinKeyWatch watch;
        watch.Key(VK_LWIN, down, true, false, false);
        Expect(watch.Key(VK_LWIN, up, false, false, false) == KeyAction::Pass, "Ctrl held as Win goes down: no tap");
        watch.Key(VK_LWIN, down, false, true, false);
        Expect(watch.Key(VK_LWIN, up, false, true, false) == KeyAction::Pass, "Shift+Win: no tap");
        watch.Key(VK_LWIN, down, false, false, true);
        Expect(watch.Key(VK_LWIN, up, false, false, true) == KeyAction::Pass, "Alt+Win: no tap");
    }
    {
        WinKeyWatch watch;
        Expect(watch.Key(VK_LWIN, up, false, false, false) == KeyAction::Pass, "Win up without its down: no tap");
    }
    {
        WinKeyWatch watch;
        Expect(watch.Key(VK_ESCAPE, down, true, false, false) == KeyAction::TapAndSwallow, "Ctrl+Esc: a tap, and the Esc is kept");
        Expect(watch.Key(VK_ESCAPE, up, true, false, false) == KeyAction::Pass, "Ctrl+Esc: the Esc going up is let through");
        Expect(watch.Key(VK_ESCAPE, down, true, true, false) == KeyAction::Pass, "Ctrl+Shift+Esc: left alone");
        Expect(watch.Key(VK_ESCAPE, down, true, false, true) == KeyAction::Pass, "Ctrl+Alt+Esc: left alone");
        Expect(watch.Key(VK_ESCAPE, down, false, false, false) == KeyAction::Pass, "Esc alone: left alone");
        watch.Key(VK_LWIN, down, false, false, false);
        Expect(watch.Key(VK_ESCAPE, down, true, false, false) == KeyAction::Pass, "Esc while Win is down: a shortcut, not Ctrl+Esc");
    }
    std::printf(failures ? "FAILED: %d\n" : "PASS\n", failures);
    return failures;
}
