# dsh-chemdraw

Read and **safely write** ChemDraw `.cdx` files from DeepSeek Harness: structures, stereo
geometry, captions, reaction arrows, plus 3D conformers — 12 tools in total.

```
ChemDraw (installed & activated, hidden window)
        │  COM automation
        ▼
ChemBridge.exe   ──  C# bridge: CDX ⇄ CDXML ⇄ SDF, geometry, verification, backups
        │  ChemScript .NET (chemistry engine: SMILES / InChI / IUPAC name / 3D)
        ▼
dsh-chemdraw (index.js)  ──  12 tools
```

## Requirements

| Item | Notes |
|---|---|
| Windows | The bridge is a .NET Framework console program |
| ChemDraw, installed and activated | A hidden process is started per call (~1 s cold start) |
| `CambridgeSoft.ChemScript.dll` | Ships with ChemDraw, **not** redistributed here — copy it next to `bin/ChemBridge.exe` from your own installation |
| .NET Framework 4.x + `csc.exe` | Only needed to rebuild the bridge |

Install this plugin, then put your own `CambridgeSoft.ChemScript.dll` in `bin/`. The plugin
writes its runtime cache (CDXML, page PNG, per-structure thumbnails) under `var/`, which is
git-ignored.

Rebuild the bridge from source (no network needed):

```powershell
powershell -ExecutionPolicy Bypass -File bridge/build.ps1
```

## Tools

| Tool | What it does | Writes? |
|---|---|---|
| `chemdraw_inspect` | Structure list (SMILES/InChI/formula/MW/IUPAC name) + 2D geometry + stereo checks + captions + reaction arrows + thumbnails | no |
| `chemdraw_find` | Find structures by label / id / formula / SMILES fragment; returns ids and thumbnails (use it to disambiguate duplicate labels) | no |
| `chemdraw_render` | Whole-page PNG plus one cropped image per structure | no |
| `chemdraw_3d` | MM2 conformer, planarity RMSD, largest torsion, molecular span | no |
| `chemdraw_add` | Add a structure from SMILES (layout avoids existing ones, optional caption) | yes (dry-run by default) |
| `chemdraw_delete` | Delete a structure and its own captions; `nth` picks among duplicate labels | yes (dry-run by default) |
| `chemdraw_replace` | Replace a structure in place and refresh its Chemical Formula / Molecular Weight captions | yes (dry-run by default) |
| `chemdraw_annotate` | Add a text caption (never reads "B1" as boron) | yes (dry-run by default) |
| `chemdraw_move` | Move a structure together with its captions | yes (dry-run by default) |
| `chemdraw_add_arrow` | Add a reaction arrow | yes (dry-run by default) |
| `chemdraw_edit` | Batch transaction: add / delete / replace / annotate / move / arrow in one commit | yes (dry-run by default) |
| `chemdraw_doctor` | Check installation, activation and engine self-test | no |

After a write, every change reports `id_after_save` (the id after ChemDraw re-saved the file)
and `id_match` (`position` / `inchi` / `inchi_ambiguous`). ChemDraw reorders fragments when it
saves, so **inspect again after writing, or address structures by `label`**.

## Write safety model

1. **Dry-run by default** — nothing is written until `dry_run: false`.
2. **Verify, then save** — the edit is written to a temporary `.cdx`, read back, and every
   structure is compared by InChI multiset (or a structural key when InChI is unavailable).
   A mismatch aborts and leaves the original untouched.
3. **Automatic backup** — `xxx.cdx.bak.<timestamp>`, only the latest 3 are kept.
4. **Atomic replace** — temporary file in the same directory + `File.Replace`.
5. **Never touches other people's text** — only captions that belong to the edited structure
   are updated (position-based, one-to-one pairing).

## Known limitations

1. **Write scope** — the DSH sandbox lets a session write inside its workspace. Editing a `.cdx`
   outside it is rejected; copy the file into the workspace or run with full access.
2. **Ids change** — ChemDraw reorders fragments on save. Re-inspect after writing; deleting by
   `label` is more stable.
3. **No InChIKey** — ChemScript does not provide one and this plugin does not reimplement the
   hash (better absent than wrong). Use SMILES/InChI for lookups; PubChem accepts both.
4. **Unrecognised abbreviations** are treated as elements; the `formula_unexpected` warning
   points at them and a human has to confirm.
5. **Caption refresh is bounded** — only the value after `Chemical Formula:` / `Molecular
   Weight:` is rewritten; a hand-typed compound name line is left alone.
6. **ChemDraw must be installed and activated** — a license dialog can block the hidden process
   and the call will time out with the innermost COM error.

## Install

```sh
dsh plugin add github:CGH999/dsh-chemdraw
```

Or add it from **Plugins → Add plugin** with the repository URL.

## Licence

MIT. ChemDraw and ChemScript are CambridgeSoft/Revvity products and are **not** included here.
