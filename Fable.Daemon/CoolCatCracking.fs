module Fable.Daemon.CoolCatCracking

open System
open System.Diagnostics
open System.IO
open System.Reflection
open System.Text.Json
open Fable
open Fable.Compiler.ProjectCracker

let fsharpFiles = set [| ".fs" ; ".fsi" ; ".fsx" |]

let isFSharpFile (file : string) =
    Set.exists (fun (ext : string) -> file.EndsWith (ext, StringComparison.Ordinal)) fsharpFiles


/// Add additional Fable arguments
let private mkOptions (projectFile : FileInfo) (compilerArgs : string array) : string array =
    let arguments =
        compilerArgs
        |> Array.map (fun (line : string) ->
            if not (isFSharpFile line) then
                line
            else
                Path.Combine (projectFile.DirectoryName, line)
        )

    [|
        yield! arguments
        yield "--define:FABLE_COMPILER"
        yield "--define:FABLE_COMPILER_4"
        yield "--define:FABLE_COMPILER_JAVASCRIPT"
    |]

type FullPath = string

let private dotnet_msbuild (fsproj : FullPath) (args : string) : Async<string> =
    backgroundTask {
        let psi = ProcessStartInfo "dotnet"
        let pwd = Assembly.GetEntryAssembly().Location |> Path.GetDirectoryName
        psi.WorkingDirectory <- pwd
        psi.Arguments <- $"msbuild \"%s{fsproj}\" %s{args}"
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.UseShellExecute <- false
        use ps = new Process ()
        ps.StartInfo <- psi
        ps.Start () |> ignore
        let output = ps.StandardOutput.ReadToEnd ()
        let error = ps.StandardError.ReadToEnd ()
        do! ps.WaitForExitAsync ()

        if not (String.IsNullOrWhiteSpace error) then
            failwithf $"In %s{pwd}:\ndotnet %s{args} failed with\n%s{error}"

        return output.Trim ()
    }
    |> Async.AwaitTask

let mkOptionsFromDesignTimeBuildAux (fsproj : FileInfo) (additionalArguments : string) : Async<ProjectOptionsResponse> =
    async {
        let! targetFrameworkJson =
            dotnet_msbuild fsproj.FullName "--getProperty:TargetFrameworks --getProperty:TargetFramework"

        let targetFramework =
            let tf, tfs =
                JsonDocument.Parse targetFrameworkJson
                |> fun json -> json.RootElement.GetProperty "Properties"
                |> fun properties ->
                    properties.GetProperty("TargetFramework").GetString (),
                    properties.GetProperty("TargetFrameworks").GetString ()

            if not (String.IsNullOrWhiteSpace tf) then
                tf
            else
                tfs.Split ';' |> Array.head

        let version = DateTime.UtcNow.Ticks % 3600L

        let properties =
            [
                "/p:Telplin=True"
                $"/p:TargetFramework=%s{targetFramework}"
                "/p:DesignTimeBuild=True"
                "/p:SkipCompilerExecution=True"
                "/p:ProvideCommandLineArgs=True"
                // See https://github.com/NuGet/Home/issues/13046
                "/p:RestoreUseStaticGraphEvaluation=False"
                // Avoid restoring with an existing lock file
                "/p:RestoreLockedMode=false"
                "/p:RestorePackagesWithLockFile=false"
                // We trick NuGet into believing there is no lock file create, if it does exist it will try and create it.
                " /p:NuGetLockFilePath=Telplin.lock"
                // Pass in a fake version to avoid skipping the CoreCompile target
                $"/p:Version=%i{version}"
            ]
            |> List.filter (String.IsNullOrWhiteSpace >> not)
            |> String.concat " "

        let targets =
            "ResolveAssemblyReferencesDesignTime,ResolveProjectReferencesDesignTime,ResolvePackageDependenciesDesignTime,FindReferenceAssembliesForReferences,_GenerateCompileDependencyCache,_ComputeNonExistentFileProperty,BeforeBuild,BeforeCompile,CoreCompile"

        let arguments =
            $"/restore /t:%s{targets} %s{properties} --getItem:FscCommandLineArgs %s{additionalArguments} --getItem:ProjectReference --getProperty:OutputType"

        let! json = dotnet_msbuild fsproj.FullName arguments
        let jsonDocument = JsonDocument.Parse json
        let items = jsonDocument.RootElement.GetProperty "Items"
        let properties = jsonDocument.RootElement.GetProperty "Properties"

        let options =
            items.GetProperty("FscCommandLineArgs").EnumerateArray ()
            |> Seq.map (fun arg -> arg.GetProperty("Identity").GetString ())
            |> Seq.toArray

        if Array.isEmpty options then
            return
                failwithf
                    $"Design time build for %s{fsproj.FullName} failed. CoreCompile was most likely skipped. `dotnet clean` might help here."
        else

        let options = mkOptions fsproj options

        let projectReferences =
            items.GetProperty("ProjectReference").EnumerateArray ()
            |> Seq.map (fun arg -> arg.GetProperty("FullPath").GetString ())
            |> Seq.toArray

        let outputType = properties.GetProperty("OutputType").GetString ()

        return
            {
                ProjectOptions = options
                ProjectReferences = projectReferences
                OutputType = Some outputType
                TargetFramework = Some targetFramework
            }
    }

let coolCatResolver : ProjectCrackerResolver =
    { new ProjectCrackerResolver with
        member x.GetProjectOptionsFromProjectFile (isMain, options, projectFile) =
            let fsproj = FileInfo projectFile

            if not fsproj.Exists then
                invalidArg (nameof fsproj) $"\"%s{fsproj.FullName}\" does not exist."

            // Bad I know...
            let result = mkOptionsFromDesignTimeBuildAux fsproj "" |> Async.RunSynchronously

            result
    }
