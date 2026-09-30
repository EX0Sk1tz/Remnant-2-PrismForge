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
| `Game/SegmentCatalog.cs` + `Data/SegmentCatalog.json` | 110 rows (45 standard, 23 fusion, 42 legendary) with display name, colour, description, class; `MergeLive` merges the game's own tables (§3a). |
| `Game/PrismTables.cs` | Reads `PrismStoneDataTable` / `PrismStoneMythicDataTable` from memory (mod support, §3a). |
| `Game/FNameReader.cs` | Finds the FNamePool, resolves IDs ↔ names (case-insensitive, cached). |
| `Game/PrismScanner.cs` | Pointer chain, inventory scan, prism/segment/fed reads, stale checks, diagnostic. |
| `Game/ObjectArray.cs` | GUObjectArray reader; finds class default objects and named objects (data tables). |
| `Game/PrismWriter.cs` | Commits staged edits, then reads them back. |
| `Game/GameBuild.cs` | Known game exes (Steam/Epic and Game Pass, both tested), store guess from path. |
| `Game/BuildProbe.cs` | Report header, module region map, signature check; attach report when the FNamePool fails. |
| `Diagnostics/SupportBundle.cs` + `UI/MainViewModel.Support.cs` | Support zip and its state summary. |
| `Memory/*` | Win32 P/Invoke, read/write helpers, AOB scan. |
| `Diagnostics/Log.cs` | Session log (Info level, `--verbose` for Debug), newest 10 kept, plus the live journal feed. |
| `Diagnostics/AppInfo.cs` | Version, credits, links, settings file; data folder `%LocalAppData%\Prismforge`. |
| `UI/MainViewModel.Shell.cs` | About panel and first-start safety notice (shown once per version). |
| `Game/StatAdvice.cs` | Soft warning rules for attribute targets. |
| `Game/StatInfo.cs` | Tooltip text for every attribute; "(likely)" marks explanations inferred from the name. |

---

## 3. Runtime flow

A timer ticks every 2 s:

1. **Not attached**: look for `Remnant2-Win64-Shipping.exe` (Steam/Epic, tested) or
   `Remnant2-WinGDK-Shipping.exe` (Game Pass, tested; see §6b), open it with VM read/write/operation +
   query rights, find the FNamePool, resolve the 110 catalog rows to FName IDs.
2. **Game exited**: drop everything and go back to step 1.
3. **Scan** when forced, when the list is empty, or when any prism's addresses went stale
   (inventory changed). Staged edits and session originals are carried over to the new objects.
4. Otherwise **refresh live values**; slots without staged edits follow the game.

After the first scan of a connection, class default objects are indexed in the background
(also re-run on every manual Rescan).

### 3a. Prism tables read from the game (mod support)

After the first successful scan, Prismforge reads `PrismStoneDataTable` and
`PrismStoneMythicDataTable` from the running game (`Game/PrismTables.cs`: DataTable RowMap, row
field offsets from the row struct's reflection data) and merges them into the embedded catalog
(`SegmentCatalog.MergeLive`). This is for mods such as Beyond Hell that add fusions and repoint
legendaries:

- Rows a mod adds become pickable (mythic table → Legendary; two-colour category or combo → Fusion).
  New fusions get a description from their fragment pair ("Status Damage + Mod Damage").
- Known rows take the game's name, description and segment class, so Apply caches the class the
  mod uses. Rows a mod added, renamed or repointed carry a mod tag in the pickers ("Beyond Hell" when
  its pak is in `Content\Paks`, else "Mod").
- Rows a mod points to `PrismSegment_Invalid` stay visible on prisms that have them but are not offered.
- Embedded rows the game's tables don't contain are hidden.

On an unmodded game the tables equal the embedded catalog, so nothing changes. The embedded catalog
stays in use when the tables can't be read (tried on up to 10 scans), when only one of the two is
readable, or when fewer than 90% of the embedded rows are in them (a misread); the log says which.
The catalog is reloaded fresh on every attach. The diagnostic report has a "Prism tables (live)"
section listing every row; `NEW` marks rows not in the embedded catalog, `CLS` rows whose class differs.

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

### 5a. Segment levels above 10 (tested on Steam, 2026-09-27)

The level field is a plain int32 and the game accepts any value. What the value does depends on the
segment kind:

| Kind | Bonus source | Above level 10 |
|---|---|---|
| Standard | Per-level curve, 10 keys, not linear (Skill Duration: Lv5 3.6%, Lv9 8.2%, Lv10 15%) | Stays at the level-10 value (Lv11 and Lv50 both 15%). The curve holds its last key. |
| Fusion | per-level value × level | Scales without a cap (Hulk Lv10 10%/15% → Lv20 20%/30%). This is how 1,000,000-level prisms are made. |
| Legendary | — | Not tested. |

- The prism level the game shows is the sum of segment levels (5 × 100 + legendary 1 = 501 in the
  Game Pass test).
- **Bonuses apply on equip.** Written levels show in the tooltip right away, but the character's
  stats only change after the prism is unequipped and re-equipped (Skill Duration Mod 15 → 0 → 15).
  Apply says so in the status bar.
- **Levels are saved.** A level of 50 survived quitting to the main menu and reloading; the game
  doesn't correct it on load. "Revert to session start" only helps while the editor is attached.
- Editor limits: segments accept 0 to 100,000,000. Six segments at that level still sum to well
  under the int32 maximum. Fed fragments stay at 0 to 999, since levels above 10 are untested there.
  Each segment row explains what its level does past 10. Fusions above 100 are shown as a warning,
  because extreme bonuses (cooldowns below −100%, millions of regen) can break the game.
- The level pips show 10 ticks. Above that, they show the number instead.

### Extra segments (experimental, 2026-09-27)

CurrentSegments is a normal `TArray`: Data `+0x58`, Num `+0x60`, **Max `+0x64`**.

- Loading a save allocates exactly the used count (the test prism read Num 5 / Max 5).
- When the game adds a segment itself (legendary pick after an XP level-up), UE's grow slack kicks
  in: the array moved and read **Num 6 / Max 25**. The spare slots hold stale heap bytes.
- **Add segment** writes a complete 0x28-byte entry into slot `Num` (FName `{id,0}`, level,
  `+0x0C = -1`, zeros, `+0x20` = class default object or null), then raises Num. The buffer still
  belongs to the game's allocator, so freeing, growing and saving work as usual.
- Any segment can be removed (see "Remove, reorder, reset" below).
- A buffer allocated by the editor (`VirtualAllocEx`) is deliberately not used: the game would later
  free or reallocate it through its own allocator and crash.
- The scanner accepts up to Num ≤ Max ≤ 256 segments (was a hard limit of 16).
- Confirmed on Steam: a 7th segment (Warrior, Lv 10) added this way is listed on the game's prism
  screen with its computed bonus (+15% / +15%) and survived save → quit to menu → reload.
- After a reload the array is exact again (Num = Max), so spare room exists only in the session
  where the game grew the array. Add every extra segment in that session.
- **The game only adds a segment while a prism has at most 5 normal segments.** With 5 fusions it
  added the legendary pick; with 6 normal segments (5 fusions + an added one) it ignored the pick
  and didn't consume the pending XP.
- **Make room** gets spare room on a prism that already has its legendary:
  1. It reorders the array to [first 5 normal segments][other normal segments][legendaries].
  2. It sets Num and Max to 5 (or fewer, if the prism has fewer normal segments). Lowering Max is
     safe because UE frees and reallocates by pointer.
  3. Then the user gives the prism XP, and the game offers the legendary again. It finds the array
     full, reallocates with slack (Num 6 / Max 25 in testing) and appends the new pick. The old
     buffer, with the removed entries in it, is freed.
  4. On the next scan the editor sees the new array and writes the removed normal segments back
     from its backup into the spare room.

  Lowering only Num is not enough: the game then reuses the free slot without reallocating.
  The removed entries and the old Num/Max are kept in the editor, and **Restore legendary** puts them
  back until the game reallocates. The removed entries are also written to the log as hex. Prismforge
  has to stay open between Make room and the legendary pick. After Make room, previously added
  segments count as normal ones.
- Confirmed on Steam (2026-09-27): Make room → XP → legendary pick → automatic put-back → Add
  segment, repeated up to at least 13 fusions at level 1,000 plus the legendary. The game's prism
  screen lists all of them with linearly scaled bonuses (e.g. Hulk +1,000% / +1,500%).
- Attaching while the game is still loading can resolve 0/110 segment names, which left the stat
  pickers empty for the whole session. Name resolution now retries every 5 s (for up to 5 minutes)
  after each scan, then rebuilds the pickers.
- Extra segments' bonuses reach the character stats: confirmed on the Attributes page with 27
  segments (2026-09-28).

### Growing the array with the game's allocator (2026-09-28)

`Game/GameAllocator.cs` finds UE's `GMalloc` and calls it, so a full segment array can get a bigger
buffer that the game treats as its own. This replaces the Make room workflow; Make room only shows
when GMalloc wasn't found.

- **Finding GMalloc:** shipping builds inline `mov rcx,[rip+G]; test rcx,rcx; jne …` wherever they
  allocate. Of the most-referenced globals loaded that way, GMalloc is the one whose vtable has
  FMalloc's default `TryMalloc` (`mov rax,[rcx]; jmp [rax+20]` at `+0x28`) and `TryRealloc`
  (`… jmp [rax+30]` at `+0x38`). This is a structural check without a fixed offset, so it should also work on the Game Pass exe.
  Steam: global at module+0x78EBF08, 361 sites, found in ~0.7 s. (The most-referenced global with
  this pattern, module+0x78C1990 with ~6,100 sites, is the console manager, not the allocator.)
- **Slots:** `+0x20` Malloc(Count, Alignment), `+0x30` Realloc, `+0x40` Free(Original); call sites pass
  the matching arguments (e.g. `mov rdx,rbx; call [rax+40]`).
- **Calling it:** a 50-byte stub on its own page, run with `CreateRemoteThread` (the process handle
  now includes `PROCESS_CREATE_THREAD`). rcx = parameter block `{ &GMalloc, slot, arg1, arg2, result }`.
  FMalloc is thread-safe, so the call doesn't have to be on the game thread. Malloc/write/read/Free
  round trip confirmed on the live game.
- **Grow** (`PrismWriter.GrowSegments`, used by Add segment when Num == Max): Malloc (Num + 16) × 0x28,
  copy the entries, re-check that nothing moved, swap the Data pointer at `+0x58`, then raise Max at
  `+0x64`. In between, the game sees the new buffer with the old, smaller Max, which is valid. The old buffer
  is not freed (a game thread could still be reading it; it's a few hundred bytes).
  Works on an empty array too (Data 0, Max 0).
- Confirmed on Steam (2026-09-28): prism with 5 segments and Max 22 → 17 added into the game's slack →
  the next Add segment grew the array to Max 38 → 27 segments, 11 spare. Levels changed on all of them;
  their bonuses show on the Attributes page.
- Also confirmed: save → reload keeps the grown array and its segments, and Add segment grows the
  reloaded (exactly full) array again. Reset prism → save → reload → Add segment from 0 works, and the
  added segments can be changed, levelled and take effect.
- Still untested: a game-side grow (the game adding its own pick to a full array we allocated).

### Build codes (2026-09-28)

`Game/BuildCode.cs` and the planner page (`EX0Sk1tz/prismforge-planner`, `index.html`) share one format:
`PF1-` + base64url without padding of raw DEFLATE of UTF-8 JSON
`{ "v":1, "n":note, "xp":float, "s":[[row, level]…], "f":[[row, level]…] }`. Rows are catalog row
names, compared case-insensitively. Limits: 256 segments (0–100,000,000), 128 fed fragments (0–999),
note 200 characters; `n` and `xp` are optional. The page encodes with `CompressionStream("deflate-raw")`.
Round trip JS → C# → JS checked with umlauts, level 100,000,000 and fractional XP.

`PrismWriter.ApplyBuild` resolves every row first (nothing is written if one is unknown), then
replaces each array with `ReplaceArray`: grow through GMalloc if the build needs more than Max, Num 0,
write all entries, Num = count, read back. Segment entries are built like Add segment (FName, level,
action -1, class default object or null). CurrentFeedData is a TArray like CurrentSegments: Data
`+0x70`, Num `+0x78`, Max `+0x7C`, stride 0x0C; it is grown the same way. Pending XP is written only
if the code has `xp`. The internal level (`+0x28`) is left alone. Untested in game so far.

### Remove, reorder, reset (tested on Steam, 2026-09-28)

Confirmed in game, persistent through save and reload: moving a segment, removing a normal segment
in the middle (then Add segment into the freed slot), removing an equipped legendary, Reset prism,
and after a reset the game offering normal picks again from level 1 when given XP. So internal
level 0 is the right value for a blank prism. Removing a fed fragment is still untested (no test prism had one).

- **Remove / move** (segments and fed fragments) rewrite the TArray in place through
  `PrismWriter.Rearrange`. So the game sees a valid prefix at every step: Num first drops to the
  first position that changes, then the entries behind it are written, then Num is set to the new count.
  The buffer and Max stay unchanged, so every removed entry is spare room for Add segment in that
  session. The array before the change is logged as hex. Read-back compares row name and level only
  (the first 0x0C bytes); a legendary's action handle may change meanwhile.
- Removing a legendary whose `+0x0C` isn't -1 (an active ability) adds a note to unequip and
  re-equip or reload, since the game may still hold that ability.
- **Reset prism** writes segment Num 0, fed Num 0, XP 0 and internal level (`+0x28`) 0. That is what every
  untouched prism read in the diagnostics (`internalLv=0 xp=0 segments=0 fed=0`). Other readings:
  a naturally levelled prism read 59–60 with 6 segments, and one after repeated Make room read 15
  with 15 segments. So `+0x28` looks like the game's own
  level/pick counter; after a reset the game offers picks from level 1 again.
- Remove, move and reset are refused while a Make room is pending.
- The old "Remove added" (last segment, only those added this session) is gone.

---

## 5b. Attributes page: holding character stats (code hook)

Standard segment levels above 10 don't raise a prism bonus (see 5a; fusions do). To push any
single stat past normal limits, the **Attributes** page edits the character's computed stats, as
the CT's "Manage System Statistics" script does.

**Stat list**: `[Pawn+0x648]` StatsComponent → `+0x128` TArray (Num `+0x130`), element stride 0x14:
`+0x00` FName id, `+0x08` float value. About 210 stats (e.g. `WeakSpotDamageMod`, `CritDamageMod`,
`RangedDamageMod`, `MoveSpeedCap`, `SkillCooldownMod`).

The list isn't fixed. The game adds and removes rows at runtime without moving the array: in the
Game Pass test it went from 209 to 212 rows, `WeaponEquipSpeedMod` moved from index 85 to 109 and
`FogOfWarMod` disappeared. Stats are therefore keyed by FName id, never by index. Every direct write
first checks that the row still holds the expected id. If it doesn't, the list is re-read and the row
found again. Stats no longer in the list lose their address.

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
- **Support zip** (status bar) packs every session log, every diagnostic report, `settings.json` and a
  `summary.txt` of the current state (target, build fingerprint, link state, failed reads, prisms, hook)
  into `logs/Prismforge_Support_<time>.zip` and selects it in Explorer. The newest 3 are kept.
- Every attach logs the image path and a **build fingerprint** (file version, PE TimeDateStamp and
  SizeOfImage read from memory), so logs from different machines can be matched to a build.
- Processes whose name contains "Remnant" but isn't a known exe are logged, in case a store ships
  yet another exe name.

## 6b. Game Pass and untested builds

`Remnant2-WinGDK-Shipping.exe` (Game Pass) uses the same offsets and signatures as the Steam exe.
It started out marked untested; a tester confirmed reading, Apply and Hold on it (2026-09-27), so it
is now marked tested and writes are on by default.

The untested-build mode stays in the code for a future exe (`GameTarget.Tested = false`):

- **Read-only** by default: Apply and Hold are refused. `--allow-untested-writes` enables them.
- The log level drops to Debug automatically (every chain step, AOB hit, FName candidate).
- If the FNamePool still isn't found after 10 attach attempts, `Prismforge_Diag_<time>_attach.txt`
  is written: build header, module region map, and every signature (FNamePool, GEngine, both
  GUObjectArray variants, stat hook) with hit counts and the bytes around each hit.
- After the first successful scan a full chain report (`_chain.txt`, including the signature section)
  is written. If the chain fails 15 scans in a row, one is written with the failure reason instead.

What to ask a tester for: start the game, load a character, start Prismforge, open the Prisms and
Attributes pages, wait ~30 s, press **Support zip**, send the zip. If everything reads correctly,
a second run with `--allow-untested-writes` can check Apply and Hold on a backed-up save.