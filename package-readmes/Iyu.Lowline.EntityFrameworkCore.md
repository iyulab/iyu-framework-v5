# Iyu.Lowline.EntityFrameworkCore

Entity Framework Core store behind Iyu.Lowline's two ports, over the application's own DbContext tables; reference it only if the records live there.

```
dotnet add package Iyu.Lowline.EntityFrameworkCore
```

**Documentation:** [Learning from confirmed records (`Iyu.Lowline`)](https://github.com/iyulab/iyu-framework-v5#learning-from-confirmed-records-iyulowline) in the repository README.

`Iyu.Lowline.EntityFrameworkCore` shares the version of the other `Iyu.*` packages of [iyu-framework-v5](https://github.com/iyulab/iyu-framework-v5) and builds on `Iyu.Lowline`, which has its own line; the [changelog](https://github.com/iyulab/iyu-framework-v5/blob/main/CHANGELOG.md) is shipped in this package, and its "Packages affected" line says which release touched which package. MIT licensed.
