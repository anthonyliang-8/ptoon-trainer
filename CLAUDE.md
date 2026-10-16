# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

Terminal trainer for **pla_toon** (Steam, Unreal Engine 4.20, process `pla_toon-Win64-Shipping`): god mode, infinite ammo, bot-only head aim, no spread, bot ESP overlay. Scope is offline/bot matches only; aim must stay restricted to soldiers with `isBot` set. It's an external process-memory tool (ReadProcessMemory/WriteProcessMemory), no DLL injection.

## Build

No Python, no .NET SDK, no csproj/sln, no tests. Compile with the .NET Framework compiler; **C# 5 syntax only** (no `$""`, `?.`, expression-bodied members):

```
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
& $csc /nologo /out:bin\PlatoonTrainer.exe src\Mem.cs src\Ue.cs src\Trainer.cs
& $csc /nologo /out:bin\UeDump.exe        src\Mem.cs src\Ue.cs src\Dump.cs
```

`bin\*.exe` is committed. Both programs have their own `Main`, so never compile `Trainer.cs` and `Dump.cs` together. Running requires the game to be up (attaches by process name).

- `bin\UeDump.exe dump` rewrites `dump\sdk.txt` / `dump\objects.txt` (reflection dump of every class, property and offset). Grep these instead of guessing offsets.
- Ghidra 12.1.4 at `C:\Users\antho\tools\ghidra\ghidra_12.1.4_PUBLIC`. Decompile specific addresses headless with `ghidra\DecompAt.java` (project `ghidra\proj`, `-noanalysis`). The analysis DB is not in git; see `ghidra\README.md`.

## Architecture

- `Mem.cs`: thin Read/WriteProcessMemory wrapper (`Ptr`, module base, remote alloc).
- `Ue.cs`: UE 4.20 reflection reader (GNames, chunked GUObjectArray, UClass/UProperty walking). Fixed struct offsets are constants here; the GNames/GUObjectArray addresses are build-specific module offsets (see `OFFSETS.md`).
- `Trainer.cs`: resolves game-field offsets **by reflected name at startup** (`ue.Off(class, "Field")`), so game updates only break it if names change. Two background threads (`RefreshLoop` finds the local soldier/bots, `TickLoop` applies features); the main thread is the key menu. Non-reflected values (`ComponentToWorld` `0x180`, `TakeDamage` vtable slot `0x468`) are hardcoded constants found by probing.
- God mode works by pointing your health component at a copied vtable whose `TakeDamage` slot is a `ret`; it must be restored on toggle-off/quit.
- `Overlay.cs`: click-through WinForms overlay (own STA thread) that draws the ESP boxes from `Trainer.EspTargets`.
- `Dump.cs`: standalone SDK dumper on the same Mem/Ue.

`OFFSETS.md` is the source of truth for what each offset means and how it was found. Update it when you discover or change one.

## Guidelines

Behavioral guidelines to reduce common LLM coding mistakes. Merge with project-specific instructions as needed.

**Tradeoff:** These guidelines bias toward caution over speed. For trivial tasks, use judgment.

## 1. Think Before Coding

**Don't assume. Don't hide confusion. Surface tradeoffs.**

Before implementing:
- State your assumptions explicitly. If uncertain, ask.
- If multiple interpretations exist, present them - don't pick silently.
- If a simpler approach exists, say so. Push back when warranted.
- If something is unclear, stop. Name what's confusing. Ask.

## 2. Simplicity First

**Minimum code that solves the problem. Nothing speculative.**

- No features beyond what was asked.
- No abstractions for single-use code.
- No "flexibility" or "configurability" that wasn't requested.
- No error handling for impossible scenarios.
- If you write 200 lines and it could be 50, rewrite it.

Ask yourself: "Would a senior engineer say this is overcomplicated?" If yes, simplify.

## 3. Surgical Changes

**Touch only what you must. Clean up only your own mess.**

When editing existing code:
- Don't "improve" adjacent code, comments, or formatting.
- Don't refactor things that aren't broken.
- Match existing style, even if you'd do it differently.
- If you notice unrelated dead code, mention it - don't delete it.

When your changes create orphans:
- Remove imports/variables/functions that YOUR changes made unused.
- Don't remove pre-existing dead code unless asked.

The test: Every changed line should trace directly to the user's request.

## 4. Goal-Driven Execution

**Define success criteria. Loop until verified.**

Transform tasks into verifiable goals:
- "Add validation" → "Write tests for invalid inputs, then make them pass"
- "Fix the bug" → "Write a test that reproduces it, then make it pass"
- "Refactor X" → "Ensure tests pass before and after"

For multi-step tasks, state a brief plan:
```
1. [Step] → verify: [check]
2. [Step] → verify: [check]
3. [Step] → verify: [check]
```

Strong success criteria let you loop independently. Weak criteria ("make it work") require constant clarification.

---

**These guidelines are working if:** fewer unnecessary changes in diffs, fewer rewrites due to overcomplication, and clarifying questions come before implementation rather than after mistakes.
