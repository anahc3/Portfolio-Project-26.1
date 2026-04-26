module Program

open System
open DataFetcher
open Portfolio
open Simulation

// ─── Configuration ────────────────────────────────────────────────────────────

let trainStart = DateTime(2025, 7, 1)
let trainEnd   = DateTime(2025, 12, 31)
let testStart  = DateTime(2025, 1, 1)
let testEnd    = DateTime(2025, 3, 31)

let nSelect      = 20
let nSimPerCombo = 1_000_000
let cacheFile    = "returns_cache.csv"

// ─── Helpers ──────────────────────────────────────────────────────────────────

let printResult (label: string) (r: PortfolioResult) =
    printfn "\n════════════════════════════════════════"
    printfn " %s" label
    printfn "════════════════════════════════════════"
    printfn " Sharpe Ratio:          %.4f" r.SharpeRatio
    printfn " Annualized Return:     %.2f%%" (r.AnnualizedReturn * 100.0)
    printfn " Annualized Volatility: %.2f%%" (r.AnnualizedVolatility * 100.0)
    printfn " Selected Tickers (%d):" r.Tickers.Length
    Array.zip r.Tickers r.Weights
    |> Array.sortByDescending snd
    |> Array.iter (fun (t, w) -> printfn "   %-6s  %.2f%%" t (w * 100.0))
    printfn "════════════════════════════════════════\n"

let getOrFetchData (cFile: string) (tickers: string array) (start: DateTime) (finish: DateTime) =
    match loadFromCSV cFile with
    | Some (t, m) ->
        printfn "Loaded cached data from %s (%d tickers, %d days)" cFile t.Length (Array2D.length1 m)
        t, m
    | None ->
        printfn "No cache found, fetching from Yahoo Finance..."
        let t, m = fetchAllReturns tickers start finish |> Async.RunSynchronously
        saveToCSV cFile t m
        printfn "Saved cache to %s" cFile
        t, m

// ─── Entry Point ──────────────────────────────────────────────────────────────

[<EntryPoint>]
let main argv =
    let mode = if argv.Length > 0 then argv.[0] else "optimize"

    printfn "╔══════════════════════════════════════════════╗"
    printfn "║     Portfolio Optimizer - Dow Jones 30       ║"
    printfn "║     Programação Funcional - Insper 2026      ║"
    printfn "╚══════════════════════════════════════════════╝"
    printfn "Mode: %s | CPUs: %d\n" mode Environment.ProcessorCount

    match mode with
    | "benchmark" ->
        let tickers, matrix = getOrFetchData cacheFile dowJonesTickers trainStart trainEnd
        benchmark tickers matrix nSelect 10_000 5 500

    | "test" ->
        let bestFile = "best_portfolio.txt"
        if not (IO.File.Exists bestFile) then
            printfn "Run 'optimize' first to generate best_portfolio.txt"
        else
            let lines        = IO.File.ReadAllLines bestFile
            let savedTickers = lines.[0].Split(',')
            let weights      = lines.[1].Split(',') |> Array.map float

            printfn "Testing best portfolio on Q1 2025 (out-of-sample)..."
            let testT, testM = getOrFetchData "returns_test_cache.csv" savedTickers testStart testEnd

            let alignedWeights =
                testT |> Array.map (fun t ->
                    match savedTickers |> Array.tryFindIndex ((=) t) with
                    | Some idx -> weights.[idx]
                    | None     -> 0.0)
            let wSum  = Array.sum alignedWeights
            let normW = if wSum > 0.0 then Array.map (fun w -> w / wSum) alignedWeights else alignedWeights

            let cov    = covarianceMatrix testM
            let result = evaluatePortfolio testT normW testM cov
            printResult "OUT-OF-SAMPLE TEST: Q1 2025" result

    | _ ->
        let tickers, matrix = getOrFetchData cacheFile dowJonesTickers trainStart trainEnd

        printfn "Training period: %s → %s" (trainStart.ToString("yyyy-MM-dd")) (trainEnd.ToString("yyyy-MM-dd"))
        printfn "Selecting %d from %d tickers" nSelect tickers.Length
        printfn "Simulations per combination: %s\n" (nSimPerCombo.ToString("N0"))

        let sw         = Diagnostics.Stopwatch.StartNew()
        let bestResult = runOptimization tickers matrix nSelect nSimPerCombo 1000
        sw.Stop()

        printfn "\nTotal optimization time: %.1f min (%.0f s)" sw.Elapsed.TotalMinutes sw.Elapsed.TotalSeconds

        match bestResult with
        | None -> printfn "No valid portfolio found!"
        | Some r ->
            printResult "BEST PORTFOLIO FOUND" r
            IO.File.WriteAllLines("best_portfolio.txt", [|
                r.Tickers |> String.concat ","
                r.Weights |> Array.map string |> String.concat ","
            |])
            printfn "Saved best portfolio to best_portfolio.txt"

    0