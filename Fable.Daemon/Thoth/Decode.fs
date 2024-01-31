namespace Thoth.Json.SystemTextJson

open System.Text.Json
open Thoth.Json.Core

[<RequireQualifiedAccess>]
module Decode =
    let serializerOptions =
        JsonSerializerOptions (WriteIndented = true, AllowTrailingCommas = true)

    let helpers =
        { new IDecoderHelpers<JsonElement> with
            member _.isString jsonValue =
                jsonValue.ValueKind = JsonValueKind.String

            member _.isNumber jsonValue =
                jsonValue.ValueKind = JsonValueKind.Number

            member _.isBoolean jsonValue =
                jsonValue.ValueKind = JsonValueKind.True
                || jsonValue.ValueKind = JsonValueKind.False

            member _.isNullValue jsonValue =
                jsonValue.ValueKind = JsonValueKind.Null

            member _.isArray jsonValue =
                jsonValue.ValueKind = JsonValueKind.Array

            member _.isObject jsonValue =
                jsonValue.ValueKind = JsonValueKind.Object

            member _.hasProperty fieldName jsonValue =
                jsonValue.ValueKind = JsonValueKind.Object
                && fst (jsonValue.TryGetProperty (fieldName))

            member _.isIntegralValue jsonValue =
                jsonValue.ValueKind = JsonValueKind.Number
                && not (fst (jsonValue.TryGetDouble ()))

            member _.asString jsonValue = jsonValue.GetString ()
            member _.asBoolean jsonValue = jsonValue.GetBoolean ()

            member _.asArray jsonValue =
                jsonValue.EnumerateArray () |> Seq.toArray

            member _.asFloat jsonValue = jsonValue.GetDouble ()
            member _.asFloat32 jsonValue = jsonValue.GetSingle ()
            member _.asInt jsonValue = jsonValue.GetInt32 ()

            member _.getProperties jsonValue =
                jsonValue.EnumerateObject () |> Seq.map (fun prop -> prop.Name)

            member _.getProperty (fieldName : string, jsonValue : JsonElement) = jsonValue.GetProperty fieldName

            member _.anyToString jsonValue =
                // Serializing the JsonElement to a string
                JsonSerializer.Serialize (jsonValue, serializerOptions)
        }

    let fromString (decoder : Decoder<'T>) =
        fun (value : string) ->
            try
                let parsedJson = JsonSerializer.Deserialize<JsonElement> (value, serializerOptions)

                match decoder.Decode (helpers, parsedJson) with
                | Ok success -> Ok success
                | Error error ->
                    let finalError = error |> Decode.Helpers.prependPath "$"
                    Error (Decode.errorToString helpers finalError)
            with ex -> // Catching generic exceptions here, refine as needed
                Error ("Given an invalid JSON: " + ex.Message)

    let unsafeFromString (decoder : Decoder<'T>) =
        fun value ->
            match fromString decoder value with
            | Ok x -> x
            | Error e -> failwith e
