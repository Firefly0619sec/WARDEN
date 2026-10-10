# WARDEN — a popup and window behaviour blocker for Windows

> 中文版：[README.md](README.md)　·　License: **AGPL-3.0-or-later**

WARDEN watches the windows that appear on your desktop and closes the ones that
misbehave. It is **not** an ad-signature list and it does **not** rely on window
titles alone — it scores **behaviour**: a window that pops up dozens of times in
seconds, repeatedly steals focus, has no input box, has no child controls, is a
small floating panel, churns its title, or comes back after being closed.

The result: annoying popups go away, and ordinary windows are left alone.

---

## Why it exists

Most "popup blockers" are either

* a fixed list of process names that goes stale in weeks, or
* a title-keyword matcher that any vendor can defeat by renaming a button.

Behaviour is much harder to fake without giving up the thing the popup exists
for (stealing your attention), so WARDEN scores behaviour and only acts above a
threshold you control.

## How it behaves

| Situation | What WARDEN does |
| --- | --- |
| Suspicious window | Politely asks it to close |
| It ignores that | Hides it from the screen |
| It keeps coming back | Force-kills it (only if you allow force actions) |
| The program has a valid code signature | **Never** force-killed by default — hidden or asked to close, at most |
| It is on your whitelist | Never touched at all |
| Something tries to close **WARDEN** | Emergency suppression mode, plus a full-screen warning and an evidence export |
| You are not sure yet | **Drill mode**: log everything, act on nothing |

### Safety design (this part matters)

* WARDEN is **completely offline**. Nothing is collected, nothing is uploaded.
  Only the updater touches the network, and only when *you* click *Check for
  updates*.
* `allow_terminate` is **off** by default. Force-killing is opt-in.
* Windows and processes with a valid signature, or on your whitelist, are never
  hard-killed by default.
* Three anti-collateral switches are **on** by default: never touch the focused
  window, skip windows that contain input boxes, skip windows that contain child
  controls.
* Force actions are **not pre-authorised**: when it meets a window it cannot
  suppress, it asks you, and acts once with your live consent — that is what we
  call *manual elevation*.
* Everything it does is written to a log. Evidence (what it was attacked with,
  when, by which process) is kept on your machine.

## Install

1. Download `WARDEN-Setup.exe`.
2. Run it. It installs to `%LOCALAPPDATA%\Programs\WARDEN` — **no administrator
   rights needed**.
3. That is all. No account, no registration, no runtime to install first.

Verify the download against the SHA256 published with the release, and check the
digital signature (once code signing is in place) before running anything you got
from a third party.

## First run

WARDEN asks exactly one question the first time it starts:

> Watch for a few days first (drill mode), or start blocking right away?

Drill mode only writes to the log and never touches a window — a good way to see
what it *would* block before you trust it.

## Tray icon

Right-click the tray icon:

| Item | Meaning |
| --- | --- |
| Open settings | Rules, thresholds, whitelists |
| Pause / Resume protection | Let it rest for a while (the icon turns amber) |
| Reload settings | Re-read the settings file, no restart needed |
| View logs | Opens the log viewer, which summarises and exports a report |
| Check for updates | Contacts the release page, once |
| Uninstall WARDEN | Removes the program; your settings and logs are kept by default |
| Quit | Shut it down completely |

The icon colour tells you the state at a glance: normal = guarding, amber =
paused, red = emergency suppression.

## Settings

The settings window has seven tabs: **Rules / Protection / Actions / Lists /
Safety / Log / Language**.

* Rules — process name, title keywords or a title regex. A rule is only a
  starting point; the behaviour score still decides.
* Protection — the anti-collateral switches and drill mode.
* Actions — thresholds, escalation, and whether force actions are allowed.
* Lists — whitelist, permanently allowed force-kills, force-kill history.
* Safety — self-check on start, expected own hash, the immutable hard blocklist.
* Log — tail the log without leaving the window.
* Language — switch the interface language.

Rules can be **imported and exported** as JSON, so you can back them up or share
them.

## Languages

The interface ships in six languages, and each Traditional Chinese variant has
its own wording (they are not the same language with a character conversion):

| Code | Name shown in the app |
| --- | --- |
| `zh-Hans` | 简体中文 |
| `en-GB` | English (UK) |
| `en-US` | English (US) |
| `zh-TW` | 繁體中文（中國臺灣） |
| `zh-HK` | 繁體中文（中國香港） |
| `zh-MO` | 繁體中文（中國澳門） |

Language packs are plain text (`lang/*.txt`), so you can edit the wording in
Notepad or add a new language without recompiling. See
[docs/语言包.md](docs/语言包.md).

## Tools that come with it

| Tool | Purpose |
| --- | --- |
| `WARDEN-Settings.exe` | The settings window |
| `WARDEN-LogView.exe` | Log viewer with a one-click Markdown report |
| `WARDEN-Updater.exe` | Applies an offline update package, verifying its hash and saving a rollback backup |
| `WARDEN-Uninstaller.exe` | Uninstall; keeps your settings and logs unless you tick the box |
| `WARDEN-Help.exe` | Offline help and the feedback form |
| `warden-cli.exe` | `status` / `tail` / `stop` / `rules` / `report` for scripts and remote troubleshooting |
| `WARDEN-RedTeam.exe` | Optional component: opens a batch of "unclosable" windows so you can watch WARDEN work |

## Command line

```
WARDEN.exe --help      show options
WARDEN.exe --version   version
```

## Portable mode

Put `WARDEN.portable` next to `WARDEN.exe` and all data (settings, logs,
evidence) is written to `data\` next to the executable instead of
`%LOCALAPPDATA%\WARDEN` — handy for a USB stick.

## Licence

**AGPL-3.0-or-later**. Full text in [LICENSE](LICENSE).

### Why AGPL and not GPL

GPL obligations only trigger when you **distribute** the software. Someone could
run WARDEN as a hosted service and never release a line of source — which is the
biggest hole in GPL.

AGPL section 13 (*Remote Network Interaction*) extends the obligation to
**network use**: if you let other people use it over a network, you must offer the
complete source.

There are **no third-party dependencies** — only the .NET runtime and the Win32
API — so there is nothing else to attribute. Details:
[docs/许可与合规.md](docs/许可与合规.md).

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md). Comments are written in Chinese (the
project's working language); code and identifiers are English.

## Status

Beta (`0.1.0-beta.1`). It works and it is tested, but the behaviour scoring is
still being tuned — expect the thresholds to move. Bug reports with a log
excerpt are very welcome.
