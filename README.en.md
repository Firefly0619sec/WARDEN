# WARDEN — a popup and window behaviour blocker for Windows

> 中文版：[README.md](README.md)　·　License: **AGPL-3.0-or-later**
>
> **Status: Beta 2 (0.1.0-beta.2)**
> Downloads: <https://github.com/Firefly0619sec/WARDEN/releases>
> Interface languages: **six** — Simplified Chinese, English (UK), English (US),
> 繁體中文（中國臺灣 · 中國香港 · 中國澳門）

WARDEN watches the windows that appear on your desktop and closes the ones that
misbehave. It is **not** an ad-signature list and it does **not** rely on window
titles alone — it scores **behaviour**: a window that pops up dozens of times in
seconds, repeatedly steals focus, has no input box, has no child controls, is a
small floating panel, churns its title, or comes back after being closed.

The result: annoying popups go away, and ordinary windows are left alone.

---

## Why we build this

### A scene that happens every day

Someone who is not a computer person is in the middle of writing something.
A box appears in the corner of the screen:

> *Your computer has 27 security risks. Clean now.*

They do not know whether it is real. They click **Clean now**, and end up with
three browser extensions and a new browser they never asked for.

**They are not stupid.** They are simply one person, up against hundreds of teams
whose entire job is to work out how to make them click one more thing. That is
not a fair fight, and it should not be their problem to win.

### What we are trying to do

**Give them a quiet desktop back.**

Almost every trade-off in WARDEN comes back to that one sentence:

| We choose | Rather than | Because |
| --- | --- | --- |
| Missing one popup | Killing one innocent window | If it breaks their software once, they will never trust it again |
| Hide windows of signed programs | Force-kill everything | Signed software is usually legitimate; killing it can break their system |
| Consent dialogs written in plain words | A wall of jargon | "This program's popup will not close. Force-kill it?" is answerable |
| A red alert that lists the three things to do | A red alert that only shouts | A frightened person needs steps, not an alarm |
| Logs in everyday language | English debug tags | So they can look and understand what happened |
| Never going online, never asking for an account | Collecting "just a little" data | Nobody owes us their privacy in exchange for a free tool |

### The intersection of technology and the humanities

Steve Jobs described Apple as standing at the intersection of technology and the
humanities. Our reading of that is simple: **the numbers are not the goal; the
person is.**

* An engine that closes a popup in 0.1 seconds is a failure if it leaves the user
  wondering whether it will kill their own software.
* A tool that blocks 100% of popups is also a failure if an older user cannot
  understand a single line it shows them.

So we keep reworking the things that never appear in a benchmark: replacing
technical terms with plain language one at a time, growing the red alert from one
button to five (where pressing one does something visible, immediately), and
reading every line of the settings window out loud until it makes sense.

## Supply chain and trust

> This section answers one question: **why should I trust this installer?**

Our rule is blunt: **anything you can verify yourself, you should not have to
take on faith.**

| Practice | What it lets you do |
| --- | --- |
| **Zero third-party dependencies** | Only the .NET runtime and Windows' own APIs — nothing of unknown provenance |
| **Every commit GPG-signed** | Verify any change in the public history |
| **The build script ships with the source** | Do not trust our binary? `git clone` and run `build.ps1` |
| **`SHA256SUMS.txt` on the release page** | Compare after downloading; if it does not match, do not install |
| **The online installer verifies before running** | A bad hash means it **refuses to run**; no checksum file, no automatic run either |
| **The updater backs up before overwriting** | Roll back if something goes wrong — not "upgrade and hope" |
| **Self-check on every start** | The program hashes itself; if it was swapped, it halts |
| **Evidence stays on your machine** | It is written to a local log; whether it is ever sent is your decision |
| **Works fully offline** | The main program never touches the network — fine on locked-down networks |

We treat "**installed it and it will not start**" as a release blocker.
For someone technical that is a nuisance; for everyone else it means "this
software is broken". (A real example from our own log: the self-check used to
decide "this looks like a fresh update" from file timestamps. That was too
fragile — rewriting the settings file made it refuse to launch, three times in
one day — so it now decides from the code signature instead. See
[docs/工作日志-2026-10-10.md](docs/工作日志-2026-10-10.md).)

### About code signing (current state)

We have applied to the **SignPath Foundation** for free open-source code signing,
and are waiting for review. Once approved, releases will carry a verifiable
signature and SmartScreen will stop showing "Unknown publisher". Until then,
please check the release against `SHA256SUMS.txt`. A PGP key is also available —
see [docs/PGP公钥-在哪里与怎么用.md](docs/PGP公钥-在哪里与怎么用.md).

## Why it exists

Most "popup blockers" are either

* a fixed list of process names that goes stale in weeks, or
* a title-keyword matcher that any vendor can defeat by renaming a button.

Behaviour is much harder to fake without giving up the thing the popup exists
for (stealing your attention), so WARDEN scores behaviour and only acts above a
threshold you control.

### What is in this version (Beta 2)

| Capability | Notes |
| --- | --- |
| Behaviour-based blocking | No ad list, no title matching alone — it scores behaviour |
| Six interface languages | Simplified Chinese, English (UK), English (US), and three Traditional Chinese variants |
| First-run guide | One question: watch in drill mode first, or start blocking now |
| Tray state icon | Original = guarding, amber = paused, red = emergency suppression |
| Settings window | Seven tabs: Rules / Protection / Actions / Lists / Safety / Log / Language |
| Rule import & export | JSON backup and sharing, with validation and a skipped-entry report |
| Log viewer | Browse by category, export a Markdown report in one click |
| Evidence reporting | One-click masked report; usernames/e-mails/phone numbers are redacted; nothing is uploaded unless you choose a transport |
| Command line | `warden-cli`: status / tail / stop / rules / report |
| Online installer | ~5 MB, fetches the latest release and verifies SHA256 before running |
| Portable mode | Drop `WARDEN.portable` next to the exe; all data stays in that folder |
| Crash collection | Writes a local `CRASH-<time>.log`; nothing is sent anywhere |

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

Verify the download against the SHA256 published with the release before running
anything you got from a third party. Code signing (SignPath Foundation) is
under review; until it lands, the checksum is the verification path.

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
