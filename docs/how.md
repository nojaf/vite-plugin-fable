---
index: 2
categoryindex: 1
category: docs
---

# How does this work?

The key concept is that everything starts with launching the Vite dev server and a special Vite plugin is able to deal with importing an F# file.

Assuming you have an existing `fsproj`, with all your code and F# dependencies, your `vite.config.js` would need to wire up the plugin:

```js
import { defineConfig } from "vite";
import fable from "vite-plugin-fable";

// https://vitejs.dev/config/
export default defineConfig({
  plugins: [fable()],
});
```

[Vite plugins](https://vitejs.dev/plugins/) have various hooks that will deal with importing and transforming F# files.
These hooks will start a `dotnet` process and communicate with it via [JSON RPC](https://www.jsonrpc.org/).

## Index.html

The index.html needs to import an F# file to the plugin to process:

```html
<!doctype html>
<html lang="en">
  <head>
    <meta charset="UTF-8" />
    <link rel="icon" type="image/svg+xml" href="/vite.svg" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
    <title>Vite + Fable</title>
  </head>
  <body>
    <script type="module">
      import "/Library.fs";
    </script>
  </body>
</html>
```

The is a technical limitation why we cannot load the initial entrypoint as `<script type="module" src="/Library.fs"></script>`.  
See [vitejs/vite#9981](https://github.com/vitejs/vite/pull/9981)

## Starting Vite

    npm run dev

<div class="mermaid">
sequenceDiagram
    Vite->>dotnet: 1. config resolve hook
    activate dotnet
    dotnet->>Vite: 2. FSharpOptions resolved
    deactivate dotnet
    Vite->>dotnet: 3. FSharp file changed
    activate dotnet
    dotnet->>Vite: 4. Transformed FSharp files
    deactivate dotnet
</div>

### Config resolution

When the Vite dev server starts it needs to process the main `fsproj` file and do the initial compile of all F# files to JavaScript.  
All the good stuff happens here:

- NuGet packages get resolved.
- The [FSharpProjectOptions](https://fsharp.github.io/fsharp-compiler-docs/reference/fsharp-compiler-codeanalysis-fsharpprojectoptions.html) is being composed.
- The project gets type-checked and Fable will transpile all source files to JavaScript.

The `dotnet` process is using [Fable.Compiler](https://github.com/fable-compiler/Fable/issues/3552) to pull this off.
This is a large portion of shared code that also would be running when you invoke `dotnet fable`.

### FSharpOptions resolved

The resulting JSON message the dotnet process will return to the Vite plugin are the FSharpOptions and the entire set of compiled F# files.

Note that no transpiled F# file has been written to disk at this point. We keep everything in memory and avoid IO overhead.

### An FSharp file changed

When we edit our code, we need to re-evaluate our current project. One or multiple F# files could potentially need a recompile to JavaScript.
The [Graph Based checking](https://devblogs.microsoft.com/dotnet/a-new-fsharp-compiler-feature-graphbased-typechecking/) algorithm is used here to figure out what needs to be reevaluated.

Note that we let Vite decide here whether any file was changed or not. This is already a key difference between how this setup works and how `dotnet fable` works.

### Transformed FSharp files

The JSON rpc response will contain the latest version of the changed files. The plugin will update the inner state and the `handleHotUpdate` hook will alert the browser if any file change requires an HMR update or browser reload.

[Next]({{fsdocs-next-page-link}})
