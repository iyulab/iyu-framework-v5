# Iyu.UBoard

Host-neutral board storage for embedding a board viewer and editor - boards as opaque JSON documents with an append-only revision history, optimistic saves, and a publication pointer with a history to fall back through; it depends on neither Entity Framework Core nor Iyu.Core.

```
dotnet add package Iyu.UBoard
```

**Documentation:** [Keeping boards and their history (`Iyu.UBoard`)](https://github.com/iyulab/iyu-framework-v5#keeping-boards-and-their-history-iyuuboard) in the repository README.

`Iyu.UBoard` is versioned on its own line, separate from the other `Iyu.*` packages of [iyu-framework-v5](https://github.com/iyulab/iyu-framework-v5); its [changelog](https://github.com/iyulab/iyu-framework-v5/blob/main/src/Iyu.UBoard/CHANGELOG.md) is shipped in this package. MIT licensed.
