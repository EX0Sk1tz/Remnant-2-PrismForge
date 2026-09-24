# Prismforge: developer documentation

A WPF (.NET 8, x64) tool that reads and edits your prisms in **Remnant 2's memory while the game
runs**. Offsets come from the Cheat Engine table `Remnant 2_v3.1_Released.CT` ("Manage Prisms...");
stat names and descriptions come from the game's `PrismStoneDataTable` / `PrismStoneMythicDataTable`
(taken from the save editor in `R2PrismEditor-ReadOnly`).

The game's last update was in 2024, so the offsets target that final build.

---

## 1. Using it

1. Start Remnant 2 and load a character.
2. Start `Prismforge.exe`. It finds the game on its own (no admin rights needed) and lists
   your prisms. The indicator in the title bar shows the state: *Looking for Remnant 2* →
   *Waiting for character* → *Linked*.
3. Pick a prism. Change a segment's stat with **Change**, its level with − / + or by typing, and
   the pending XP in the header. **Stage** sets every non-legendary segment to one level.
4. Edits are **staged** (amber bar, amber diamond in the list) until you press **Apply to game**.
   After applying, every value is read back from memory to confirm it.
5. **Discard** drops staged edits. **Revert to session start** stages the values the prism had
   when the editor first saw it this session; apply them to undo your edits.
6. The game writes the new values into your save at its next autosave. **Back up your save first.**

Build: `dotnet build`. Release: `powershell -ExecutionPolicy Bypass -File build_release.ps1` (close the app
first). It publishes a self-contained single exe to `publish\` (~68 MB, no .NET install needed) and packs
`Release\Prismforge_v<version>.zip` (exe, README.txt, CHANGELOG.txt) for Nexus. The version comes
from `<Version>` in the csproj. Nexus page text: `Release\NEXUS_PAGE.txt`. User docs: `README.md`.

**App icon**: `Assets\app.ico` (multi-size .ico). It is used for the exe (`<ApplicationIcon>`), the
taskbar and the window. Replace the file and rebuild to change it. Links shown in the About panel
(Nexus, source) are set in `Diagnostics\AppInfo.cs` (`NexusUrl`, `SourceUrl`).

---

## 2. Project layout

| Path | Purpose |
|---|---|
| `App.xaml(.cs)` | Theme merge, generated film-grain texture, logger, global exception handlers, `--diagnose` switch. |
| `UI/Theme.xaml` | Palette, fonts (Bahnschrift), buttons, fields, scrollbars, tooltip, stat picker. |
| `UI/MainWindow.xaml(.cs)` | Chrome-less window: prism list, prism detail, action bar, journal, status bar. |
| `UI/MainViewModel.cs` | Auto-connect loop, scan/merge, staging, Apply/Discard/Revert, commands. |
| `UI/Controls.cs` | `Gem` (faceted two-tone diamond), `LevelPips`, `{ui:Caps}` letter-spacing, converters. |
| `Models/PrismModels.cs` | `PrismData`, `SegmentSlot`, `FeedSlot`: live value, staged edit, session original. |
| `Game/GameOffsets.cs` | Every offset and signature. |
| `Game/SegmentCatalog.cs` + `Data/SegmentCatalog.json` | 110 rows (45 standard, 23 fusion, 42 legendary) with display name, colour, description, class. |
| `Game/FNameReader.cs` | Finds the FNamePool, resolves IDs ↔ names (case-insensitive, cached). |
| `Game/PrismScanner.cs` | Pointer chain, inventory scan, prism/segment/fed reads, stale checks, diagnostic. |
| `Game/ObjectArray.cs` | GUObjectArray reader; finds class default objects by name. |
| `Game/PrismWriter.cs` | Commits staged edits, then reads them back. |
| `Memory/*` | Win32 P/Invoke, read/write helpers, AOB scan. |
| `Diagnostics/Log.cs` | Session log (Info level, `--verbose` for Debug), newest 10 kept, plus the live journal feed. |
| `Diagnostics/AppInfo.cs` | Version, credits, links, settings file; data folder `%LocalAppData%\Prismforge`. |
| `UI/MainViewModel.Shell.cs` | About panel and first-start safety notice (shown once per version). |
| `Game/StatAdvice.cs` | Soft warning rules for attribute targets. |
| `Game/StatInfo.cs` | Tooltip text for every attribute; "(likely)" marks explanations inferred from the name. |

---

## 3. Runtime flow

A timer ticks every 2 s:

1. **Not attached**: look for `Remnant2-Win64-Shipping.exe`, open it with VM read/write/operation +
   query rights, find the FNamePool, resolve the 110 catalog rows to FName IDs.
2. **Game exited**: drop everything and go back to step 1.
3. **Scan** when forced, when the list is empty, or when any prism's addresses went stale
   (inventory changed). Staged edits and session originals are carried over to the new objects.
4. Otherwise **refresh live values**; slots without staged edits follow the game.

After the first scan of a connection, class default objects are indexed in the background
(also re-run on every manual Rescan).

---

## 4. Memory layout (confirmed on the live game, 2026-09-24)

```
GEngine global   ← AOB 49 8B D7 48 8B 01 FF 90 D8 02 00 00, instruction at hit-7 = mov rcx,[rip+x]
  [global]          UGameEngine                      (single dereference)
    +0xFC0          BP_RemnantGameInstance_C
      +0x38         TArray<ULocalPlayer*> (Num +0x40)
        [0]         LocalPlayer
          +0x30     Remnant_PlayerController_C
            +0x2D0  Character_Master_Player_C
              +0xB48  RemnantPlayerInventoryComponent   (0xB40 tried as fallback, then a class-name scan)
                +0x1D8/+0x1E0  items TArray, stride 0x28
```

| Struct | Offset | Meaning |
|---|---|---|
| Inventory item | +0x08 | item definition; `[+0x110]+0x18` = class FName (`Default__PrismOfVoracity_C`) |
| | +0x18 | instance data |
| Definition class | `[[+0x110]+0x300]+0x30` | display name, wide string ("Prism of Voracity") |
| Prism data | +0x28 | internal level (int32) |
| | +0x58 / +0x60 | CurrentSegments TArray |
| | +0x70 / +0x78 | CurrentFeedData TArray (fed fragments; "Roll Chances" in the CT) |
| | +0x84 | PendingExperience (float) |
| Segment (0x28) | +0x00 | RowName FName (index, number) |
| | +0x08 | level (int32) |
| | +0x0C | -1, or an action handle for action legendaries (not touched) |
| | +0x20 | class default object of the row, e.g. `Default__RelicFragment_ModDamage_C`, `Default__PrismSegment_Tank_C`; null for action legendaries (Unbreakable) |
| Fed fragment (0x0C) | +0x00 | RowName FName |
| | +0x08 | FedLevel (int32) |

The level shown in the UI is the sum of segment levels, as in the save editor.

**FNamePool**: AOB `48 03 F8 44 89 44 24 38`, instruction at hit-7 is `lea rax,[rip+x]`; blocks at
`[target+0x18]+0x10` (the CT's method). Validated by name #0 == "None".

**GUObjectArray**: AOB `48 8B 05 ?? ?? ?? ?? 48 8B 0C C8 48 8D 04 D1` gives the `Objects` chunk-table
field; `NumElements` at +0x14, item stride 0x18. Validated by looking up GEngine, GameInstance,
PlayerController and Pawn by their `InternalIndex` (+0x0C). If the signature fails, the module's
writable data is scanned for a qword that validates the same way.

---

## 5. What Apply writes

- XP: float at data+0x84.
- Level: int32 at slot+0x08.
- Stat: FName `{id, 0}` at slot+0x00. For segments, +0x20 is set to the new row's class default
  object (found in another segment or through GUObjectArray, re-validated by name before use).
  If that class isn't loaded in the running game, +0x20 is set to null (a state the game uses
  itself) and the status bar says the effect starts after reloading the character.

Before writing, the prism's item/data/array pointers and counts are re-checked; if anything moved,
nothing is written and a rescan is triggered.

---

## 5b. Attributes page: holding character stats (code hook)

Segment levels above 10 don't raise a prism bonus (the game's per-level table stops at 10).
To go past normal limits, the **Attributes** page edits the character's computed stats, as the
CT's "Manage System Statistics" script does.

**Stat list**: `[Pawn+0x648]` StatsComponent → `+0x128` TArray (Num `+0x130`), element stride 0x14:
`+0x00` FName id, `+0x08` float value. About 210 stats (e.g. `WeakSpotDamageMod`, `CritDamageMod`,
`RangedDamageMod`, `MoveSpeedCap`, `SkillCooldownMod`).

**Hook**: the game recalculates these values on events (equip, buffs, checkpoints), so a plain write
doesn't last. Pressing **Hold** patches the same place the CT hooks:

```
AOB 09 04 93 48 63 C6 48 8D 0C 80, site = hit+0x26:  41 89 44 24 08   mov [r12+08],eax
                                                     (r12 = stat entry, eax = value, ecx = stat FName id)
```

The 5 bytes become `jmp cave`. The cave (one page allocated within ±2 GB) looks `ecx` up in an
override table and replaces `eax` on a match, then runs the original store and jumps back.
Flags, rdx and r11 are preserved.

```
cave+0x000 "R2PEHOOK"   +0x008 original bytes   +0x010 count   +0x014 hit counter
cave+0x018 { int32 id; float value } × 200       cave+0x800 code
```

- Holding also writes the value straight into the stat entry, and the tick re-writes it if it drifts.
- **Release** removes one stat. With none left, or on **Release all**, the original 5 bytes are restored.
  The cave page stays allocated in case a game thread is still inside it.
- Closing the app with stats held leaves the hook active. On the next start the app recognises
  its cave by the magic and takes the held stats over.
- If the site already starts with a jump that isn't ours (e.g. the CT's script is active), the app
  refuses to patch and says so.

Verified live 2026-09-24: install, 381 stat writes through the hook with the game stable,
correct jump targets, adoption after restarting the app, release restoring `41 89 44 24 08`.

---

## 6. Debugging

- **Journal** (status bar) shows the live log. **Logs** opens `%LocalAppData%\Prismforge\logs`
  (newest 10 logs and 10 diagnostic reports are kept). Start with `--verbose` for Debug-level lines
  (every chain step, AOB results).
- **Diagnostic** writes `logs/Prismforge_Diag_<time>.txt`: every chain step with class names and
  neighbour probes, catalog resolution, and a full dump of each prism including raw segment bytes
  and the +0x20 object's class.
- `Prismforge.exe --diagnose` writes that report automatically after the first scan.
- `Prismforge.exe --selftest-ui` (developer check): starts off-screen without taking focus,
  opens every stat picker of every prism, logs its item count (`SELFTEST` lines), rescans, repeats,
  then exits. It never writes to the game or releases another instance's stat hook.

---

## 7. Open points

- [ ] Full Apply round trip (stat change, level change, XP) checked in game, then after save and reload.
- [ ] Whether the game re-applies stat bonuses immediately after a level/stat write or only after
      a checkpoint/re-equip (the CT notes that most stats apply after visiting a checkpoint).
- [ ] Fed fragments: no prism on the test character had any, so +0x70 is only verified as empty.
- [ ] `Seg +0x0C` action handle: changing a legendary that uses an action (e.g. Unbreakable) leaves
      the old action running until reload.

---

## 8. Changelog

- **2026-09-24 (public 1.0.0)**: Self-contained release build and `build_release.ps1`; app icon;
  About panel with version and credits; first-start safety notice; logs and settings moved to
  %LocalAppData% with retention; Info log level by default; Game Pass detection message; soft warnings
  on the Attributes page; README, CHANGELOG and Nexus page draft. Versioning restarts at 1.0.0 for the
  public release (the 2.x entries below were internal).

- **2026-09-24 (v2.1)**: Attributes page. Live list of the ~210 computed character stats, search
  and category filters, and **Hold** backed by a table-driven code hook at the CT's System Statistics
  injection point (any number of stats instead of the CT's four). Hit counter, adoption across app
  restarts, clean restore on release. Diagnostic report now includes the stat list.

- **2026-09-24 (v2.0)**: Rework. New Remnant-style UI (custom chrome, gems, pips, staged edits,
  journal). Auto-connect and live refresh. Real stat catalog from the game's data tables (replaces
  guessed `PrismSegment_*` names). Fed fragments read/edit. Class-name based inventory offset
  (replaces broken version detection). GUObjectArray lookup so stat changes also update the cached
  class object at +0x20. Stale-address checks and read-back verification on every write.
  Session revert. Minimal process access rights; no admin needed. Output renamed to `Prismforge.exe`.
- **2026-09-24 (v1)**: Logging, GEngine RIP-relative fix, diagnostic, FName search rewrite.
