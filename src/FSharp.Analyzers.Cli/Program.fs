﻿open System
open System.IO
open System.Runtime.Loader
open System.Runtime.InteropServices
open System.Text.RegularExpressions
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Text
open Argu
open FSharp.Analyzers.SDK
open GlobExpressions
open Microsoft.CodeAnalysis.Sarif
open Microsoft.CodeAnalysis.Sarif.Writers
open Ionide.ProjInfo

type Arguments =
    | Project of string list
    | Analyzers_Path of string list
    | [<EqualsAssignment; AltCommandLine("-p:"); AltCommandLine("-p")>] Property of string * string
    | [<Unique; AltCommandLine("-c")>] Configuration of string
    | [<Unique; AltCommandLine("-r")>] Runtime of string
    | [<Unique; AltCommandLine("-a")>] Arch of string
    | [<Unique>] Os of string
    | [<Unique>] Treat_As_Info of string list
    | [<Unique>] Treat_As_Hint of string list
    | [<Unique>] Treat_As_Warning of string list
    | [<Unique>] Treat_As_Error of string list
    | [<Unique>] Ignore_Files of string list
    | [<Unique>] Exclude_Analyzer of string list
    | [<Unique>] Report of string
    | [<Unique>] FSC_Args of string
    | [<Unique>] Code_Root of string
    | [<Unique>] Verbose

    interface IArgParserTemplate with
        member s.Usage =
            match s with
            | Project _ -> "Path to your .fsproj file."
            | Analyzers_Path _ -> "Path to a folder where your analyzers are located."
            | Property _ -> "A key=value pair of an MSBuild property."
            | Configuration _ -> "The configuration to use, e.g. Debug or Release."
            | Runtime _ -> "The runtime identifier (RID)."
            | Arch _ -> "The target architecture."
            | Os _ -> "The target operating system."
            | Treat_As_Info _ ->
                "List of analyzer codes that should be treated as severity Info by the tool. Regardless of the original severity."
            | Treat_As_Hint _ ->
                "List of analyzer codes that should be treated as severity Hint by the tool. Regardless of the original severity."
            | Treat_As_Warning _ ->
                "List of analyzer codes that should be treated as severity Warning by the tool. Regardless of the original severity."
            | Treat_As_Error _ ->
                "List of analyzer codes that should be treated as severity Error by the tool. Regardless of the original severity."
            | Ignore_Files _ -> "Source files that shouldn't be processed."
            | Exclude_Analyzer _ -> "The names of analyzers that should not be executed."
            | Report _ -> "Write the result messages to a (sarif) report file."
            | Verbose -> "Verbose logging."
            | FSC_Args _ -> "Pass in the raw fsc compiler arguments. Cannot be combined with the `--project` flag."
            | Code_Root _ ->
                "Root of the current code repository, used in the sarif report to construct the relative file path. The current working directory is used by default."

type SeverityMappings =
    {
        TreatAsInfo: Set<string>
        TreatAsHint: Set<string>
        TreatAsWarning: Set<string>
        TreatAsError: Set<string>
    }

    member x.IsValid() =
        let allCodes = [ x.TreatAsInfo; x.TreatAsHint; x.TreatAsWarning; x.TreatAsError ]

        let unionCount = allCodes |> Set.unionMany |> Set.count
        let summedCount = allCodes |> List.sumBy Set.count
        summedCount = unionCount

let mapMessageToSeverity (mappings: SeverityMappings) (msg: FSharp.Analyzers.SDK.AnalyzerMessage) =
    let targetSeverity =
        if mappings.TreatAsInfo |> Set.contains msg.Message.Code then
            Info
        else if mappings.TreatAsHint |> Set.contains msg.Message.Code then
            Hint
        else if mappings.TreatAsWarning |> Set.contains msg.Message.Code then
            Warning
        else if mappings.TreatAsError |> Set.contains msg.Message.Code then
            Error
        else
            msg.Message.Severity

    { msg with
        Message =
            { msg.Message with
                Severity = targetSeverity
            }
    }

let mutable verbose = false

let fcs = Utils.createFCS None

let parser = ArgumentParser.Create<Arguments>(errorHandler = ProcessExiter())

let rec mkKn (ty: Type) =
    if Reflection.FSharpType.IsFunction(ty) then
        let _, ran = Reflection.FSharpType.GetFunctionElements(ty)
        let f = mkKn ran
        Reflection.FSharpValue.MakeFunction(ty, (fun _ -> f))
    else
        box ()

let origForegroundColor = Console.ForegroundColor

let printInfo (fmt: Printf.TextWriterFormat<'a>) : 'a =
    if verbose then
        Console.ForegroundColor <- ConsoleColor.DarkGray
        printf "Info : "
        Console.ForegroundColor <- origForegroundColor
        printfn fmt
    else
        unbox (mkKn typeof<'a>)

let printError (text: string) : unit =
    Console.ForegroundColor <- ConsoleColor.Red
    Console.Write "Error : "
    Console.WriteLine(text)
    Console.ForegroundColor <- origForegroundColor

let loadProject toolsPath properties projPath =
    async {
        let loader = WorkspaceLoader.Create(toolsPath, properties)
        let parsed = loader.LoadProjects [ projPath ] |> Seq.toList

        if parsed.IsEmpty then
            printError $"Failed to load project '{projPath}'"
            exit 1

        let fcsPo = FCS.mapToFSharpProjectOptions parsed.Head parsed

        return fcsPo
    }

let runProjectAux
    (client: Client<CliAnalyzerAttribute, CliContext>)
    (fsharpOptions: FSharpProjectOptions)
    (ignoreFiles: Glob list)
    (mappings: SeverityMappings)
    =
    async {
        let! checkProjectResults = fcs.ParseAndCheckProject(fsharpOptions)

        let! messagesPerAnalyzer =
            fsharpOptions.SourceFiles
            |> Array.filter (fun file ->
                match ignoreFiles |> List.tryFind (fun g -> g.IsMatch file) with
                | Some g ->
                    printInfo $"Ignoring file %s{file} for pattern %s{g.Pattern}"
                    false
                | None -> true
            )
            |> Array.choose (fun fileName ->
                let fileContent = File.ReadAllText fileName
                let sourceText = SourceText.ofString fileContent

                Utils.typeCheckFile fcs printError fsharpOptions fileName (Utils.SourceOfSource.SourceText sourceText)
                |> Option.map (Utils.createContext checkProjectResults fileName sourceText)
            )
            |> Array.map (fun ctx ->
                printInfo "Running analyzers for %s" ctx.FileName
                client.RunAnalyzers ctx
            )
            |> Async.Parallel

        return
            Some
                [
                    for messages in messagesPerAnalyzer do
                        let mappedMessages = messages |> List.map (mapMessageToSeverity mappings)
                        yield! mappedMessages
                ]
    }

let runProject
    (client: Client<CliAnalyzerAttribute, CliContext>)
    toolsPath
    properties
    proj
    (globs: Glob list)
    (mappings: SeverityMappings)
    =
    async {
        let path = Path.Combine(Environment.CurrentDirectory, proj) |> Path.GetFullPath
        let! option = loadProject toolsPath properties path
        return! runProjectAux client option globs mappings
    }

let fsharpFiles = set [| ".fs"; ".fsi"; ".fsx" |]

let isFSharpFile (file: string) =
    Seq.exists (fun (ext: string) -> file.EndsWith ext) fsharpFiles

let runFscArgs
    (client: Client<CliAnalyzerAttribute, CliContext>)
    (fscArgs: string)
    (globs: Glob list)
    (mappings: SeverityMappings)
    =
    if String.IsNullOrWhiteSpace fscArgs then
        printError "Empty --fsc-args were passed!"
        exit 1
    else

    let fscArgs = fscArgs.Split(';', StringSplitOptions.RemoveEmptyEntries)

    let sourceFiles =
        fscArgs
        |> Array.choose (fun (argument: string) ->
            // We make an absolute path because the sarif report cannot deal properly with relative path.
            let path = Path.Combine(Directory.GetCurrentDirectory(), argument)

            if not (isFSharpFile path) || not (File.Exists path) then
                None
            else
                Some path
        )

    let otherOptions = fscArgs |> Array.filter (fun line -> not (isFSharpFile line))

    let projectOptions =
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

    runProjectAux client projectOptions globs mappings

let printMessages (msgs: AnalyzerMessage list) =
    if verbose then
        printfn ""

    if verbose && List.isEmpty msgs then
        printfn "No messages found from the analyzer(s)"

    msgs
    |> Seq.iter (fun analyzerMessage ->
        let m = analyzerMessage.Message

        let color =
            match m.Severity with
            | Error -> ConsoleColor.Red
            | Warning -> ConsoleColor.DarkYellow
            | Info -> ConsoleColor.Blue
            | Hint -> ConsoleColor.Cyan

        Console.ForegroundColor <- color

        printfn
            "%s(%d,%d): %s %s - %s"
            m.Range.FileName
            m.Range.StartLine
            m.Range.StartColumn
            (m.Severity.ToString())
            m.Code
            m.Message

        Console.ForegroundColor <- origForegroundColor
    )

    ()

let writeReport (results: AnalyzerMessage list option) (codeRoot: string option) (report: string) =
    try
        let codeRoot =
            match codeRoot with
            | None -> Directory.GetCurrentDirectory() |> Uri
            | Some root -> Path.GetFullPath root |> Uri

        // Construct full path to ensure path separators are normalized.
        let report = Path.GetFullPath report
        // Ensure the parent directory exists
        let reportFile = FileInfo(report)
        reportFile.Directory.Create()

        let driver = ToolComponent()
        driver.Name <- "Ionide.Analyzers.Cli"
        driver.InformationUri <- Uri("https://ionide.io/FSharp.Analyzers.SDK/")
        driver.Version <- string (System.Reflection.Assembly.GetExecutingAssembly().GetName().Version)
        let tool = Tool()
        tool.Driver <- driver
        let run = Run()
        run.Tool <- tool

        use sarifLogger =
            new SarifLogger(
                report,
                logFilePersistenceOptions =
                    (FilePersistenceOptions.PrettyPrint ||| FilePersistenceOptions.ForceOverwrite),
                run = run,
                levels = BaseLogger.ErrorWarningNote,
                kinds = BaseLogger.Fail,
                closeWriterOnDispose = true
            )

        sarifLogger.AnalysisStarted()

        for analyzerResult in (Option.defaultValue List.empty results) do
            let reportDescriptor = ReportingDescriptor()
            reportDescriptor.Id <- analyzerResult.Message.Code
            reportDescriptor.Name <- analyzerResult.Message.Message

            analyzerResult.ShortDescription
            |> Option.iter (fun shortDescription ->
                reportDescriptor.ShortDescription <-
                    MultiformatMessageString(shortDescription, shortDescription, dict [])
            )

            analyzerResult.HelpUri
            |> Option.iter (fun helpUri -> reportDescriptor.HelpUri <- Uri(helpUri))

            let result = Result()
            result.RuleId <- reportDescriptor.Id

            result.Level <-
                match analyzerResult.Message.Severity with
                | Info -> FailureLevel.Note
                | Hint -> FailureLevel.Note
                | Warning -> FailureLevel.Warning
                | Error -> FailureLevel.Error

            let msg = Message()
            msg.Text <- analyzerResult.Message.Message
            result.Message <- msg

            let physicalLocation = PhysicalLocation()

            physicalLocation.ArtifactLocation <-
                let al = ArtifactLocation()
                al.Uri <- codeRoot.MakeRelativeUri(Uri(analyzerResult.Message.Range.FileName))
                al

            physicalLocation.Region <-
                let r = Region()
                r.StartLine <- analyzerResult.Message.Range.StartLine
                r.StartColumn <- analyzerResult.Message.Range.StartColumn + 1
                r.EndLine <- analyzerResult.Message.Range.EndLine
                r.EndColumn <- analyzerResult.Message.Range.EndColumn + 1
                r

            let location: Location = Location()
            location.PhysicalLocation <- physicalLocation
            result.Locations <- [| location |]

            sarifLogger.Log(reportDescriptor, result, System.Nullable())

        sarifLogger.AnalysisStopped(RuntimeConditions.None)

        sarifLogger.Dispose()
    with ex ->
        let details = if not verbose then "" else $" %A{ex}"
        printfn $"Could not write sarif to %s{report}%s{details}"

let calculateExitCode (msgs: AnalyzerMessage list option) : int =
    match msgs with
    | None -> -1
    | Some msgs ->
        let check =
            msgs
            |> List.exists (fun analyzerMessage ->
                let message = analyzerMessage.Message

                message.Severity = Error
            )

        if check then -2 else 0

/// If multiple MSBuild properties are given in one -p flag like -p:prop1="val1a;val1b;val1c";prop2="1;2;3";prop3=val3
/// argu will think it means prop1 has the value: "val1a;val1b;val1c";prop2="1;2;3";prop3=val3
/// so this function expands the value into multiple key-value properties
let expandMultiProperties (properties: (string * string) list) =
    properties
    |> List.map (fun (k, v) ->
        if not (v.Contains('=')) then // no multi properties given to expand
            [ (k, v) ]
        else
            let regex = Regex(";([a-z,A-Z,0-9,_,-]*)=")
            let splits = regex.Split(v)

            [
                yield (k, splits[0])

                for pair in splits.[1..] |> Seq.chunkBySize 2 do
                    match pair with
                    | [| k; v |] when String.IsNullOrWhiteSpace(v) ->
                        printError $"Missing property value for '{k}'"
                        exit 1
                    | [| k; v |] -> yield (k, v)
                    | _ -> ()

            ]
    )
    |> List.concat

let validateRuntimeOsArchCombination (runtime, arch, os) =
    match runtime, os, arch with
    | Some _, Some _, _ ->
        printError "Specifying both the `-r|--runtime` and `-os` options is not supported."
        exit 1
    | Some _, _, Some _ ->
        printError "Specifying both the `-r|--runtime` and `-a|--arch` options is not supported."
        exit 1
    | _ -> ()

let getProperties (results: ParseResults<Arguments>) =
    let runtime = results.TryGetResult <@ Runtime @>
    let arch = results.TryGetResult <@ Arch @>
    let os = results.TryGetResult <@ Os @>
    validateRuntimeOsArchCombination (runtime, os, arch)

    let runtimeProp =
        let rid = RuntimeInformation.RuntimeIdentifier // assuming we always get something like 'linux-x64'

        match runtime, os, arch with
        | Some r, _, _ -> Some r
        | None, Some o, Some a -> Some $"{o}-{a}"
        | None, Some o, None ->
            let archOfRid = rid.Substring(rid.LastIndexOf('-') + 1)
            Some $"{o}-{archOfRid}"
        | None, None, Some a ->
            let osOfRid = rid.Substring(0, rid.LastIndexOf('-'))
            Some $"{osOfRid}-{a}"
        | _ -> None

    results.GetResults <@ Property @>
    |> expandMultiProperties
    |> fun props ->
        [
            yield! props

            match results.TryGetResult <@ Configuration @> with
            | (Some x) -> yield ("Configuration", x)
            | _ -> ()

            match runtimeProp with
            | (Some x) -> yield ("RuntimeIdentifier", x)
            | _ -> ()
        ]

[<EntryPoint>]
let main argv =
    let toolsPath = Init.init (DirectoryInfo Environment.CurrentDirectory) None

    let results = parser.ParseCommandLine argv
    verbose <- results.Contains <@ Verbose @>
    printInfo "Running in verbose mode"

    let severityMapping =
        {
            TreatAsHint = results.GetResult(<@ Treat_As_Hint @>, []) |> Set.ofList
            TreatAsInfo = results.GetResult(<@ Treat_As_Info @>, []) |> Set.ofList
            TreatAsWarning = results.GetResult(<@ Treat_As_Warning @>, []) |> Set.ofList
            TreatAsError = results.GetResult(<@ Treat_As_Error @>, []) |> Set.ofList
        }

    printInfo "Treat as Hints: [%s]" (severityMapping.TreatAsHint |> String.concat ", ")
    printInfo "Treat as Info: [%s]" (severityMapping.TreatAsInfo |> String.concat ", ")
    printInfo "Treat as Warning: [%s]" (severityMapping.TreatAsWarning |> String.concat ", ")
    printInfo "Treat as Error: [%s]" (severityMapping.TreatAsError |> String.concat ", ")

    if not (severityMapping.IsValid()) then
        printError "An analyzer code may only be listed once in the <treat-as-severity> arguments."

        exit 1

    let projOpts = results.GetResults <@ Project @> |> List.concat
    let fscArgs = results.TryGetResult <@ FSC_Args @>
    let report = results.TryGetResult <@ Report @>
    let codeRoot = results.TryGetResult <@ Code_Root @>
    let ignoreFiles = results.GetResult(<@ Ignore_Files @>, [])
    printInfo "Ignore Files: [%s]" (ignoreFiles |> String.concat ", ")
    let ignoreFiles = ignoreFiles |> List.map Glob
    let properties = getProperties results

    if Option.isSome fscArgs && not properties.IsEmpty then
        printError "fsc-args can't be combined with MSBuild properties."
        exit 1

    if verbose then
        properties |> List.iter (fun (k, v) -> printInfo $"Property %s{k}=%s{v}")

    let analyzersPaths =
        results.GetResults(<@ Analyzers_Path @>)
        |> List.concat
        |> function
            | [] -> [ "packages/Analyzers" ]
            | paths -> paths
        |> List.map (fun path ->
            if Path.IsPathRooted path then
                path
            else
                Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, path))
        )

    printInfo "Loading analyzers from %s" (String.concat ", " analyzersPaths)

    let excludeAnalyzers = results.GetResult(<@ Exclude_Analyzer @>, [])

    let logger =
        { new Logger with
            member _.Error msg = printError msg

            member _.Verbose msg =
                if verbose then
                    printInfo "%s" msg
        }

    AssemblyLoadContext.Default.add_Resolving (fun _ctx assemblyName ->
        if assemblyName.Name <> "FSharp.Core" then
            null
        else

        let msg =
            $"""Could not load FSharp.Core %A{assemblyName.Version}. The expected assembly version of FSharp.Core is %A{Utils.currentFSharpCoreVersion}.
        Consider adding <PackageReference Update="FSharp.Core" Version="<CorrectVersion>" /> to your .fsproj.
        The correct version can be found over at https://www.nuget.org/packages/FSharp.Analyzers.SDK#dependencies-body-tab.
        """

        printError msg
        exit 1
    )

    let client =
        Client<CliAnalyzerAttribute, CliContext>(logger, Set.ofList excludeAnalyzers)

    let dlls, analyzers =
        ((0, 0), analyzersPaths)
        ||> List.fold (fun (accDlls, accAnalyzers) analyzersPath ->
            let dlls, analyzers = client.LoadAnalyzers analyzersPath
            (accDlls + dlls), (accAnalyzers + analyzers)
        )

    printInfo "Registered %d analyzers from %d dlls" analyzers dlls

    let results =
        if analyzers = 0 then
            Some []
        else
            match projOpts, fscArgs with
            | [], None ->
                printError "No project given. Use `--project PATH_TO_FSPROJ`."

                None
            | _ :: _, Some _ ->
                printError "`--project` and `--fsc-args` cannot be combined."
                exit 1
            | [], Some fscArgs -> runFscArgs client fscArgs ignoreFiles severityMapping |> Async.RunSynchronously
            | projects, None ->
                for projPath in projects do
                    if not (File.Exists(projPath)) then
                        printError $"Invalid `--project` argument. File does not exist: '{projPath}'"
                        exit 1

                projects
                |> List.map (fun projPath ->
                    runProject client toolsPath properties projPath ignoreFiles severityMapping
                )
                |> Async.Sequential
                |> Async.RunSynchronously
                |> Array.choose id
                |> List.concat
                |> Some

    results |> Option.iter printMessages
    report |> Option.iter (writeReport results codeRoot)

    calculateExitCode results
