---
index: 1
categoryindex: 1
category: docs
---

# vite-plugin-fable

<style>img { max-width: minmax(100%, 400px); display: block; margin-inline: auto; }</style>

![vite-plugin-fable logo](./img/logo.png)

## Introduction

When diving into Vite, I found myself having a friendly debate with what the [get started with Vite](https://fable.io/docs/getting-started/javascript.html#browser) guide suggests.  
It's purely a matter of taste, and I mean no disrespect to the authors.

If you peek at the latest Fable docs, you'll notice this snippet at the end:

    dotnet fable watch --run npx vite

Now, that's where my preferences raise an eyebrow.
For nearly everything else in Vite, whether it's JSX, TypeScript, Sass, or Markdown, I find myself typing `npm run dev`. (<small>or even `bun run dev`, cheers to [Bun](https://twitter.com/i/status/1701702174810747346) for that!</small>)  
You know, the command that summons `vite`, the proclaimed [Next Generation Frontend Tooling](https://vitejs.dev/).

I'm of the opinion (_brace yourselves, hot take incoming_) that integrating Fable with frontend development should align as closely as possible with the broader ecosystem. Vite is a star in the frontend universe, with a user base dwarfing that of F# developers. It makes sense to harmonize with their flow, not the other way around.

    bun run dev

I absolutely recognize and respect the legacy of Fable. It's a veteran in the scene, predating Vite, so I get the historical reasons for its current approach.  
But that doesn't mean I can't cheer for evolution and a bit of change, right?

[Next]({{fsdocs-next-page-link}})
