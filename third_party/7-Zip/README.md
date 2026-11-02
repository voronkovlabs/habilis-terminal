# 7-Zip 7z.dll

Archives as folders (issue #51) use 7-Zip's `7z.dll`, unmodified.

- Version: 26.04, from the official release https://github.com/ip7z/7zip/releases/tag/26.04
- Taken from the installers `7z2604-x64.exe` (SHA-256 `d54bf805f9f3704d1e8db2fa3498ae7ef2df0312b40b558e7c71c734430a665d`)
  and `7z2604-arm64.exe` (SHA-256 `d4117b95495b0e925334ae7b5bddf2853513274603de15dee6acf03999fbc09c`), checked against
  the release's published digests.
- `win-x64/7z.dll` SHA-256 `899cd73d7fc60e1c1c35c7de0f5bed3dc69bfe2cecbd48014d868a347970a0b1`
- `win-arm64/7z.dll` SHA-256 `eb84a6eb184c378d84742f372e7501d9a99c34a718ea81d4c71cd62b5a207dee`
- License: `License.txt` (GNU LGPL with the unRAR restriction, BSD); also in `ThirdPartyNotices.txt`.

`HabilisTerminal.App.csproj` copies the DLL that matches the build's runtime identifier next to `habi.exe`.
To update: download both installers from a new release, check their digests, extract `7z.dll` from each
(`7z e 7zXXXX-x64.exe 7z.dll`), replace the files here and update this README and the notices.
