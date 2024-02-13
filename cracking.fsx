#I "./bin"
#r "Fable.AST"
#r "Fable.Compiler"
#r "Fable.Daemon"
#r "./bin/FSharp.Compiler.Service.dll"

open System.IO
open Fable.Compiler.Util
open Fable.Compiler.ProjectCracker
open Fable.Daemon

fsi.AddPrinter (fun (x : ProjectOptionsResponse) ->
    $"ProjectOptionsResponse: %i{x.ProjectOptions.Length} options, %i{x.ProjectReferences.Length} references, %s{x.TargetFramework.Value}, %s{x.OutputType.Value}"
)

let fsproj =
    "/home/nojaf/projects/fantomas-tools/src/client/fsharp/FantomasTools.fsproj"
// Path.Combine (__SOURCE_DIRECTORY__, "sample-project/App.fsproj")

let cliArgs : CliArgs =
    {
        ProjectFile = fsproj
        RootDir = __SOURCE_DIRECTORY__
        OutDir = None
        IsWatch = false
        Precompile = false
        PrecompiledLib = None
        PrintAst = false
        FableLibraryPath = Some (Path.Combine (__SOURCE_DIRECTORY__, "sample-project/node_modules/fable-library"))
        Configuration = "Debug"
        NoRestore = false
        NoCache = true
        NoParallelTypeCheck = false
        SourceMaps = false
        SourceMapsRoot = None
        Exclude = []
        Replace = Map.empty
        RunProcess = None
        CompilerOptions =
            {
                TypedArrays = true
                ClampByteArrays = false
                Language = Fable.Language.JavaScript
                Define = [ "FABLE_COMPILER" ; "FABLE_COMPILER_4" ; "FABLE_COMPILER_JAVASCRIPT" ]
                DebugMode = true
                OptimizeFSharpAst = false
                Verbosity = Fable.Verbosity.Verbose
                FileExtension = ".fs"
                TriggeredByDependency = false
                NoReflection = false
            }
        Verbosity = Fable.Verbosity.Verbose
    }

let options : CrackerOptions = CrackerOptions (cliArgs, true)
let resolver : ProjectCrackerResolver = CoolCatResolver ()

#time "on"

let result = resolver.GetProjectOptionsFromProjectFile (true, options, fsproj)

#time "off"

// result.ProjectReferences

for option in result.ProjectOptions do
    printfn "%s" option
