# Archives

Opens archives as folders in a panel, on 7-Zip's `7z.dll` (`native/<arch>/`, licence in `third_party/7-Zip`).

- **Enter** on a `.zip`, `.7z`, `.rar`, `.tar`, `.gz`, `.bz2`, `.xz`, `.iso`, `.cab` and the like goes in.
- **Ctrl+PgDn** on any file tries every format 7-Zip reads.
- **F3** views, **F5** copies out, and where 7-Zip writes the format **F5 / F6** in, **F6** out, **F8** and **F7** change it.
- An archive inside an archive opens in place from a temporary copy; it is read-only.
- Encrypted archives ask for the password once per archive; it stays in memory.

Without `7z.dll` the plugin claims no files, and Enter opens them in their program.
