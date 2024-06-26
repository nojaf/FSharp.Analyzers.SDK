namespace FSharp.Analyzers.SDK

type AnalysisResult =
    {
        AnalyzerName: string
        Output: Result<Message list, exn>
    }

[<Interface>]
type Logger =
    abstract member Error: string -> unit
    abstract member Verbose: string -> unit

type Client<'TAttribute, 'TContext when 'TAttribute :> AnalyzerAttribute and 'TContext :> Context> =
    new: logger: Logger * excludedAnalyzers: string Set -> Client<'TAttribute, 'TContext>
    new: unit -> Client<'TAttribute, 'TContext>
    /// <summary>
    /// Loads into private state any analyzers defined in any assembly
    /// matching `*Analyzer*.dll` in given directory (and any subdirectories)
    /// </summary>
    /// <returns>number of found dlls matching `*Analyzer*.dll` and number of registered analyzers</returns>
    member LoadAnalyzers: dir: string -> int * int
    /// <summary>Runs all registered analyzers for given context (file).</summary>
    /// <returns>list of messages. Ignores errors from the analyzers</returns>
    member RunAnalyzers: ctx: 'TContext -> Async<AnalyzerMessage list>
    /// <summary>Runs all registered analyzers for given context (file).</summary>
    /// <returns>list of results per analyzer which can either be messages or an exception.</returns>
    member RunAnalyzersSafely: ctx: 'TContext -> Async<AnalysisResult list>
