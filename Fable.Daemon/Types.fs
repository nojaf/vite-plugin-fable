namespace Fable.Daemon

open FSharp.Compiler.CodeAnalysis

type ProjectChangedPayload =
    {
        /// Absolute path of fsproj
        Project : string
        /// Absolute path of fable-library. Typically found in the npm modules
        FableLibrary : string
    }

[<RequireQualifiedAccess>]
type ProjectChangedResult =
    | Success of projectOptions : FSharpProjectOptions * compiledFiles : Map<string, string>
    | Error of string

type FileChangedResult =
    {
        CompiledFSharpFiles : Map<string, string>
    }

type PingPayload = { Msg : string }

type CompileFilePayload = { FileName : string }
