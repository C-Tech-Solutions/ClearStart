# Updates

ClearStart can tell you when a newer version is out, and install it for you. This page says what it
checks, what it sends, how it makes sure a download is genuine, and how to turn it off.

- [Checking](#checking)
- [When there is a newer version](#when-there-is-a-newer-version)
- [Installing](#installing)
- [Which copies update themselves](#which-copies-update-themselves)
- [Turning it off](#turning-it-off)

## Checking

A couple of minutes after ClearStart starts, and then about once a day, it asks GitHub for the latest
release of ClearStart. That is one request to GitHub's public API:

```
https://api.github.com/repos/C-Tech-Solutions/ClearStart/releases/latest
```

It carries ClearStart's version (`ClearStart/0.1.0`) as its user agent and nothing else: no
account, no token, and nothing about you or the PC. Like any website, GitHub sees the address the
request comes from. If GitHub cannot be reached, ClearStart tries again an hour later; the settings
page and the log say so, and nothing pops up.

Only a finished release is offered: never a draft or a pre-release, and never the version you
already have or an older one.

## When there is a newer version

A download button with a blue dot appears on the main bar, next to the weather. Rest the mouse on
it to see the version; click it to install it or to read what is new. The **General** page of the
settings says the same, with **Install**, **What's new** and **Check now** buttons.

Nothing is downloaded until you ask, unless **Install updates automatically** is on.

## Installing

1. ClearStart downloads the new version's setup program, `ClearStart-Setup-<version>.exe`, and the
   checksum file that comes with it, from the release on GitHub into
   `%LOCALAPPDATA%\ClearStart\updates`.
2. It checks that the setup program's SHA-256 is the one the checksum file gives, and that the
   program carries a valid signature from **C-Tech Solutions LLC**, the same company whose
   signature is on the copy of ClearStart that is running. If either check fails, the download is
   deleted and nothing runs. The settings page says why.
3. It runs the setup program without any windows. Setup closes ClearStart (the Windows taskbar
   shows for a moment), puts the new version in place, and starts ClearStart again the way it was
   running, live or as a preview. **Start with Windows** stays as it was.

If Setup stops without updating, the old version keeps running, or Setup starts it again, and the
settings page says so. Setup's log is `setup.log` in the same `updates` folder. ClearStart does not
install that version again by itself; **Install** tries again.

**Install updates automatically** is off unless you turn it on. When it is on, ClearStart downloads
a newer version as soon as an automatic check finds one and installs it once nobody has used the PC
for ten minutes. It works together with **Check for updates automatically**: with that off, nothing
is downloaded until you click **Install**.

## Which copies update themselves

Only the copy that Setup installed, in `%LOCALAPPDATA%\Programs\ClearStart`, updates itself, and
only when that copy is itself signed by C-Tech Solutions LLC. A copy you built from source, or
installed with an installer you built without signing it, still checks, and still says when a newer version is out, but its **Download** button opens the release
page on GitHub instead of installing anything.

## Turning it off

Switch off **Check for updates automatically** on the settings' **General** page, or set
`updates.checkAutomatically` to `false` in the [settings file](settings-file.md#updates). Then
ClearStart sends nothing to GitHub unless you click **Check now**.
