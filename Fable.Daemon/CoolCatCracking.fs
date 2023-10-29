module Fable.Daemon.CoolCatCracking

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open FSharp.Compiler.CodeAnalysis
open Fable
open Fable.Compiler.Service.Util

let fsharpFiles = set [| ".fs" ; ".fsi" ; ".fsx" |]

let isFSharpFile (file : string) =
    Seq.exists (fun (ext : string) -> file.EndsWith ext) fsharpFiles

let mkOptions (fsproj : string) (compilerArgs : string array) =
    let projectDir = Path.GetDirectoryName fsproj

    let sourceFiles =
        compilerArgs
        |> Array.choose (fun (line : string) ->
            let filePath = Path.Combine (projectDir, line)

            if isFSharpFile line && File.Exists filePath && not (filePath.Contains "obj") then
                Some (Path.normalizeFullPath filePath)
            else
                None
        )

    let otherOptions =
        [|
            yield! compilerArgs |> Array.filter (fun line -> not (isFSharpFile line))
            yield "--define:FABLE_COMPILER"
            yield "--define:FABLE_COMPILER_4"
            yield "--define:FABLE_COMPILER_JAVASCRIPT"
        |]

    {
        ProjectFileName = "Project"
        ProjectId = None
        SourceFiles = sourceFiles
        OtherOptions = otherOptions
        ReferencedProjects = [||]
        IsIncompleteTypeCheckEnvironment = false
        UseScriptResolutionRules = false
        LoadTime = DateTime.Now
        UnresolvedReferences = None
        OriginalLoadReferences = []
        Stamp = None
    }

let dotnet pwd args =
    let psi = ProcessStartInfo "dotnet"
    psi.WorkingDirectory <- pwd
    psi.Arguments <- args
    psi.RedirectStandardOutput <- true
    psi.UseShellExecute <- false
    use ps = new Process ()
    ps.StartInfo <- psi
    ps.Start () |> ignore
    let output = ps.StandardOutput.ReadToEnd ()
    ps.WaitForExit ()
    output.Trim ()

let mkOptionsFromDesignTimeBuild (fsproj : string) (additionalArguments : string) =
    if not (File.Exists fsproj) then
        invalidArg (nameof fsproj) $"\"%s{fsproj}\" does not exist."

    // Move fsproj to temp folder
    let pwd = Path.Combine (Path.GetTempPath (), Guid.NewGuid().ToString "N")

    try
        let dir = DirectoryInfo pwd
        dir.Create ()

        let version = dotnet pwd "--version"

        if version <> "8.0.100-rc.2.23502.2" then
            failwith $"Expected the SDK to be 8.0.100-rc.2.23502.2 in %s{pwd}"

        let tmpFsproj = Path.Combine (pwd, Path.GetFileName fsproj)
        File.Copy (fsproj, tmpFsproj)

        let targets =
            "Restore,ResolveAssemblyReferencesDesignTime,ResolveProjectReferencesDesignTime,ResolvePackageDependenciesDesignTime,FindReferenceAssembliesForReferences,_GenerateCompileDependencyCache,_ComputeNonExistentFileProperty,BeforeBuild,BeforeCompile,CoreCompile"

        let json =
            dotnet
                pwd
                $"msbuild /t:%s{targets} /p:DesignTimeBuild=True /p:SkipCompilerExecution=True /p:ProvideCommandLineArgs=True --getItem:FscCommandLineArgs %s{additionalArguments}"

        let jsonDocument = JsonDocument.Parse json

        let options =
            jsonDocument.RootElement
            |> fun root -> root.GetProperty("Items").GetProperty("FscCommandLineArgs").EnumerateArray ()
            |> Seq.map (fun arg -> arg.GetProperty("Identity").GetString ())
            |> Seq.toArray

        mkOptions fsproj options
    finally
        if Directory.Exists pwd then
            Directory.Delete (pwd, true)
