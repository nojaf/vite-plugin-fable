module App

open Fable.Core.JS
open Browser.Dom
open Math
open Thoth.Json

let r = sum 1 7

let someJsonString =
    Encode.object [ "track", Encode.string "Smash Shit Up" ] |> Encode.toString 4

let h1Element = document.querySelector "h1"
h1Element.textContent <- $"Dynamic Fable text %i{r}! %s{someJsonString}"
