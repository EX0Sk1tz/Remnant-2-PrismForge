# Changelog

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
