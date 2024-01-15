module App

open Fable.Core.JS
open Browser.Dom
open Math
open Thoth.Json

let r = sum 1 4

let someJsonString =
    Encode.object [ "track", Encode.string "Lonely Mountain" ] |> Encode.toString 4

let h1Element = document.querySelector "h1"
h1Element.textContent <- $"Dynamic Fable text %i{r}! %s{someJsonString}"
