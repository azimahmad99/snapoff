# Audit — Snapoff 0.9.1.0

One page, reproducible. Every number below came from running the command shown, on Windows 11 (build 26200) with .NET Framework 4.8, 2026-11-19. If a claim cannot be reproduced on your machine, the claim is wrong — not your machine.

Build audited: `Snapoff.exe`, 54,272 bytes. The hash below belongs to the exact binary inside `Snapoff-0.9.1.zip` on the release page — compilation is not reproducible, so rebuilding the same source produces a different hash:

```
sha256  7570a02467eaa2b9d609e58fe08981f596abf5738cf6ced23adb0501b0ebb053   (0.9.1 release build)
```

What must reproduce is the behaviour, not the bytes. Rebuild from source, re-run the checks below, and compare the numbers — a mismatch means the audit page is wrong, and the source is the authority.

---

## 1. No network code

The binary carries no reference to any network library. Windows P/Invoke targets and assembly names appear as plain ASCII in a managed PE, so a string search is enough to catch one:

```bash
for d in ws2_32 wininet winhttp urlmon dnsapi mswsock mshtml; do
  printf "%-9s %s\n" "$d" "$(grep -a -o -i "$d" Snapoff.exe | wc -l)"
done
```

```
ws2_32    0
wininet   0
winhttp   0
urlmon    0
dnsapi    0
mswsock   0
mshtml    0
```

The same search finds `mscoree` exactly once (the CLR bootstrapper) and `user32` once — the only DLLs this program talks to.

**Caveat, stated plainly:** this is a string search, not a full import-table dump. It would not catch a library name assembled at runtime from fragments. Reading the source closes that gap: every native call in the project is declared with `[DllImport]`, and the complete list is:

```
user32.dll  × 4      (DPI awareness, hotkey registration, message posting in the test build)
shcore.dll  × 1      (DPI awareness fallback, Windows 8.1+)
```

## 2. No registry

`advapi32.dll` — the DLL that owns `RegCreateKey`, `RegSetValue`, and friends — appears **zero** times in the binary. In the source, the word "registry" appears only inside comments and UI text explaining that the registry is deliberately not used:

```bash
grep -c -i "Microsoft.Win32\|Registry\|RegSetValue\|RegOpenKey" Snapoff.cs
```

Run the same search on `Snapoff.exe`: `advapi32` → 0.

Consequence you can check in seconds: install nothing, run the exe, then look at `HKCU\Software` — there is no Snapoff key, and never was one.

## 3. Exactly three things are written to disk

The complete list of write paths in the source, by function:

| Where | What |
|---|---|
| `Settings.Save()` | `settings.ini` next to the exe — only after you press Save in the settings dialog |
| `ApplyAutostart()` | `Snapoff.cmd` in the user's Startup folder — only when autostart is enabled; deleted when disabled |
| `TrySave()` / `TrySaveAuto()` | the captured PNG/JPEG/BMP in the configured output folder (default `Pictures\Snapoff`, created on demand) |

Everything else is in process memory. There is no cache directory, no temp spill, no log file, no crash reporter.

## 4. No service, no driver, no scheduled task, no admin rights

- No `sc.exe`, no `schtasks`, no Task Scheduler COM, no service control API anywhere in the source.
- The single-instance guard is a named `Mutex` in memory — not a lock file, not a registry key.
- Nothing in the code requests elevation; the exe has no manifest requiring `requireAdministrator`.

## 5. What it does touch, so nobody is surprised

- Global hotkey registration (`RegisterHotKey`) — a per-session window-message hook, released on exit.
- Screen capture (`CopyFromScreen`) — reads pixels of your own desktop, writes them to the file you asked for.
- Clipboard on `Ctrl+C` — write-only; it never reads what is already there (except in the test-only `--clipcheck` diagnostic).
- `explorer.exe` is launched only when you pick "Open output folder" from the tray menu.

## 6. Reproduce the behaviour claims

```bash
./Snapoff-e2e.exe --selftest    # exit 0, prints every measured number
./Snapoff-e2e.exe --clipcheck   # clipboard write + read timings
```

The self-test is in the test build only (`Snapoff-test.cs`); the shipped `Snapoff.exe` answers `--selftest` with one line and exit code 2, because measurement code is not part of the product.

## 7. Not verified (do not quote these as facts)

- The import table was not dumped with a PE tool; the audit above is string search plus source inspection.
- Behaviour under a corporate EDR/AV product was not tested. Unsigned binaries are sometimes quarantined by policy, regardless of what they do.
- The "desktop locked / RDP disconnected" capture path is readable code and a visible error message, not an automated test.
- Mixed-DPI correctness was verified mechanically (process awareness, monitor rectangles, capture size, output size), not on two monitors with different scale factors.
- Re-run section 1 and 2 on every release. A future commit could add an import without anyone noticing; these two greps are the tripwire, and they belong in the release checklist.