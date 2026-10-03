`model-aliases-sample.md` is a hand-written sample so the alias parser tests run **without a network**.
The official page (https://code.claude.com/docs/en/model-config.md) is copyrighted, so no copy is kept in the public repo (2026-10-01).
It only imitates the page's **shape**: a "### Model aliases" heading, a table of `**`name`**` entries under it, ending at the next "### " heading.

- Expected aliases: `fable, sonnet, opus, haiku`
  (`default`·`best`·`opusplan` are excluded because they are not model families, `sonnet[1m]`·`opus[1m]` because of the `[`, duplicates are dropped,
  and bold names before and after the section are excluded too)
- If the real page changes shape (when a fetched page yields fewer than 3 aliases, the app leaves the button as it is), update this sample to the new shape.
