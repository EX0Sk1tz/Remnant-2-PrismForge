# Changelog

## 2.3.0

- **Mod support (Beyond Hell).** Prismforge now reads the prism tables from the running game. Segments
  a mod adds (such as Beyond Hell's new fusions and legendaries) can be picked, show their in-game
  names and descriptions, and are tagged with the mod's name. Legendaries a mod replaces or switches off
  are shown correctly, and switched-off ones are no longer offered. Without mods nothing changes; if the
  tables can't be read, the built-in segment list is used as before.
- **Legendary slots can take any segment.** A legendary can now be changed to a fusion or a single
  stat (and back), like any other segment.
- **Search in the segment picker.** Type to filter by name or effect (e.g. "crit", "grey health",
  "beyond hell"); every word must match. Down moves into the list, Enter takes the first match.

## 2.2.1

- **Legendaries in normal slots.** The segment picker of a normal slot now lists legendary segments too
  (grouped last), so any segment can be turned into a legendary. Legendary slots still offer only
  legendaries. The gold label and the legendary note follow the segment you picked, before Apply.

## 2.2.0

- **Build codes.** Plan a prism on the new [Prismforge Planner](https://ex0sk1tz.github.io/prismforge-planner/)
  web page and import it: paste the code under **Build code** on the Prisms page and press **Import**.
  The prism's segments, fed fragments and (if the code has one) pending XP are replaced; Prismforge
  shows the content and asks first. **Copy code** turns an existing prism into a code.

## 2.1.0

- **Add segment works on any prism, any time.** When the list is full, Prismforge first gives it a
  bigger buffer from the game's own memory allocator, so no Make room, XP or legendary pick is needed.
  Make room is only shown if the allocator can't be found. Tested on Steam: growing, save and reload,
  and building a reset prism up again from 0 segments.
- **Remove any segment**, not only ones added in the current session: hover a segment row and press the
  bin icon. Arrows next to it move the segment up or down. Both are written to the game right away.
  A removed segment's slot becomes spare room, so **Add segment** works right after.
- Fed fragments can be removed the same way (bin icon on the card; untested so far).
- **Reset prism** turns a prism back into a blank one: no segments, no fed fragments, XP and level 0.
  It asks first, and the old entries are written to the log. The freed slots stay available for
  Add segment in that session. After a reset the game offers normal picks from level 1 again.
- Tested on Steam: moving, removing (including an equipped legendary) and reset persist through
  save and reload.
- **Remove added** is replaced by the above.

## 2.0.0

- **Game Pass / Microsoft Store version supported.** Reading, Apply and Hold were confirmed by a tester.
- New **Support zip** button: packs logs, diagnostic reports and a state summary into one file to send.
- Logs now include the exact game build, install path, admin rights and command-line options.
- Only one Prismforge can run at a time, so two copies can't overwrite each other's changes in the game.
- Fix: writing an attribute could hit the wrong stat when the game had added or removed stats since the
  last refresh. Each write now checks the stat's name first and finds its new position if it moved.
- Segment levels can go up to 100,000,000 (was 999). Fusion bonuses keep scaling with level; standard
  segments stop gaining at level 10, and each segment row now says which applies. Very high fusion
  levels show a warning.
- After Apply the status bar reminds you to re-equip the prism: the game applies segment bonuses on equip.
- Experimental **Add segment**: appends segments into spare room the game reserved after adding a
  segment itself (for example after the legendary pick). Segments added this way can be removed again.
  **Make room** takes the legendary (and any segments beyond the fifth) off the list so the game
  re-adds it with spare room; the extra segments are put back automatically afterwards. **Restore
  legendary** undoes that until the new pick.
- Fix: the Change menus stayed empty when Prismforge connected while the game was still loading.

## 1.0.0 (first public release)

- Prisms: live list with names, levels, segments and fed fragments; change segment stats (standard,
  fusion, legendary) and levels, pending XP; staged edits with Apply, Discard and Revert to session
  start; read-back check after every write.
- Attributes: live list of the character's computed stats with search and filters; Hold any stat via
  a code hook at the game's stat store; hold survives closing the editor; soft warnings for risky values;
  hover any attribute for a short explanation.
- Connects automatically, follows character changes, detects the game closing.
- Self-contained single exe, no admin rights, settings and logs in %LocalAppData%\Prismforge.
- Tested on the Steam version.
