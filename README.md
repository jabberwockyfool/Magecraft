<p align="center">
  <img src="ICON.ico" alt="Magecraft" width="96">
</p>

# Magecraft

A Windows tool for translating MAGES. visual novels built on the SC3 engine
(CHAOS;CHILD and friends). It drives
[sc3tools](https://github.com/CommitteeOfZero/sc3tools) by the Committee of Zero for the
`.scx` ↔ text conversion, and carries its own MPK reader/writer (same format as
[SGOMPK](https://github.com/mrboms/SGOMPK) and `mpkpack.py`) so that building the archive
takes no manual step.

## Usage

Pick a mode at the top of the window, fill in the paths — they are remembered between
sessions — then press the left button (**Check**, **Preview** or **Extract**) and **Build**.

| Mode | For |
|---|---|
| **Scripts (.scx + text)** | SC3 games: CHAOS;CHILD and friends, text injected with sc3tools |
| **Files (CG, textures…)** | rebuilding an asset archive from a folder |
| **Scenario (PSB archive)** | M2 engine games: STEINS;GATE RE:BOOT and friends |

### Scripts mode

Injects translated text into `.scx` scripts with sc3tools, then packs them.

| Field | |
|---|---|
| **Script folder (.scx)** | the compiled scripts, e.g. `script-compile` |
| **Text folder (.scx.txt)** | the translated text, e.g. `script-decompile` |
| **Original archive** | optional; see *Entry order* below |
| **Archive to write (.mpk)** | the output, e.g. `languagebarrier\enscript.mpk` |
| **sc3tools.exe** | found automatically when it sits near the program |

A text file is matched to its script by name: `cc_01_01.scx` accepts both
`cc_01_01.scx.txt` and `cc_01_01.txt`. A script with no matching text is packed unchanged.

**Check** reports the problems listed further down, and **Fix characters** applies the safe
replacements straight into the text files.

Two options: **Also write the built .scx next to the archive**, which drops them into a
`compiled-scx` folder if you want to inspect or version them; and **Build even if some
scripts fail**, since by default a single compilation failure cancels the archive so you
never ship a half-translated patch without noticing.

### Files mode

Rebuilds an asset archive — CG, textures, `c0data` — straight from a folder. No text, no
sc3tools: every entry of the original archive is replaced by the folder's file of the same
name, and entries the folder does not provide are streamed back out of the original.

Here the **Original archive** is required, and **Preview** reports what would be replaced,
kept, and left out.

Your source files are never modified. An existing output archive is kept as `.mpk.bak`,
which is also what the build reads from when you write over the original in place.

### Entry order

The game addresses files by id, and those ids are not always a plain `0..n-1` run:
`c0data.mpk` holds 239 entries whose ids reach 374, in an order of their own. Such a table
cannot be derived from a folder listing, so Magecraft copies it from the original archive —
the same thing SGOMPK does when it repacks.

Script archives happen to be the simple case: sorted by name, numbered `0..n-1`. So in
Scripts mode the original archive is optional, and without it Magecraft numbers the entries
itself. Supply it anyway whenever you have it — it is the only way to be sure the table
matches, and it lets you rebuild while providing only some of the scripts.

A file the original archive does not list cannot simply be added: since entries are reached
by id, inventing one would not make the game load it. Such files are reported and left out.

### Scenario mode

For the M2 / PSB engine, which is a different beast entirely: no MPK, no sc3tools. An
archive is a pair `X_info.psb.m` (index) + `X_body.bin` (payload), both wrapped in `mzs`
containers — Zstandard compressed, then XOR-encrypted with an MT19937 keystream seeded
from a key found in the game executable. Magecraft drives
[FreeMote](https://github.com/UlyssesWu/FreeMote) for that part.

| Field | |
|---|---|
| **Workspace folder** | where Magecraft keeps its working files and your text |
| **Original archive (_info.psb.m)** | e.g. `scenario_info.psb.m`; the body must sit next to it |
| **Output folder** | both files are written there under the names the game expects |
| **FreeMote folder** | holding `PsbDecompile.exe` and `PsBuild.exe` |
| **Archive key** | for STEINS;GATE RE:BOOT: `Rk3nwA8ZYV0yV` |

**Extract** fills the workspace:

```
<workspace>/
    script-compile/    the archive, its scripts and their JSON   (managed by Magecraft)
    script-decompile/  one .txt per script — this is what you translate
    reference/         the same lines in Japanese, for context
```

(A workspace laid out with the older `archive/` and `text/` names keeps working.)

Each `.txt` holds **one line per dialogue entry**, so the line count must never change —
that is what maps a translation back to its slot. Pick which track you are writing with
**Language** (`en` overwrites the English one, which is usually what a translation wants
since the game already renders that font).

**Build** injects the text back, recompiles only the scripts that changed, and repacks the
archive. It writes the pair `<name>_info.psb.m` + `<name>_body.bin` into the output folder,
keeping those exact names -- the game looks them up by name, so they are not yours to
choose. Previous files there are kept as `.bak`.

The character counter each line carries is recomputed for you. It is not the raw string
length: markup like `%p;` and ruby annotations `[reading]kanji` do not count, a `
` counts
for nothing, and any other escape counts as one. That rule matches 98366 of the 98367
lines shipped in STEINS;GATE RE:BOOT.

> FreeMote cannot write `mzs` archives as released: `MzsShell.ToShell` forgets to rewind
> its stream before assembling the container, so it emits an 8-byte header and nothing
> else. Add `ms.Seek(0, SeekOrigin.Begin);` before the `shellMs` block and rebuild it —
> the fix is one line, and the sibling `MxbShell` already does exactly that.

## What Check looks for (Scripts mode)

The reference is the text actually held by the `.scx` files, read back with
`sc3tools extract-text`: the real structure of the game's scripts rather than a separate
export that may have drifted.

- **Line count** against the script — a mismatch shifts the whole file.
- **Characters the game refuses.** In CHAOS;CHILD, `œ Œ « » … Ÿ €` and the straight quote
  `"` are not in the charset, and sc3tools then rejects **the entire file**. Accented
  letters such as `à â ä ç é è ê ë î ï ô ö ù û ü ÿ` and curly quotes `“ ” ‘ ’` are fine.
  Suggested fixes: `œ → oe`, `« » → “ ”`, `… → ...`.
- **Lone carriage returns.** A `\r` without `\n` — invisible in an editor — counts as a
  text character and stops the whole file from compiling.
- **Tags.** An error when a structural tag disappears (`[%p]`, `[%e]`, `[name]`, `[line]`,
  `[evaluate]` — that is code, not text); a warning for layout tags (`[color]`,
  `[linebreak]`, `[margin]`), which a translation may legitimately redistribute.
- **Unbalanced brackets**, compared against the script so punctuation tables that already
  contain literal brackets are not flagged.
- **Lines to build**: the text differs from the script in place, so the next build will
  apply it.

Because the scripts were compiled from these same texts, a tag lost in translation is
already lost in the compiled script, so the comparison can no longer surface it. Catching
that would need a reference in the original language.

### Charset detection

The character table does live inside `sc3tools.exe`, but mixed together with the other
games': `…` appears there even though CHAOS;CHILD rejects it. Magecraft therefore asks
sc3tools itself, once per character, and caches the verdicts in `charset-cache.txt` next
to the executable. Delete that file to force a fresh detection, which is useful after
updating sc3tools.

## Building

```powershell
dotnet build -c Release
```

Output: `src\Magecraft\bin\Release\Magecraft.exe`. It targets .NET Framework 4.8, which
ships with Windows 10 and 11, so translators have nothing to install. The dotnet SDK is
enough to compile it; Visual Studio is not required.

## Credits

- **Committee of Zero** for `sc3tools` and their reverse engineering of the SC3 engine.
- **mrboms** for SGOMPK, used as a reference on the MPK format.
- **MAGES. Inc.** for the original games.

A fan-made tool, not affiliated with MAGES.
