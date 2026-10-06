v1.1.2 - documentation: the lessons-learned file.

### Added

- `references/lessons-learned.md` - every mistake made while building this project,
  grouped by kind:

  **Conclusions that turned out to be wrong** (these cost the most time):
  - A cleanup reported success while deleting nothing, because the lookup used an exact
    key name that did not match the real, space-padded key.
  - A failed `reg import` was blamed on the Chinese comments in the `.reg` file. A
    controlled test disproved it: ASCII-only, with Chinese comments, and with `*` plus a
    padded key name all imported fine. The real failure was in our own test command.
  - Three successive wrong root causes were offered for one "my input was ignored"
    symptom, before instrumentation showed the tool had been working all along and simply
    required an explicit `YES` confirmation.

  **Technical bugs**: `Registry.OpenBaseKey` not existing on Windows PowerShell 5.1,
  `\s` collapsing inside double-quoted paths, an invalid `[regex]('\u...')` pattern,
  `ConvertFrom-Json` neither decoding `\uXXXX` nor enumerating arrays on 5.1,
  `ConvertTo-Json -InputObject` expanding arrays, a licence file whose appended notice
  broke GitHub's MIT detection, and an HTTP 400 from `Invoke-RestMethod` once a request
  body passed roughly 75 KB.

  **Omissions and process mistakes**: a keyword list that missed a whole extension,
  an undocumented confirmation step, two avoidable credential round-trips, testing an
  interactive program without a timeout, and a stale process mistaken for a file lock.

- Both READMEs and `SKILL.md` now link to it.

### Notes

- No code change. The executable and scripts are identical to v1.1.1.
- If a description in this repository disagrees with the code, the lessons file is the
  place that explains why the mistake was made and what the correct behaviour is.
