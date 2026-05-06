# Comment audit heuristics

Reference document for any future automated or manual comment-cleanup pass on this codebase. Read this in full before editing a single comment. The first automated pass on 2026-05-04 failed by inverting the most important rule below (sentence-boundary breaks) — these heuristics encode that lesson.

## Goal

Comments should be concise, descriptive, and **break on natural meaning boundaries**, not on visual width.

## Definition of a "bad" comment

A comment is bad if and only if at least one of these holds:

1. **Wordy / filler.** It restates what the code already says, opens with stock filler (`This method is responsible for...`, `We need to...`, `Note that...`), or pads a one-line idea across three lines.
2. **Mid-clause line break.** A multi-line comment whose breaks fall inside a noun phrase, verb phrase, or clause — i.e. someone visually rectangle-aligned the lines to equal width instead of breaking on natural clause endings. A continuous thought has been artificially fragmented.
3. **Hard line over 120 chars** *and* a natural break point exists within the line. If no natural break exists, leave the line alone — going to 122-125 chars to preserve a sentence is fine.

If none of these hold, the comment is fine. Leave it alone.

## Definition of a "good" comment

1. **Concise but descriptive.** A reader gets the *why* in one read.
2. **Breaks on sentence boundaries first**, then clause boundaries (comma, semicolon, em-dash, colon).
3. Lines aim for `<= 120` chars but slight overrun is acceptable when breaking earlier would split a phrase.

## The 120 rule — cap, not target

- 120 is a **cap**, not a target.
- **Never break a line short of 120 just to balance widths against the next line.**
- Going slightly over 120 to keep a sentence whole is acceptable. Sentence integrity > strict 120 compliance.
- If a sentence is so long it must break, break at the strongest internal pause (period > semicolon > em-dash > comma > "and"/"or"/"but" > weak comma).

## Hard rules — do NOT

- **Do NOT rectangle-flatten** multi-line comments that were already broken on sentence/clause boundaries. The most common failure mode of a careless rewrite. If the original breaks on periods and commas, the rewrite must too.
- **Do NOT break inside a noun phrase or verb phrase.** Examples of bad breaks: `revalidate the | shell:startup shortcut`, `LoadOrDefault writes the default | file`, `any in-flight op finish | cleanly`, `kill-this-process timer if we | sit here too long`.
- **Do NOT delete useful intent comments.** Short single-liners like `// Pre-warm the flyout so it opens instantly`, `// Initialize theme manager`, `// Wait for the watcher to exit` describe section intent and may stay even when nominally redundant. Only delete a comment when it is purely redundant noise (e.g. `// constructor` directly above a constructor signature, or `// returns` above a `return` statement).
- **Do NOT change code.** Only the text of comments. No identifier renames, no expression edits, no whitespace changes inside code lines.
- **Do NOT add new comments.** If a section has no comment, leave it.
- **Do NOT touch XML doc structure** (`<summary>`, `<param>`, etc.). Fix wordiness and bad breaks inside them, but keep the tags.
- **Do NOT touch ASCII-art layout, hex tables, byte sequences, RVAs, or struct field offsets.** These are load-bearing reference data, not prose.

## Procedure

1. Read the entire file. Form a model of which comments are load-bearing vs. cosmetic.
2. For each comment in turn, run the three-question test:
   - Wordy / filler / restates code? Y/N
   - Multi-line break that lands inside a clause, noun phrase, or verb phrase? Y/N
   - A single hard line over 120 chars that *also* contains a natural pause point you can break on? Y/N
3. If all three are N, **leave the comment alone**. Default to no-edit.
4. If any is Y, rewrite — but only the part that's actually wrong. Preserve the original phrasing and break choices wherever they're correct.
5. After editing, re-read the comment. Does each line end at a sentence period, semicolon, em-dash, colon, or comma? If a line ends mid-phrase, you broke the rule — fix it.
6. Verify line widths: every line should be `<= 120` chars unless the only natural break is past 120. If you ended up `<= 110` on every line because you broke early, you over-fragmented — let some lines run longer.

## Worked examples

### Example 1 — wordy summary, bad rewrite

Original (already good):

```csharp
// Load app settings first - theme needs them.
// Detect first-run before LoadOrDefault writes the default file
// so we can reconcile OS state (e.g. startup registration) with the defaults that just got persisted.
```

Bad rewrite (do NOT do this):

```csharp
// Load app settings first - theme needs them. Detect first-run before LoadOrDefault writes the default
// file so we can reconcile OS state (e.g. startup registration) with the defaults that just got persisted.
```

Why bad: line 1 ends mid-noun-phrase (`writes the default | file`). The original was already correctly broken on clauses — leaving it alone was the right call.

### Example 2 — wordy + bad breaks at the same time

Original:

```csharp
// Drop the legacy HKCU\...\Run autostart entry (older builds wrote one)
// and revalidate the shell:startup shortcut.
// Without these, an upgraded user could end up running the app twice at sign-in,
// or worse, having the shortcut point at a no-longer-existing exe path that silently does nothing.
```

Acceptable rewrite (folds the two leading clauses into one sentence on one line):

```csharp
// Drop the legacy HKCU\...\Run autostart entry (older builds wrote one) and revalidate the shell:startup shortcut.
// Without these, an upgraded user could end up running the app twice at sign-in,
// or worse, having the shortcut point at a no-longer-existing exe path that silently does nothing.
```

Notes: line 1 is 123 chars but breaking earlier would split `the shell:startup shortcut`. Sentence integrity wins. Lines 2-3 break on commas, real clause boundaries.

Bad rewrite (do NOT do this):

```csharp
// Drop the legacy HKCU\...\Run autostart entry (older builds wrote one) and revalidate the
// shell:startup shortcut. Without these, an upgraded user could end up running the app twice at sign-in,
// or worse, having the shortcut point at a no-longer-existing exe path that silently does nothing.
```

Why bad: line 1 ends mid-noun-phrase (`revalidate the | shell:startup shortcut`).

### Example 3 — leave-alone case

Original:

```csharp
// The drain caps the wait so a hung op can't block the exit,
// while still letting any in-flight op finish cleanly when it can.
// Quick caps (200-500ms) because these paths are urgent:
// Windows has its own kill-this-process timer if we sit here too long.
```

This is already good. Each line ends at a comma, period, or colon — all real pauses. Do not touch.

A bad rewrite would compress to:

```csharp
// The drain caps the wait so a hung op can't block the exit, while still letting any in-flight op finish
// cleanly when it can. Quick caps (200-500ms) because Windows has its own kill-this-process timer if we
// sit here too long.
```

Why bad: lines 1 and 2 break mid-clause (`any in-flight op finish | cleanly`, `kill-this-process timer if we | sit`). The original was correct — apparent verbosity is not justification for rectangle-flattening.

### Example 4 — single-liner over 120

Original (line is 132 chars):

```csharp
// Falls back to the default monitor list when no monitor service is registered, which can happen during early startup before App.OnStartup wires it.
```

Acceptable rewrite (break at the comma — a real clause boundary):

```csharp
// Falls back to the default monitor list when no monitor service is registered,
// which can happen during early startup before App.OnStartup wires it.
```

### Example 5 — XML doc

Original:

```csharp
/// <summary>
/// This method is responsible for monitoring the watcher process and exits the application if the watcher process dies.
/// This is to ensure that the application doesn't run in an orphaned state if the watcher is killed.
/// </summary>
```

Acceptable rewrite (drop filler, keep the why):

```csharp
/// <summary>
/// Polls the watcher process and exits the app when it dies, so we don't run orphaned.
/// </summary>
```

## Heuristic for whether you're being too aggressive

After your pass, count edits per file. If you touched more than ~20% of comments in a file, you are probably wrong about most of those edits. Re-read your diff against the rules above. Keep only the edits where the original genuinely violated the rules; revert the rest.

If during a pass you find yourself reformatting a multi-line comment whose lines already end on `.`, `;`, `,`, `:`, or `--`, **stop immediately**. That comment is correct. Move on.

## Concurrency note

When the user is editing the codebase concurrently, prefer non-action over action. A missed bad comment is recoverable on the next pass; an edit that stomps a user's in-progress work is not. If a file looks recently touched (mid-refactor variable names, mismatched indentation, half-applied renames), skip it.
