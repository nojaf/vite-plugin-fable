module App

open Fable.Core.JS
open Browser.Dom
open Math
open Thoth.Json

let r = sum 1 2

let someDecoder =
    Encode.object [
        "track", Encode.string "Man in the Mirror"
    ]
    |> Encode.toString 4

let h1Element = document.querySelector "h1"
h1Element.textContent <- $"Dynamic Fable text yow %i{r}!"
console.info someDecoder
