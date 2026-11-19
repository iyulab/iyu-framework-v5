# Contributing to iyu-framework-v5

Thanks for your interest. This repository is a set of `Iyu.*` libraries that ship as separate NuGet
packages — the [README](./README.md) says what each one is for, and what the whole stack does and does
not do.

## Asking for help or requesting something

Open an [issue](https://github.com/iyulab/iyu-framework-v5/issues) on this repository — questions, bug
reports and feature requests all go there. Look through the open ones first.

- **A bug or a question:** name the package and its version, give the smallest code that shows it, what you
  expected and what you got.
- **A request:** describe the problem you are trying to solve before the change you would like. A request
  is weighed against what each package is for, and one that belongs in another part of the stack is
  pointed there.

Do not put credentials, connection strings or private data in an issue.

## Development

Requires the .NET 10 SDK.

```bash
dotnet build IyuFramework.slnx
dotnet test  IyuFramework.slnx
```

`TreatWarningsAsErrors` is on — a warning breaks the build. Every shipped project carries one shared
`<Version>` in `Directory.Build.props` (except the packages the README lists as versioned on their own),
so a change anywhere moves them together.

The code in the README is part of what ships. Samples marked in the README are compiled by the test suite
against the packages as they are, so a change to a public type that leaves its sample behind fails a test —
update the text in the same change.

## Conduct

Be kind, be direct, assume good faith.

## License

Contributions are accepted under the project's MIT license.
