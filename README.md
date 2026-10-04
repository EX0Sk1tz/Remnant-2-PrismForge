# Prismforge

**A live prism & stat editor for Remnant II.**

Hi Traveler, and welcome! ♡

Prismforge lets you shape your prisms and character attributes **while Remnant II is running**.
Change a value, press Apply, and it's live in your game. No save-file juggling, no restarting.
Just you, your build and a little bit of magic.

**Tested on the Steam and Game Pass versions.**

---

## What you can do

**Prisms**
- See every prism on your character with its real name, level, segments and fed fragments.
- Change any segment to any of the 45 standard stats, 23 fusions or 42 legendaries, each with its
  in-game name and description.
- **Every slot takes every segment**: put a legendary in a normal slot, or a fusion in your legendary
  slot. It's your prism, after all.
- **Search the segment picker**: just type "crit", "grey health" or whatever you're looking for.
  Every word has to match; Enter takes the first result.
- Adjust segment levels (up to 100,000,000) and the prism's pending XP. **Fusion** bonuses keep
  growing with level (Lv 20 = twice the Lv 10 bonus); **standard** segments stop at level 10. Each
  segment row tells you which applies.
- **Add segments** beyond the usual six, any time. When a prism is full, Prismforge gently makes room
  using the game's own memory allocator.
- Remove or reorder any segment or fed fragment, or **Reset prism** to a blank one and build it up
  again from level 0.

**Build codes & the Prismforge Planner**
- Dream up your prism on the [Prismforge Planner](https://ex0sk1tz.github.io/prismforge-planner/)
  web page, copy the code, and import it in one step.
- **Copy code** turns any of your prisms into a code, perfect for sharing your favourite build.

**Presets**
- Save your favourite prism layouts under a name ("Mod focus", "Ranged focus", …) and put one on any
  prism with a single click, on every character.

**Mod support**
- Prismforge reads the prism tables straight from your running game, so segments that mods add (like
  **Beyond Hell**'s new fusions and legendaries) show up in the picker with their names, descriptions
  and a little tag for the mod.
- Playing without mods? Nothing changes for you at all.

**Attributes**
- A live list of about 220 character stats: damage, crit, weak spot, speeds, resistances, caps and more.
- **Hold** keeps a stat at your value, even when the game recalculates it (the same technique as the
  well-known Cheat Engine table, but for any stat).
- Soft warnings for risky values, like a stat above its game cap, chances above 100 % or reductions
  at 100 % or more. Nothing is ever blocked; they're just there to look out for you.

**Made to feel safe**
- Your edits are staged first and only written when you press **Apply to game**. Every write is read
  back and confirmed.
- **Discard** drops staged edits; **Revert to session start** brings the prism back to how it looked
  when Prismforge first saw it.
- **Support zip** packs logs, diagnostic reports and a short summary into one file, so reporting a bug
  is easy.

---

## What you need

- Windows 10 or 11, 64-bit.
- Remnant II (Steam or Game Pass / Microsoft Store).
- That's it! The exe is self-contained: no .NET install and no admin rights needed.

## Getting started

1. **Back up your saves** first, please: copy `%USERPROFILE%\Saved Games\Remnant2` somewhere safe.
2. Unzip Prismforge anywhere you like.
3. Start Remnant II and load into the world with your character.
4. Run `Prismforge.exe`. It finds the game on its own.

To uninstall, simply delete the exe. Settings and logs live in `%LocalAppData%\Prismforge`.

---

## How to use it

**Prisms tab**
1. Pick a prism on the left.
2. Use **Change** to pick a new stat for a segment, − / + for its level, or type a new pending XP.
3. Your staged changes glow amber. Press **Apply to game** when you're happy.
4. **Unequip and re-equip the prism** in game: that's when the game applies segment bonuses.
5. The game saves your edits at its next autosave.

**Extra segments (experimental)**
- **Add segment** works any time: if the prism's list is full, Prismforge first gives it more room
  through the game's own memory allocator. The steps below are only needed if that allocator can't be
  found (the **Make room** button then appears).
- The game only reserves spare room in a prism's segment list when it adds a segment itself (the
  legendary pick after a level-up), and only in that session. While there is room, **Add segment**
  is active.
- On a prism that already has its legendary: press **Make room** (the legendary is taken off), give
  the prism some pending XP and Apply, then pick the legendary again in game. Prismforge puts the
  other segments back and Add segment becomes active. Keep Prismforge open in between;
  **Restore legendary** undoes Make room until the pick.
- Hover a segment row: the arrows move it up or down, the bin icon removes it. Fed fragments have a
  bin icon too. These are written to the game right away (no Apply needed). A removed segment's slot
  becomes spare room, so Add segment works right after.
- **Reset prism** makes the prism blank again (no segments, no fed fragments, XP and level 0), for
  example to start fresh on a prism you changed days ago. It always asks first.

**Build codes**
- Build a prism on the [Prismforge Planner](https://ex0sk1tz.github.io/prismforge-planner/) (or press
  **Open planner**) and copy its build code.
- In Prismforge, pick the prism, paste the code under **Build code** at the bottom and press **Import**.
  Prismforge shows you what the code contains and asks first, then replaces the prism's segments and
  fed fragments (and its pending XP, if the code has one). Full lists get more room automatically.
- **Copy code** copies the selected prism as a build code, to share it or keep editing it on the
  planner page.

**Presets**
- Set a prism up just the way you like it, type a name under **Presets** and press **Save current**.
  The preset keeps the prism's segments and fed fragments as they are in the game (so apply any staged
  edits first).
- Pick any prism, on any character, and press **Load** next to a preset. Its segments and fed
  fragments replace the prism's right away, without asking; pending XP stays as it is. Then unequip
  and re-equip the prism in game.
- Loaded the wrong one? No worries: **Undo** (top right of the section) writes the previous layout back.
- Hover a preset to rename it, delete it or copy it as a build code. **Save as preset** next to Import
  keeps a pasted build code as a preset.
- Presets live in `%LocalAppData%\Prismforge\presets.json`; copy that file to keep them safe or take
  them to another PC.

**Attributes tab**
1. Search or filter the stats.
2. Type a value in *Hold at* and press **Hold**. Your value is kept from now on.
3. **Release** (or **Release all**) gives control back to the game.

Some favourites to try: `WeakSpotDamageMod`, `CritDamageMod`, `RangedDamageMod`, `CritChance`.
Want to run faster than normal? Raise `MoveSpeed` **and** `MoveSpeedCap`.

---

## A few kind reminders

- **Please back up your saves.** Your edits end up in your save file.
- **Unusual values can crash the game.** Start gently and work your way up.
- **Held attributes stay active after you close the editor**, until you press Release all or restart
  the game. Prismforge picks them up again when you reopen it.
- **Legendary segment changes** take full effect after you reload your character.
- **Co-op:** extreme stats affect your friends' games too, so please be considerate. ♡
- **Antivirus:** Prismforge reads and writes the game's memory and patches one instruction for Hold.
  Some antivirus tools flag every program that does this, so a warning doesn't mean anything is
  wrong. All the source code is right here if you'd like to take a look.
- If the Cheat Engine table's "System Statistics" script is active, turn it off before using Hold.
- **Very high fusion levels** (hundreds and up) give huge bonuses and can break things, like
  cooldowns below zero. Levels are saved with your character.

## Start options

A few extras are switched on with a start option: a word you add after the program name when you
start it. Double-clicking `Prismforge.exe` starts it without any options.

**With a shortcut (set it up once):**
1. Right-click `Prismforge.exe` → **Show more options** (Windows 11) → **Create shortcut**.
2. Right-click the new shortcut → **Properties**.
3. In **Target**, click at the very end, after the closing quote, add a space and the option, e.g.
   `"C:\Tools\Prismforge\Prismforge.exe" --verbose`
4. **OK**. Now start Prismforge with this shortcut when you want the option, and with the exe itself
   when you don't. Several options go one after another, separated by spaces.

**Or just once, from a console:** Shift + right-click in the folder with `Prismforge.exe` →
**Open PowerShell window here** (Windows 11: right-click → **Open in Terminal**), then type
`.\Prismforge.exe --verbose` and press Enter.

| Option | What it does |
|---|---|
| `--verbose` | More detailed logs, handy for bug reports. |
| `--diagnose` | Writes a diagnostic report after the first scan. |

## Known limitations

- Editing fed fragments works but hasn't been fully tested yet.
- Legendary segment levels above 1 count toward the prism level; whether the effect itself scales is
  still untested.
- Replacing a legendary that works through an action (e.g. Unbreakable) may keep the old effect
  until you reload.

## If something goes wrong

- **"Looking for Remnant 2"**: start the game.
- **"Waiting for character"**: load into the world; the main menu has no character yet.
- Anything else: I'd love to help! Press **Support zip** in the bottom bar and attach the zip to your
  bug report, together with a few words about what you did.

---

## Build from source

You'll need the .NET 8 SDK on Windows.

- Debug build: `dotnet build Prismforge.sln`
- Release (self-contained exe + zips): `powershell -ExecutionPolicy Bypass -File build_release.ps1`

Curious about the technical side (pointer chain, memory layout, stat hook)? It's all in
`DOCUMENTATION.md`.

---

## Thank you

Prismforge wouldn't exist without these wonderful people:

- **Paul44**: Remnant 2 Cheat Engine table (pointer chain, prism layout, stat hook point).
- **kiamchyearktng**: R2PrismEditor save editor (prism segment data tables and names).
- **Andrew Savinykh, t1nky, crackedmind**: lib.remnant2.saves, the save parser R2PrismEditor is built on.

## License

Prismforge is released under the MIT License, © 2026 EX0Sk1tz. See `LICENSE.txt`.

Remnant II is © Gunfire Games, published by Gearbox Publishing. This is an unofficial fan tool, not
affiliated with or endorsed by them. Game data (stat names and descriptions) belongs to its rights
holders. Use at your own risk.

Happy forging! ♡
