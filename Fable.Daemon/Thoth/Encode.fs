namespace Thoth.Json.SystemTextJson

open System.Text.Json
open System.Text.Json.Nodes
open Thoth.Json.Core

[<RequireQualifiedAccess>]
module Encode =
    let helpers =
        { new IEncoderHelpers<JsonNode> with
            member _.encodeString value = JsonValue.Create (value)
            member _.encodeChar value = JsonValue.Create (value.ToString ())
            member _.encodeDecimalNumber value = JsonValue.Create (value)
            member _.encodeBool value = JsonValue.Create (value)
            member _.encodeNull () = JsonValue.Create (null)
            member _.createEmptyObject () = JsonObject ()

            member _.setPropertyOnObject (o : JsonNode, key : string, value : JsonNode) =
                match o with
                | :? JsonObject as jsonObj -> jsonObj.[key] <- value
                | _ -> failwith "Not a JSON object"

            member _.encodeArray values = JsonArray (values)
            member _.encodeList values = JsonArray (List.toArray values)
            member _.encodeSeq values = JsonArray (Seq.toArray values)

            member _.encodeIntegralNumber (value : uint32) = JsonValue.Create (value)
        }

    let toString (value : Json) : string =
        let json = Encode.toJsonValue helpers value
        JsonSerializer.Serialize (json, Decode.serializerOptions)
