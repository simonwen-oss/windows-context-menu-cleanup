# Lessons learned

Every entry below was hit for real while building this project. They are grouped by what
went wrong: **false conclusions** (claims made without verification), **technical bugs**,
and **omissions** that cost the most time.

If you are extending these scripts, read the B group before writing any PowerShell that
touches the registry.

---

## A. Conclusions that turned out to be wrong

These are recorded first because they cost far more time than any code defect. Each one is
a case of **reasoning presented as a finding**.

### A1. "Deleted successfully" — with zero bytes deleted

The very first cleanup reported success. It had deleted nothing, because the lookup used
an exact key name (`YunShellExt`) that did not match the real key (see B group, padded key
names). `Remove-Item` raised no error, so the run looked fine.

**How it was caught:** re-enumerating the container and noticing the key was still there.

**Rule:** for a destructive operation, re-read the target and prove it is gone. An exit
code of 0 is not evidence.

### A2. "Chinese comments break `reg import`"

An import reported success while creating no key, and the `.reg` file had Chinese comments.
That looked like the cause and was written up as such. A controlled test disproved it:

| Test file | Result |
|---|---|
| plain ASCII, no comments | imported fine |
| **with Chinese comments**, same encoding | **imported fine** |
| `*` in the path plus a 4-space-padded key name | imported fine |

The real failure was a broken test command of ours, not the file.

**Rule:** when a test fails, suspect the test before the subject. Run a control.

### A3. Three wrong root causes for the same "input ignored" symptom

A user reported that typing `4` at an interactive prompt did nothing. Three successive
explanations were offered, all wrong:

1. "stdin is not readable in your environment" — wrong, `--stdin-info` showed a real TTY.
2. "launching via `cmd` is the problem" — wrong, it reproduced everywhere.
3. "the executable is locked by a running instance" — wrong; a leftover elevated process
   was simply running the *old* build.

The actual cause: after the numbers were accepted, the program asked for a `YES`
confirmation, and the user did not know it had to be typed. The tool was working the whole
time.

**How it was caught:** the user supplied the output of `--stdin-info` and then a trace log.

**Rule:** when a symptom is not reproducible locally, instrument the program and ask for
the data. Do not offer a sequence of guesses.

### A4. Trusting a contradictory observation instead of explaining it

`reg delete /reg:64` reported "the system was unable to find the specified registry key"
while the key had in fact just been removed. The contradiction was reported rather than
resolved. It originated in the harness: the PowerShell registry provider treats `*` in a
path as a wildcard, and `New-Item -Path '...\Classes\*\...\<name>'` silently concatenated
two test key names into one. The test data was invalid, not the command.

**Rule:** an observation that contradicts the model means the model (or the observation)
is wrong. Find out which before drawing a conclusion.

---

## B. Technical bugs

### B1. `Registry.OpenBaseKey` does not exist in Windows PowerShell 5.1

It is a .NET Core / PowerShell 7 API. Called from 5.1 it throws `MethodNotFound` **on the
first line**, so the script died before doing anything — and because it died after killing
Explorer in one variant, it once left a blank desktop.

Consequences: the scripts read through the provider and delete through `reg.exe`, and the
native executable exists partly to avoid this whole class of problem.

**Rule:** check `$PSVersionTable.PSEdition` before using any registry API. `Desktop` is 5.1.

### B2. Developing against the wrong PowerShell

The scripts were written and smoke-tested under PowerShell 7 while the target machine runs
5.1. This is the root cause of B1 and was entirely a process failure, not a knowledge gap.

**Rule:** test in the oldest environment you claim to support.

### B3. `\s` inside a PowerShell double-quoted string

`$sub = $cls + "\shellex\ContextMenuHandlers"` produced `*shellex...` with the `\s`
collapsed, so every backup and delete addressed a nonexistent path. Occurred twice.

**Fix:** build paths from `[char]92` or use `-join`, and prefer `-LiteralPath`.

### B4. `[regex]('\u([0-9a-fA-F]{4})')` throws

Inside a regex, `\u` must be followed by exactly four hex digits, so the pattern itself is
invalid: "hexadecimal digits are insufficient". The escape decoder needs a **doubled**
backslash: `[regex]('\\u([0-9a-fA-F]{4})')`.

### B5. `ConvertFrom-Json` does not decode `\uXXXX` on PowerShell 5.1

CJK patterns loaded from `vendors.json` silently never matched, because the escape
sequences stayed literal text. The scripts now decode them explicitly at load time.

### B6. `ConvertFrom-Json` emits a JSON array as ONE object

On 5.1, `@($raw | ConvertFrom-Json)` nests the whole array in a single element, so 84
entries became 1. Read it into a variable and iterate that.

### B7. `ConvertTo-Json -InputObject $array` expands the array into arguments

The result was a single object with every property concatenated. Cast to `[object[]]`, or
pipe it.

### B8. Assigning to `$args`

`$args` is an automatic variable in PowerShell. The request helper now uses `$req`.

### B9. A self-exclusion rule that excluded the deliverable

`publish.ps1` skipped any file whose name matched `publish*.ps1`, which excluded
`publish.ps1` itself from the upload.

### B10. A hard-coded release title

The release was created with `name = "v1.0.0"` regardless of the tag, producing a release
titled "v1.0.0" carrying the `v1.1.0` tag. Use the tag variable.

### B11. Wrong path for the release notes

`Join-Path $root 'release-notes.md'` pointed at the repository root while the file lives in
`src/`. Use `$PSScriptRoot`.

### B12. Appending text to the MIT licence broke licence detection

A custom risk paragraph after the standard MIT text made GitHub report
**`NOASSERTION / Other`** instead of MIT, while the README claimed MIT. The `LICENSE` file
must contain the standard text verbatim; per-project notices belong in the README.

### B13. `Invoke-RestMethod` fails on large string bodies

Uploading a ~57 KB source file (roughly 75 KB of base64 in the JSON body) returned
**HTTP 400 "malformed request"**. The identical request sent through `HttpWebRequest` with
explicit UTF-8 bytes and an explicit `ContentLength` succeeded. `publish.ps1` now does that
for every request.

### B14. Editing a closing brace away

A textual edit removed a `}`, which the compiler caught immediately. Harmless, but a
reminder that large source files need a compile after every structural edit.

---

## C. Omissions and process mistakes

### C1. A missing detection pattern hid a whole extension

The cleanup searched for `baidu`, `thunder` and `yun`, but Baidu Netdisk's second
mechanism is registered as `YunShellExt`, which those patterns missed. The removal
therefore looked complete while the menu item stayed.

**Rule:** enumerate and eyeball the container; never assume your keyword list is exhaustive.

### C2. Not documenting the confirmation step

The interactive flow lists components, asks for numbers, and then requires an upper-case
`YES`. That last step was not documented, and its absence produced four rounds of "typing 4
does nothing". The prompt is now explicit, the README states it, and the program names the
input it rejected.

**Rule:** document every prompt a user must answer, including the ones you consider
obvious.

### C3. Two avoidable credential round-trips

A fine-grained token lacked `Administration: write`, so repository creation failed with
`403 Resource not accessible by personal access token`. Worse, the token was revoked on our
advice right before it was needed again.

**Rule:** state which token *type* is required and which exact permissions it needs before
asking for one; and do not advise revoking a credential you will need again.

### C4. A near miss while writing to a binary with PowerShell

A scan script used `Set-Content -Value $s` against `app.asar`. The file turned out to be
undamaged (its UTF-8 mangling predated the session), but the pattern was dangerous.

**Rule:** never point a write cmdlet at an application bundle. Back up and verify hashes
first.

### C5. Testing an interactive program without a timeout

A background test invoked the executable with input it never supplied. The process blocked
on `ReadLine` and had to be killed, and an elevated instance could not be terminated from a
non-elevated shell.

**Rule:** interactive programs get a timeout, or a driver that always supplies input.

### C6. Mistaking a stale process for a file lock

A build appeared to fail because the target was locked. In fact a leftover elevated process
was still running the *previous* build, which made new code look ineffective. The build
script now detects a running instance and moves a locked binary aside instead of failing
after a successful compile.

### C7. An imprecise changelog claim

The changelog stated the PowerShell tooling was unchanged from v1.0.0. It had in fact been
edited before the first publish (a dead `-KeepBackup` parameter was removed and a summary
line reworded).

**Rule:** changelog entries describe what shipped, not what you remember changing.

### C8. A duplicate release

The first release creation quietly produced a **draft**, so a later attempt created a
second release with the same tag. Always list releases after creating one and confirm the
`draft` flag.
