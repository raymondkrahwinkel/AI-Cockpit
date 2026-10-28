---
title: What a Depot note carries beyond plain Markdown
aliases: [notes, flavour]
---

# What a Depot note carries

Links between notes are wikilinks: see [[Docs/Edit]], [[Docs/Edit|with an alias]] and [[Page#Heading]], not a `[link](url)`.

An alert block is a quote:

> [!NOTE]
> Written by the renderer, so the brackets must survive.

Words with & signs, a < b > c, snake_case_names, 2 * 3 and a bare address https://example.com/a?b=1&c=2 stay as typed.
Label [gemeten] and a <host> placeholder too, but `code & <tags>` and fences are never touched:

```html
<details>
  <summary>a & b</summary>
</details>
```

| Setting | Meaning                |
| ------- | ---------------------- |
| `a_b`   | A value with a \| pipe |
| `c`     | Plain & simple         |

1. First, with *emphasis*, **strong** and ~~strike~~
   1. nested one
   2. nested two
2. Second

- [ ] a task with a [[Docs/Edit]] link
  - [x] and a nested one

Final line with a trailing hard break  
and its second line.
