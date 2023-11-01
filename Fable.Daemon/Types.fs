namespace Fable.Daemon

open FSharp.Compiler.CodeAnalysis

type ProjectChangedPayload =
    {
        /// Absolute path of fsproj
        Project : string
        /// Absolute path of fable-library. Typically found in the npm modules
        FableLibrary : string
    }

type ProjectChangedResult =
    {
        ProjectOptions : FSharpProjectOptions
        CompiledFSharpFiles : Map<string, string>
    }
    
type FileChangedResult =
    {
        CompiledFSharpFiles : Map<string, string>
    }

type PingPayload = { Msg : string }

type CompileFilePayload = { FileName : string }
