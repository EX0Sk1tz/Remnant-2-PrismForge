# Prismforge

**Live prism & stat editor for Remnant II.**

Edit your prisms and character attributes in **Remnant II while the game is running**.
No save-file juggling: change a value, press Apply, and it is live in the game.

**Tested on the Steam and Game Pass versions.**

---

## Features

**Prisms**
- Lists every prism on your character with its real name, level, segments and fed fragments.
- Change a segment's stat (all 45 standard stats, 23 fusions and 42 legendaries, with descriptions),
  its level, and the prism's pending XP.
- Edits are staged first and only written when you press **Apply to game**. Every write is read back
  to confirm it.
- **Discard** drops staged edits; **Revert to session start** restores what the prism looked like
  when the editor first saw it.
- Segment levels up to 100,000,000. **Fusion** bonuses keep scaling with level (Lv 20 = twice the
  Lv 10 bonus); **standard** segments stop gaining at level 10. Each segment row says which applies.
- **Extra segments (experimental)**: **Add segment** appends segments beyond the usual six into room
  the game reserved itself. **Make room** gets that room on a prism that already has its legendary
  (see Usage).
- Remove or reorder any segment or fed fragment, or **Reset prism** to a blank one.

**Attributes**
- Live list of about 220 character stats (damage, crit, weak spot, speeds, resistances, caps…).
- **Hold** keeps a stat at your value even when the game recalculates it (same technique as the
  well-known Cheat Engine table, but for any stat).
- Soft warnings for risky values, e.g. a stat above its game cap, chances above 100 %, reductions
  at 100 % or more. Nothing is blocked.

**Support**
- **Support zip** packs logs, diagnostic reports and a short state summary into one file for bug reports.

---

## Requirements

- Windows 10 or 11, 64-bit.
- Remnant II (Steam or Game Pass / Microsoft Store).
- Nothing else. The exe is self-contained, no .NET install needed, no admin rights needed.

## Installation

1. Unzip anywhere.
2. **Back up your saves**: copy `%USERPROFILE%\Saved Games\Remnant2` somewhere safe.
3. Start Remnant II and load a character.
4. Start `Prismforge.exe`. It finds the game on its own.

To uninstall, delete the exe. Settings and logs live in `%LocalAppData%\Prismforge`.

---

## Usage

**Prisms tab**
1. Pick a prism on the left.
2. Use **Change** to pick a new stat for a segment, − / + for its level, or type a new pending XP.
3. Staged changes are marked in amber. Press **Apply to game**.
4. **Unequip and re-equip the prism** in game: the game applies segment bonuses on equip.
5. The game saves your edits at its next autosave.

**Extra segments (experimental)**
- **Add segment** works at any time: if the prism's list is full, Prismforge first gives it more room
  through the game's own memory allocator. The steps below are only needed if that allocator isn't
  found (the Make room button then appears).
- The game reserves spare room in a prism's segment list only when it adds a segment itself (the
  legendary pick after a level-up), and only in that session. While there is room, **Add segment**
  is active.
- On a prism that already has its legendary: press **Make room** (the legendary is taken off), give
  the prism some pending XP and Apply, then pick the legendary again in game. The editor puts the
  other segments back and Add segment becomes active. Keep Prismforge open in between;
  **Restore legendary** undoes Make room until the pick.
- Hover a segment row: the arrows move it up or down, the bin icon removes it. Fed fragments have a
  bin icon too. These are written to the game right away (no Apply). A removed segment's slot is
  spare room, so Add segment works right after.
- **Reset prism** makes the prism blank again (no segments, no fed fragments, XP and level 0), for
  example to start over on a prism you changed days ago. It asks first.

**Attributes tab**
1. Search or filter the stats.
2. Type a value in *Hold at* and press **Hold**. The value is forced from now on.
3. **Release** (or **Release all**) gives control back to the game.

Examples: `WeakSpotDamageMod`, `CritDamageMod`, `RangedDamageMod`, `CritChance`.
To go faster than normal, raise `MoveSpeed` **and** `MoveSpeedCap`.

---

## Good to know

- **Back up your saves.** Edits end up in your save file.
- **Unusual values can crash the game.** Start moderate.
- **Held attributes stay active after closing the editor**, until you press Release all or restart
  the game. The editor picks them up again when you reopen it.
- **Legendary segment changes** take full effect after you reload your character.
- **Co-op:** extreme stats affect other players' games too. Be considerate.
- **Antivirus:** the editor reads and writes the game's memory and patches one instruction for
  Hold. Some antivirus tools flag any program that does this. The source code is linked on the mod page.
- If the Cheat Engine table's "System Statistics" script is active, disable it before using Hold.
- **Extreme fusion levels** (hundreds and up) give huge bonuses and can break the game, for example
  cooldowns below zero. Levels are saved with your character.

## Start options

Some features are switched on with a start option: a word you add after the program name when you
start it. Double-clicking `Prismforge.exe` starts it without options.

**How to start with an option (shortcut, once):**
1. Right-click `Prismforge.exe` → **Show more options** (Windows 11) → **Create shortcut**.
2. Right-click the new shortcut → **Properties**.
3. In **Target**, click at the very end, after the closing quote, add a space and the option, e.g.
   `"C:\Tools\Prismforge\Prismforge.exe" --verbose`
4. **OK**. From now on, start Prismforge with this shortcut when you want the option, and with the
   exe itself when you don't. Several options go one after another, separated by spaces.

**Or once, from a console:** Shift + right-click in the folder with `Prismforge.exe` →
**Open PowerShell window here** (Windows 11: right-click → **Open in Terminal**), then type
`.\Prismforge.exe --verbose` and press Enter.

| Option | What it does |
|---|---|
| `--verbose` | More detailed logs, useful for bug reports. |
| `--diagnose` | Writes a diagnostic report after the first scan. |

## Known limitations

- Fed-fragment editing is implemented but untested.
- Legendary segment levels above 1 count into the prism level; whether the effect scales is untested.
- Replacing a legendary that works through an action (e.g. Unbreakable) may keep the old effect
  until you reload.

## Troubleshooting

- **"Looking for Remnant 2"**: start the game.
- **"Waiting for character"**: load into the world; the main menu has no character.
- Anything else: press **Support zip** in the bottom bar and attach the zip to your bug report,
  together with what you did.

---

## Build from source

Requires the .NET 8 SDK on Windows.

- Debug build: `dotnet build Prismforge.sln`
- Release (self-contained exe + zips): `powershell -ExecutionPolicy Bypass -File build_release.ps1`

Technical details (pointer chain, memory layout, stat hook) are in `DOCUMENTATION.md`.

---

## Credits

- **Paul44**: Remnant 2 Cheat Engine table (pointer chain, prism layout, stat hook point).
- **kiamchyearktng**: R2PrismEditor save editor (prism segment data tables and names).
- **Andrew Savinykh, t1nky, crackedmind**: lib.remnant2.saves, the save parser R2PrismEditor is built on.

## License

Prismforge is released under the MIT License, © 2026 EX0Sk1tz. See `LICENSE.txt`.

Remnant II is © Gunfire Games, published by Gearbox Publishing. This is an unofficial fan tool, not
affiliated with or endorsed by them. Game data (stat names and descriptions) belongs to its rights
holders. Use at your own risk.
