# Archives

Opens archives as folders in a panel, on 7-Zip's `7z.dll` (`native/<arch>/`, licence in `third_party/7-Zip`).

- **Enter** on a `.zip`, `.7z`, `.rar`, `.tar`, `.gz`, `.bz2`, `.xz`, `.iso`, `.cab` and the like goes in.
- **Ctrl+PgDn** on any file tries every format 7-Zip reads.
- **F3** views, **F5** copies out, and where 7-Zip writes the format **F5 / F6** in, **F6** out, **F8** and **F7** change it.
- An archive inside an archive opens in place from a temporary copy; it is read-only.
- Encrypted archives ask for the password once per archive; it stays in memory.
- **Alt+F7** with "Search in archives" looks inside (SDK 1.7). Each entry is unpacked to a temporary file when its text is
  searched: quick for zip, slower for solid 7z. Encrypted entries are found by name only, as a search never asks for a password.

Without `7z.dll` the plugin claims no files, and Enter opens them in their program.

**On a Mac** the plugin uses the system's own libarchive (`/usr/lib/libarchive.2.dylib`, the library behind `tar`)
instead, so nothing is shipped or downloaded (#281). It reads zip, 7z, rar, tar with gz / bz2 / xz / zst, cab, lha,
cpio, iso, xar and more, and a lone compressed file (`log.gz`) shows the file inside. F5 / F6 in, F8 and F7 change
zip, 7z and tar (plain, gz, bz2 or xz), as on Windows (#285). An archive with encrypted entries is only read, and an
encrypted 7z or rar does not open (libarchive does not decrypt them; encrypted zip works).
