module FSharp.Analyzers.SDK.TestHelpers

#nowarn "57"

open FSharp.Compiler.Text
open Microsoft.Build.Logging.StructuredLogger
open CliWrap
open System
open System.IO
open FSharp.Compiler.CodeAnalysis

type FSharpProjectOptions with

    static member zero =
        {
            ProjectFileName = ""
            ProjectId = None
            SourceFiles = [||]
            OtherOptions = [||]
            ReferencedProjects = [||]
            IsIncompleteTypeCheckEnvironment = false
            UseScriptResolutionRules = false
            LoadTime = DateTime.UtcNow
            UnresolvedReferences = None
            OriginalLoadReferences = []
            Stamp = None
        }

type Package =
    {
        Name: string
        Version: string
    }

    override x.ToString() = $"{x.Name}_{x.Version}"

let fsharpFiles = set [| ".fs"; ".fsi"; ".fsx" |]

let isFSharpFile (file: string) =
    Seq.exists (fun (ext: string) -> file.EndsWith ext) fsharpFiles

let readCompilerArgsFromBinLog file =
    let build = BinaryLog.ReadBuild file

    if not build.Succeeded then
        failwith $"Build failed: {file}"

    let projectName =
        build.Children
        |> Seq.choose (
            function
            | :? Project as p -> Some p.Name
            | _ -> None
        )
        |> Seq.distinct
        |> Seq.exactlyOne

    let message (fscTask: FscTask) =
        fscTask.Children
        |> Seq.tryPick (
            function
            | :? Message as m when m.Text.Contains "fsc" -> Some m.Text
            | _ -> None
        )

    let mutable args = None

    build.VisitAllChildren<Task>(fun task ->
        match task with
        | :? FscTask as fscTask ->
            match fscTask.Parent.Parent with
            | :? Project as p when p.Name = projectName -> args <- message fscTask
            | _ -> ()
        | _ -> ()
    )

    match args with
    | None -> failwith $"Could not parse binlog at {file}, does it contain CoreCompile?"
    | Some args ->
        let idx = args.IndexOf "-o:"
        args.Substring(idx).Split [| '\n' |]

let mkOptions (compilerArgs: string array) =
    let sourceFiles =
        compilerArgs
        |> Array.filter (fun (line: string) -> isFSharpFile line && File.Exists line)

    let otherOptions =
        compilerArgs |> Array.filter (fun line -> not (isFSharpFile line))

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

let mkOptionsFromBinaryLog binLogPath =
    let compilerArgs = readCompilerArgsFromBinLog binLogPath
    mkOptions compilerArgs

let createProject (binLogPath: string) (tmpProjectDir: string) (framework: string) (additionalPkgs: Package list) =
    let stdOutBuffer = System.Text.StringBuilder()
    let stdErrBuffer = System.Text.StringBuilder()

    task {
        try
            Directory.CreateDirectory(tmpProjectDir) |> ignore

            // Todo remove
            let! _ =
                Cli
                    .Wrap("dotnet")
                    .WithWorkingDirectory(tmpProjectDir)
                    .WithArguments("--version")
                    .WithStandardOutputPipe(PipeTarget.ToStringBuilder(stdOutBuffer))
                    .WithStandardErrorPipe(PipeTarget.ToStringBuilder(stdErrBuffer))
                    .WithValidation(CommandResultValidation.ZeroExitCode)
                    .ExecuteAsync()

            let! _ =
                Cli
                    .Wrap("dotnet")
                    .WithWorkingDirectory(tmpProjectDir)
                    .WithArguments($"new classlib -f {framework} -lang F#")
                    .WithStandardOutputPipe(PipeTarget.ToStringBuilder(stdOutBuffer))
                    .WithStandardErrorPipe(PipeTarget.ToStringBuilder(stdErrBuffer))
                    .WithValidation(CommandResultValidation.ZeroExitCode)
                    .ExecuteAsync()

            // Todo remove
            let! _ =
                Cli
                    .Wrap("dotnet")
                    .WithWorkingDirectory(tmpProjectDir)
                    .WithArguments("--version")
                    .WithStandardOutputPipe(PipeTarget.ToStringBuilder(stdOutBuffer))
                    .WithStandardErrorPipe(PipeTarget.ToStringBuilder(stdErrBuffer))
                    .WithValidation(CommandResultValidation.ZeroExitCode)
                    .ExecuteAsync()

            for p in additionalPkgs do
                let! _ =
                    Cli
                        .Wrap("dotnet")
                        .WithWorkingDirectory(tmpProjectDir)
                        .WithArguments($"add package {p.Name} --version {p.Version}")
                        .WithStandardOutputPipe(PipeTarget.ToStringBuilder(stdOutBuffer))
                        .WithStandardErrorPipe(PipeTarget.ToStringBuilder(stdErrBuffer))
                        .WithValidation(CommandResultValidation.ZeroExitCode)
                        .ExecuteAsync()

                ()

            let! _ =
                Cli
                    .Wrap("dotnet")
                    .WithWorkingDirectory(tmpProjectDir)
                    .WithArguments($"build -bl:{binLogPath}")
                    .WithStandardOutputPipe(PipeTarget.ToStringBuilder(stdOutBuffer))
                    .WithStandardErrorPipe(PipeTarget.ToStringBuilder(stdErrBuffer))
                    .WithValidation(CommandResultValidation.ZeroExitCode)
                    .ExecuteAsync()

            return ()
        with e ->
            printfn $"StdOut:\n%s{stdOutBuffer.ToString()}"
            printfn $"StdErr:\n%s{stdErrBuffer.ToString()}"
            printfn $"Exception:\n%s{e.ToString()}"
    }

let mkOptionsFromProject (framework: string) (additionalPkgs: Package list) =
    task {
        try
            let id = Guid.NewGuid().ToString("N")
            let tmpProjectDir = Path.Combine(Path.GetTempPath(), id)

            let uniqueBinLogName =
                let packages =
                    additionalPkgs |> List.map (fun p -> p.ToString()) |> String.concat "_"

                $"v{Utils.currentFSharpAnalyzersSDKVersion}_{framework}_{packages}.binlog"

            let binLogCache =
                Path.Combine(Path.GetTempPath(), "FSharp.Analyzer.SDK.BinLogCache")

            let binLogPath = Path.Combine(binLogCache, uniqueBinLogName)

            if not (File.Exists(binLogPath)) then
                Directory.CreateDirectory(binLogCache) |> ignore
                let! _ = createProject binLogPath tmpProjectDir framework additionalPkgs
                ()

            return mkOptionsFromBinaryLog binLogPath
        with e ->
            printfn $"Exception:\n%s{e.ToString()}"
            return FSharpProjectOptions.zero
    }

let getContext (opts: FSharpProjectOptions) source =
    let fileName = "A.fs"
    let files = Map.ofArray [| (fileName, SourceText.ofString source) |]

    let documentSource fileName =
        Map.tryFind fileName files |> async.Return

    let fcs = Utils.createFCS (Some documentSource)
    let printError (s: string) = Console.WriteLine(s)
    let pathToAnalyzerDlls = Path.GetFullPath(".")

    let foundDlls, registeredAnalyzers =
        Client.loadAnalyzers printError pathToAnalyzerDlls

    if foundDlls = 0 then
        failwith $"no Dlls found in {pathToAnalyzerDlls}"

    if registeredAnalyzers = 0 then
        failwith $"no Analyzers found in {pathToAnalyzerDlls}"

    let opts =
        { opts with
            SourceFiles = [| fileName |]
        }

    fcs.NotifyFileChanged(fileName, opts) |> Async.RunSynchronously // workaround for https://github.com/dotnet/fsharp/issues/15960
    let checkProjectResults = fcs.ParseAndCheckProject(opts) |> Async.RunSynchronously
    let allSymbolUses = checkProjectResults.GetAllUsesOfAllSymbols()

    if Array.isEmpty allSymbolUses then
        failwith "no symboluses"

    match Utils.typeCheckFile fcs (Utils.SourceOfSource.DiscreteSource source, fileName, opts) with
    | Some(file, text, parseRes, result) ->
        let ctx =
            Utils.createContext (checkProjectResults, allSymbolUses) (file, text, parseRes, result)

        match ctx with
        | Some c -> c
        | None -> failwith "Context creation failed"
    | None -> failwith "typechecking file failed"
