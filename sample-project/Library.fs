module App

open Fable.Core.JS
open Browser.Dom
open Math
open Thoth.Json

let r = sum 1 19

let someJsonString =
    Encode.object [ "track", Encode.string "Fils de personne" ] |> Encode.toString 4

let h1Element = document.querySelector "h1"
h1Element.textContent <- $"Dynamic Fable text %i{r}! %s{someJsonString}"
