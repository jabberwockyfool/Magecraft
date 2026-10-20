# Magecraft

A Windows tool for translating MAGES. visual novels built on the SC3 engine
(All the games released after S;G). It drives
[sc3tools](https://github.com/CommitteeOfZero/sc3tools) by the Committee of Zero for the
.scx to text conversion, and carries its own MPK reader/writer (same format as
[SGOMPK](https://github.com/mrboms/SGOMPK) and mpkpack.py) so that building the archive
takes no manual step.

## Building

```powershell
dotnet build -c Release
```

Output: `src\Magecraft\bin\Release\Magecraft.exe`. It targets .NET Framework 4.8

## Credits

- **Committee of Zero** for `sc3tools` and their reverse engineering of the SC3 engine.
- **mrboms** for SGOMPK, used as a reference on the MPK format.
- **MAGES. Inc.** for the original games.

## Notes

Contributions are welcome. Using AI is fine sometimes, but the code must always be reviewed by a human. Code that is entirely vibecoded will be discarded.
