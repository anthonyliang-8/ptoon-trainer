# platoon-reverse

An external memory trainer for **pla_toon** (Steam, Unreal Engine 4.20, process `pla_toon-Win64-Shipping`), plus the
reverse-engineering notes and tooling it was built from.

Intended for **offline / bot matches** on your own machine. Aim assist and ESP only ever consider soldiers flagged `isBot`.

## Features

| Key | Feature | How it works |
|---|---|---|
| `1` | God mode | Your health component gets a copied vtable whose `TakeDamage` slot is a bare `ret`; health is also held at 100. Bots keep the real vtable. Restored on toggle-off and quit. |
| `2` | Infinite ammo | Keeps the held weapon's `AmmoInClip` at `ClipSize`. |
| `3` | Bot head aim | While right mouse is held, turns the view toward the head hitbox of the live bot closest to the crosshair inside an FOV cone (`+`/`-` adjusts, 5-180 deg). |
| `4` | No spread | Pins the held weapon's accuracy/shotgun-spread/recoil fields and the soldier's recoil levels; originals are restored on toggle-off. |
| `5` | Bot ESP | Click-through overlay with a box, health, distance and weapon for each live enemy bot. Windowed/borderless only. |
| `Q` | Quit | Restores all patches. |

## Build and run

No SDK or project files; everything compiles with the .NET Framework compiler that ships with Windows (C# 5 only).

```powershell
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
& $csc /nologo /target:exe /out:bin\PlatoonTrainer.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll `
    src\Mem.cs src\Ue.cs src\Overlay.cs src\Trainer.cs
& $csc /nologo /target:exe /out:bin\UeDump.exe src\Mem.cs src\Ue.cs src\Dump.cs
```

Start the game, join a match, then run `bin\PlatoonTrainer.exe`. It waits until a local soldier exists.
`bin\UeDump.exe <outDir>` writes a full reflection dump (`sdk.txt`, `objects.txt`); the latest is in [dump/](dump/).

## Architecture

```
Trainer.cs ──┬── Ue.cs ── Mem.cs ── ReadProcessMemory / WriteProcessMemory ──> pla_toon
             └── Overlay.cs (WinForms, own STA thread)
Dump.cs ─────── Ue.cs ── Mem.cs
```

- **Stack:** C# / .NET Framework 4.x, Win32 P/Invoke (`kernel32`, `user32`), WinForms for the overlay. No injection: the
  trainer is a separate process that reads and writes game memory.
- **[Mem.cs](src/Mem.cs):** process attach, typed reads/writes, remote allocation, PE section lookup.
- **[Ue.cs](src/Ue.cs):** a UE 4.20 reflection reader. It locates `GNames` and `GUObjectArray` by pattern-scanning the
  module's `.data` section, then walks `UClass`/`UProperty` chains to resolve any field offset by name.
- **[Trainer.cs](src/Trainer.cs):** resolves every game field by name at startup, then runs three loops:
  a refresh thread (rescans all objects every 2 s for the local controller and soldiers), a 4 ms tick thread
  (applies god/ammo/spread/aim), and the console menu. ESP data is produced on demand for the overlay.
- **[Overlay.cs](src/Overlay.cs):** transparent, topmost, click-through form sized to the game's client area; black is
  the transparency key.
- **[OFFSETS.md](OFFSETS.md):** what each offset means and how it was found. **[ghidra/](ghidra/):** headless
  decompile script and project metadata. The ~340 MB analysis DB is not in git; see [ghidra/README.md](ghidra/README.md).

Because offsets come from reflection, a game update only breaks the trainer if class or property *names* change. The
exceptions are two constants found by probing: `ComponentToWorld` (`0x180`) and the `TakeDamage` vtable slot (`0x468`).

## Known limitations

- The game's `AC_HackerFucker_C` reports tampering with weapon damage, bullet speed, recoil multiplier, fire rate and
  health to the server. The trainer avoids those fields, but this is not a safe-for-online design.
- If the trainer is killed without pressing `Q`, god mode stays patched in the game until you respawn or restart it.
- Hotkeys are read from the console window, so it needs focus to toggle features.
- ESP does not work in exclusive fullscreen (the game draws over the overlay).
- Aim writes the view rotation directly: no smoothing and no line-of-sight check.

## Outstanding work

**Fixes**
- Derive `ComponentToWorld` and the `TakeDamage` slot instead of hardcoding them, or fail loudly if they look wrong.
- Retry attaching when the game isn't running yet instead of throwing at startup.
- Free the executable allocation made for the god-mode vtable (currently leaked once per run).
- Restore patches on unhandled exit (`AppDomain.ProcessExit` / Ctrl+C handler).
- Aim's `headZ` and the ESP "pad" are magic numbers; add a line-of-sight or visibility check so aim ignores bots behind walls.

**Cleanup**
- [Trainer.cs](src/Trainer.cs) holds all state as statics in one file; split features (god, ammo, spread, aim, ESP) into
  their own classes.
- `Mem` allocates a new array for every scalar read and the tick loop issues dozens per cycle; batch reads of a soldier's
  fields, and avoid a full `AllObjects()` rescan every 2 s (use object-index deltas or only re-read changed chunks).
- Add a build script (`build.ps1`) so the csc commands live in one place.
- [OFFSETS.md](OFFSETS.md) predates no-spread and ESP and still lists the globals as fixed module offsets; they are now
  found by scanning, so document the scan instead.
- Teams detection in ESP is a heuristic (everyone on one team means free-for-all); confirm against real team modes.

**Ideas**
- Global hotkeys (`RegisterHotKey`) and a config file for FOV, aim bone and toggles.
- Aim smoothing and bone selection (head/chest).
- Optional ESP extras: skeleton from the 15 hitboxes, snaplines, off-screen indicators.
- Recorded smoke tests for `Ue.cs` against a saved `objects.txt` so offset-resolution regressions show up without the game.
