# Iyu.FileServer.Azure

Azure Blob Storage backend for the Iyu.FileServer file gateway, with connection-string and keyless (token credential) authentication.

The credential is the one your host supplies — for a storage account with shared-key access turned off — and the package itself takes no dependency on `Azure.Identity`. The gateway it plugs into is [Iyu.FileServer](https://www.nuget.org/packages/Iyu.FileServer).

```
dotnet add package Iyu.FileServer.Azure
```

**Documentation:** [File gateway (`Iyu.FileServer`)](https://github.com/iyulab/iyu-framework-v5#file-gateway-iyufileserver) in the repository README, which shows both ways to authenticate.

`Iyu.FileServer.Azure` is one of the `Iyu.*` packages of [iyu-framework-v5](https://github.com/iyulab/iyu-framework-v5), which share one version; the [changelog](https://github.com/iyulab/iyu-framework-v5/blob/main/CHANGELOG.md) is shipped in this package, and its "Packages affected" line says which release touched which package. MIT licensed.
