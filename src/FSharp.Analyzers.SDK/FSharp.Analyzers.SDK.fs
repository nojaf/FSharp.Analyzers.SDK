namespace FSharp.Analyzers.SDK

open System
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Symbols
open FSharp.Compiler.EditorServices
open System.Runtime.InteropServices
open FSharp.Compiler.Text

module EntityCache =
    let private entityCache = EntityCache()

    let getEntities (publicOnly: bool) (checkFileResults: FSharpCheckFileResults) =
        try
            let res =
                [
                    yield!
                        AssemblyContent.GetAssemblySignatureContent
                            AssemblyContentType.Full
                            checkFileResults.PartialAssemblySignature
                    let ctx = checkFileResults.ProjectContext

                    let assembliesByFileName =
                        ctx.GetReferencedAssemblies()
                        |> Seq.groupBy (fun asm -> asm.FileName)
                        |> Seq.map (fun (fileName, asms) -> fileName, List.ofSeq asms)
                        |> Seq.toList
                        |> List.rev // if mscorlib.dll is the first then FSC raises exception when we try to
                    // get Content.Entities from it.

                    for fileName, signatures in assembliesByFileName do
                        let contentType =
                            if publicOnly then
                                AssemblyContentType.Public
                            else
                                AssemblyContentType.Full

                        let content =
                            AssemblyContent.GetAssemblyContent entityCache.Locking contentType fileName signatures

                        yield! content
                ]

            res
        with _ ->
            []

[<AbstractClass>]
[<AttributeUsage(AttributeTargets.Method ||| AttributeTargets.Property ||| AttributeTargets.Field)>]
type AnalyzerAttribute([<Optional; DefaultParameterValue("Analyzer" :> obj)>] name: string) =
    inherit Attribute()
    member val Name: string = name

[<AttributeUsage(AttributeTargets.Method ||| AttributeTargets.Property ||| AttributeTargets.Field)>]
type CliAnalyzerAttribute([<Optional; DefaultParameterValue "Analyzer">] name: string) =
    inherit AnalyzerAttribute(name)

    member _.Name = name

[<AttributeUsage(AttributeTargets.Method ||| AttributeTargets.Property ||| AttributeTargets.Field)>]
type EditorAnalyzerAttribute([<Optional; DefaultParameterValue "Analyzer">] name: string) =
    inherit AnalyzerAttribute(name)

    member _.Name = name

type Context =
    interface
    end

type CliContext =
    {
        FileName: string
        SourceText: ISourceText
        ParseFileResults: FSharpParseFileResults
        CheckFileResults: FSharpCheckFileResults
        TypedTree: FSharpImplementationFileContents
        CheckProjectResults: FSharpCheckProjectResults
    }

    interface Context

    member x.GetAllEntities(publicOnly: bool) =
        EntityCache.getEntities publicOnly x.CheckFileResults

    member x.GetAllSymbolUsesOfProject() =
        x.CheckProjectResults.GetAllUsesOfAllSymbols()

    member x.GetAllSymbolUsesOfFile() =
        x.CheckFileResults.GetAllUsesOfAllSymbolsInFile()

type EditorContext =
    {
        FileName: string
        SourceText: ISourceText option
        ParseFileResults: FSharpParseFileResults option
        CheckFileResults: FSharpCheckFileResults option
        TypedTree: FSharpImplementationFileContents option
        CheckProjectResults: FSharpCheckProjectResults option
    }

    interface Context

    member x.GetAllEntities(publicOnly: bool) : AssemblySymbol list =
        match x.CheckFileResults with
        | None -> List.empty
        | Some checkFileResults -> EntityCache.getEntities publicOnly checkFileResults

    member x.GetAllSymbolUsesOfProject() : FSharpSymbolUse array =
        match x.CheckProjectResults with
        | None -> Array.empty
        | Some checkProjectResults -> checkProjectResults.GetAllUsesOfAllSymbols()

    member x.GetAllSymbolUsesOfFile() : FSharpSymbolUse seq =
        match x.CheckFileResults with
        | None -> Seq.empty
        | Some checkFileResults -> checkFileResults.GetAllUsesOfAllSymbolsInFile()

type Fix =
    {
        FromRange: range
        FromText: string
        ToText: string
    }

type Severity =
    | Info
    | Hint
    | Warning
    | Error

type Message =
    {
        Type: string
        Message: string
        Code: string
        Severity: Severity
        Range: range
        Fixes: Fix list
    }

type Analyzer<'TContext> = 'TContext -> Message list
