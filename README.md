# Vite plugin for Fable

Hi there, this is an experiment, it somewhat works on my machine and please don't expect anything from this.

## What is this?

Alright, the `tl;dr` is that I don't like the current way of how you can use [Fable](https://fable.io) with [Vite](https://vitejs.dev). It should be a plugin instead.
This is a personal preference thing, and I don't want to be disrespectful toward anyone.

If you follow the latest Fable docs on how to [get started with Vite](https://fable.io/docs/getting-started/javascript.html#browser), you will noticed that the end you run

    dotnet fable watch --run npx vite

That! Right there, I don't like that one bit.
When doing literally anything else with Vite, be it JSX, TypeScript, Sass, Markdown, I run `npm run dev`.  
You know, the thing that invokes `vite`, the dev-server.

I believe (*hot take incoming*) that using Fable for frontend should stick as close as whatever the rest of the world is doing.

## Why I feel things are disjointed right now

Running `dotnet fable` will first compile all F# files to JavaScript and writes them to disk.
The real reason this happens is that all JavaScript things exists and Vite can pick up the pieces.
The JS tooling had no idea Fable even processed the files.

The transpiled files don't need to exist on disk when Vite has a concept of virtual files.
When dealing with `*.sass` you also don't compile them first to `.css` in order for Vite do understand what is up.

Another thing I really loath is that I need start things from the `dotnet` side. I'm using Node.Js (or Bun 😉) tooling, that should be the primary in this story. 
You are using `Vite` with `Fable` and should be using `Fable` with `Vite`.

## How does my perfect world look like?

I scaffold a [new Vite project](https://vitejs.dev/guide/#scaffolding-your-first-vite-project), creat an `fsproj` and install a plugin so that Vite knows what to do with F# files:

```js
import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import fable from "vite-plugin-fable";

// https://vitejs.dev/config/
export default defineConfig({
  plugins: [react(), fable()],
})
```

run `npm run dev` (or `npx vite`) and things just work.

In my `index.html` I point to my entry file and watch everything unfold.

## Lest we forget

I'm not pleading here that we should forget about the `dotnet` side of things. It is inevitable to deal with it.
Using NuGet would still be a thing, you still need the dotnet SDK to get everything working and you kinda need to know what you are doing to some degree.

All I'm saying is that we should stick the developer experience as close as possible to what would be a typical experience when dealing with TypeScript or [Elm](https://github.com/hmsk/vite-plugin-elm). 

If you are new to F# (and/or dotnet) and you don't want to jump through too many hoops to get Vite working with F#.
That experience should be seamless when you worked with Vite in the past and wanna give F# a try.

## How does this work?

`index.js` is a Rollup / Vite plugin that talks to a JSON RPC endpoint.
That endpoint is a dotnet process that invokes Fable compilation in memory.
Highly experimental stuff, works by the [Fable.Compiler](https://github.com/fable-compiler/Fable/issues/3552) package.

## How can I help?

This is in a very premature early state, so you either:

- Dive as deep as I'm going right now and send PR's forged in the fires of your own experiences.
- Considering financial support through contracting.
