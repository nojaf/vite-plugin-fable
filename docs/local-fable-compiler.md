---
index: 7
categoryindex: 1
category: docs
---

# Using a Local Fable Compiler

It is relatively easy to set up a local Fable compiler for troubleshooting the plugin.

## Checkout the Fable Repository

Clone the [Fable repository](https://github.com/fable-compiler/Fable) as a sibling to this repository.

## Using Local Fable Binaries

Set `<UseLocalFableCompiler>true</UseLocalFableCompiler>` in [Directory.Build.props](https://github.com/fable-compiler/vite-plugin-fable/blob/main/Directory.Build.props). After running `bun install`, the daemon will be built using the local binary.

## Use Local fable-library-js

Sometimes, there could be changes in `@fable-org/fable-library-js` that you need to reflect in the daemon's output.

Build the fable-library using `./build.sh fable-library --javascript` (from the Fable repository root).

Update the `package.json` (in the root) to:

```json
{
  "dependencies": {
    "@fable-org/fable-library-js": "../Fable/temp/fable-library-js"
  }
}
```

Install again using `bun install`.

[Next]({{fsdocs-next-page-link}})
