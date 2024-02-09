namespace Fable.Daemon

open FSharp.Compiler.CodeAnalysis

type ProjectChangedPayload =
    {
        /// Release or Debug
        Configuration : string
        /// Absolute path of fsproj
        Project : string
        /// Absolute path of fable-library. Typically found in the npm modules
        FableLibrary : string
    }

type DiagnosticRange =
    {
        StartLine : int
        StartColumn : int
        EndLine : int
        EndColumn : int
    }

type Diagnostic =
    {
        ErrorNumberText : string
        Message : string
        Range : DiagnosticRange
        Severity : string
        FileName : string
    }

[<RequireQualifiedAccess>]
type ProjectChangedResult =
    | Success of projectOptions : FSharpProjectOptions * diagnostics : Diagnostic array
    | Error of string

[<RequireQualifiedAccess>]
type FilesCompiledResult =
    | Success of compiledFSharpFiles : Map<string, string>
    | Error of string

[<RequireQualifiedAccess>]
type FileChangedResult =
    | Success of compiledFSharpFiles : Map<string, string> * diagnostics : Diagnostic array
    | Error of string

type PingPayload = { Msg : string }

type CompileFilePayload = { FileName : string }
