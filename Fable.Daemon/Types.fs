namespace Fable.Daemon

open FSharp.Compiler.CodeAnalysis

type FullPath = string
type Hash = string
type JavaScript = string

type ProjectChangedPayload =
    {
        /// Release or Debug
        Configuration : string
        /// Absolute path of fsproj
        Project : FullPath
        /// Absolute path of fable-library. Typically found in the npm modules
        FableLibrary : FullPath
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
        FileName : FullPath
    }

[<RequireQualifiedAccess>]
type ProjectChangedResult =
    | Success of projectOptions : FSharpProjectOptions * diagnostics : Diagnostic array
    | Error of string

[<RequireQualifiedAccess>]
type FilesCompiledResult =
    | Success of compiledFSharpFiles : Map<FullPath, JavaScript>
    | Error of string

[<RequireQualifiedAccess>]
type FileChangedResult =
    | Success of compiledFSharpFiles : Map<FullPath, JavaScript> * diagnostics : Diagnostic array
    | Error of string

type PingPayload = { Msg : string }

type CompileFilePayload = { FileName : FullPath }
