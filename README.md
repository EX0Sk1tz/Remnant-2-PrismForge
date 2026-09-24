# Prismforge

**Live prism & stat editor for Remnant II.**

Edit your prisms and character attributes in **Remnant II while the game is running**.
No save-file juggling: change a value, press Apply, and it is live in the game.

**Tested on the Steam version.** The Game Pass version is not supported yet.

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

**Attributes**
- Live list of about 220 character stats (damage, crit, weak spot, speeds, resistances, caps…).
- **Hold** keeps a stat at your value even when the game recalculates it (same technique as the
  well-known Cheat Engine table, but for any stat).
- Soft warnings for risky values, e.g. a stat above its game cap, chances above 100 %, reductions
  at 100 % or more. Nothing is blocked.

Segment levels above 10 do **not** raise a prism bonus; the game stops at level 10. Use Attributes
to go beyond normal limits.

---

## Requirements

- Windows 10 or 11, 64-bit.
- Remnant II (Steam).
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
4. The game saves your edits at its next autosave.

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

## Known limitations

- Game Pass / Microsoft Store version not supported.
- Fed-fragment editing is implemented but has not been tested on a prism that has fed fragments.
- Replacing a legendary that works through an action (e.g. Unbreakable) may keep the old effect
  until you reload.

## Troubleshooting

- **"Looking for Remnant 2"**: start the game (Steam version).
- **"Waiting for character"**: load into the world; the main menu has no character.
- Anything else: press **Diagnostic** in the bottom bar and attach the report from **Logs**
  to your bug report, together with what you did.

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
