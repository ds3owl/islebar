# Sample model page (IsleBar test fixture)

Written by hand for IsleBar's tests. It only copies the **layout** the alias parser relies on in Claude Code's
model-config page (a level-3 "Model aliases" heading, then a table of bold code-formatted aliases). None of
the page's text is reproduced here.

## Models

Bold names before the alias section must be ignored: **`ignored-before`**, **`sonnet-legacy`**.

### Model aliases

Pick a family by its short name:

| Alias | Note |
| - | - |
| **`default`** | special value, not a family |
| **`best`** | special value, not a family |
| **`fable`** | family |
| **`sonnet`** | family |
| **`opus`** | family |
| **`haiku`** | family |
| **`sonnet[1m]`** | context variant, not a family |
| **`opus[1m]`** | context variant, not a family |
| **`opusplan`** | special mode, not a family |
| **`opus`** | listed twice on purpose |

#### A sub-heading inside the section

Text under a level-4 heading still belongs to the section.

### Next section

Bold names after the section must be ignored: **`ignored-after`**, **`mythic`**.
