#r "./bin/Thoth.Json.Core.dll"
#r "./bin/Fable.AST.dll"
#r "./bin/Fable.Compiler.dll"
#load "./Fable.Daemon/Thoth/Decode.fs"
#load "./Fable.Daemon/Thoth/Encode.fs"
#load "./Fable.Daemon/CoolCatCracking.fs"

open System
open System.IO
open Thoth.Json.Core
open Thoth.Json.SystemTextJson
open Fable
open Fable.Compiler.Util
open Fable.Compiler.ProjectCracker
open Fable.Daemon.CoolCatCracking

[<Literal>]
let CacheFileNameSuffix = ".vite-plugin-fable-cache.json"

type CacheKey =
    {
        /// Input fsproj project.
        MainFsproj : FileInfo
        /// This is the file that contains the cached information.
        /// Initially it doesn't exist and can only be checked in subsequent runs.
        CacheFile : FileInfo
        /// All the files that can influence the MSBuild evaluation.
        /// This typically is the
        DependentFiles : FileInfo list
        /// Contains both the user defined configurations (via Vite plugin options)
        Defines : string list
        /// Configuration
        Configuration : string
    }

let mkFileHash (f : FileInfo) : string = "foo"

let cacheKeyDecoder (options : CrackerOptions) (fsproj : FileInfo) : Decoder<CacheKey> =
    Decode.object (fun get ->
        let paths =
            let value = get.Required.At [ "Properties" ; "MSBuildAllProjects" ] Decode.string

            value.Split (';', StringSplitOptions.RemoveEmptyEntries)
            |> Array.choose (fun path ->
                let fi = FileInfo path

                if not fi.Exists then None else Some fi
            )

        let intermediateOutputPath =
            let v =
                get.Required.At [ "Properties" ; "BaseIntermediateOutputPath" ] Decode.string

            let v = v.TrimEnd '\\'
            Path.Combine (fsproj.DirectoryName, v) |> Path.GetFullPath

        let nugetGProps =
            let gPropFile =
                Path.Combine (intermediateOutputPath, $"%s{fsproj.Name}.nuget.g.props")
                |> FileInfo

            if not gPropFile.Exists then [] else [ gPropFile ]

        let cacheFile =
            FileInfo (
                Path.Combine (intermediateOutputPath, options.Configuration, $"{fsproj.Name}%s{CacheFileNameSuffix}")
            )

        {
            MainFsproj = fsproj
            CacheFile = cacheFile
            DependentFiles = [ yield fsproj ; yield! paths ; yield! nugetGProps ]
            Defines = options.FableOptions.Define
            Configuration = options.Configuration
        }
    )

let mkProjectCacheKey (options : CrackerOptions) (fsproj : FileInfo) =
    async {
        if not fsproj.Exists then
            raise (ArgumentException ($"%s{fsproj.FullName} does not exists", nameof (fsproj)))

        if String.IsNullOrWhiteSpace options.Configuration then
            raise (
                ArgumentException ("options.Configuration cannot be null or whitespace", nameof (options.Configuration))
            )

        let! json =
            dotnet_msbuild fsproj.FullName "--getProperty:MSBuildAllProjects --getProperty:BaseIntermediateOutputPath"

        match Decode.fromString (cacheKeyDecoder options fsproj) json with
        | Error error -> return failwith $"%s{error}"
        | Ok p -> printf "%A" p
    }

// Execute
let fsproj =
    Path.Combine (__SOURCE_DIRECTORY__, "sample-project/App.fsproj") |> FileInfo

let cliArgs : CliArgs =
    {
        ProjectFile = fsproj.FullName
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

let options : CrackerOptions = CrackerOptions (cliArgs, false)

mkProjectCacheKey options fsproj |> Async.RunSynchronously

(*

- if any of the input file change, response file can be different
- impacted cracker options: Configuration, defines
- fable.compiler version

*)

// TODO: referenced projects
