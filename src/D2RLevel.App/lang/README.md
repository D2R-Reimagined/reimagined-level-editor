# Translating the editor

Every piece of text the editor shows comes from this folder. Nothing needs to be compiled to
change a translation: edit a file, restart the editor, and pick the language under
**View → Language**.

`en.json` is generated from the source code on every build. It lists every string the editor can
show with an empty value — the **key is the English text**, and English is what the editor shows
whenever a translation is blank or missing.

## Shipped languages

| File | Language | Status |
|------|----------|--------|
| `de.json` | Deutsch | machine-drafted, needs native review |
| `es.json` | Español | machine-drafted, needs native review |
| `fr.json` | Français | machine-drafted, needs native review |
| `ko.json` | 한국어 | machine-drafted, needs native review |
| `pt-br.json` | Português (Brasil) | machine-drafted, needs native review |
| `ru.json` | Русский | machine-drafted, needs native review |
| `zh-hans.json` | 简体中文 | machine-drafted, needs native review |

These were produced by machine translation to give every user something readable on day one.
Each carries a `"$note"` saying so. Corrections from native speakers are the most valuable
contribution here — a pull request that fixes wording in one of these files is very welcome, and
you can delete the `$note` once a language has been reviewed.

## Improve a language

Open the file, change values, keep keys exactly as they are. Test by copying the file into the
`lang` folder next to `D2RLevel.App.exe` (**View → Language → Open language folder…** takes you
there) and restarting; `--language de` on the command line forces a language for one run.

## Add a language

1. Copy `en.json` to `<code>.json`, where `<code>` is the IETF language tag in lower case —
   `it.json`, `pl.json`, `ko.json`, `zh-hant.json`.
2. Set `"$language"` to the language's own name as it should appear in the menu, e.g. `"Polski"`.
3. Fill in values. Leave any you have not done as `""`.

```json
{
  "$language": "Polski",
  "Lock terrain": "Zablokuj teren",
  "Moved {0} assets together. …": "Przeniesiono {0} obiektów razem. …",
  "Open preset…": ""
}
```

## Rules that keep a file working

- Keep every `{0}`, `{1}`, `{0:F1}` placeholder the English text has. You may reorder them
  (`"{1} of {0}"`) but not add or drop one. The editor falls back to English for an entry whose
  placeholders differ, and the checks refuse the file.
- `_` before a letter in a menu header (`"_File"`) marks the Alt shortcut key. Put it before a
  letter in your translation (`"_Datei"`, `"文件(_F)"`), or leave it out if no letter fits.
- `\n` is a line break; `·` is a plain separator used in status text.
- Save as UTF-8. Comments (`// …`) and trailing commas are tolerated.
- Keys beginning with `$` are metadata (`$language`, `$note`, …) and are carried through untouched.

Untranslated or unknown entries never break the editor; a file that does not parse is reported at
startup and the editor stays in English.

## Send it in

Open a pull request that adds or changes only your `lang/<code>.json`. CI runs the same check you
can run locally:

```powershell
dotnet run --project tests/D2RLevel.Tests -- --strings-audit
```

It reports coverage per language and fails on entries the app no longer uses, placeholder
mismatches, non-string values or a missing `$language`.

## For developers: when English text changes

Wrap UI text with `L.T("…")` in C# and `{l:T '…'}` in XAML, passing a plain literal with `{0}`
placeholders rather than `$"…"` interpolation.

Building `D2RLevel.App` regenerates `en.json` from those literals and updates every other
language file: new strings are appended with `""`, and entries for text that no longer exists are
removed (the build log lists what was dropped, so a reworded string can be re-translated from the
diff). **Commit the regenerated `lang/` files with your change** — CI fails if a build would
change them. `dotnet run --project tests/D2RLevel.Tests -- --strings-update` does the same
without a full build, and `-p:UpdateTranslations=false` skips the step.
