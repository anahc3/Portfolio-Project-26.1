module Simulation

open System
open System.Threading
open Portfolio

/// Generate all combinations of k elements from [0..n-1]
let combinations (n: int) (k: int) : int array seq =
    seq {
        let indices = Array.init k id  // [0, 1, ..., k-1]
        yield Array.copy indices
        let mutable running = true
        while running do
            let mutable i = k - 1
            while i >= 0 && indices.[i] = i + n - k do
                i <- i - 1
            if i < 0 then
                running <- false
            else
                indices.[i] <- indices.[i] + 1
                for j in i + 1 .. k - 1 do
                    indices.[j] <- indices.[j - 1] + 1
                yield Array.copy indices
    }

/// Pure: funções auxiliares do pipeline
let avaliarCarteira (tickers: string array) (subMat: float[,]) (cov: float[,]) (weights: float array) =
    evaluatePortfolio tickers weights subMat cov

let carteiraValida (r: PortfolioResult) =
    r.SharpeRatio > 0.0

/// Pure: simula nSim carteiras para uma combinação e retorna a melhor.
/// Cada chamada recebe seu próprio seed — sem estado global, função pura.
let simulateBestPortfolio
    (seed: int)
    (nSim: int)
    (tickers: string array)
    (subMat: float[,])
    (cov: float[,]) : PortfolioResult option =

    let rng           = Random(seed)
    let n             = tickers.Length
    let avaliar       = avaliarCarteira tickers subMat cov
    let mutable best  : PortfolioResult option = None
    let mutable bestSharpe = Double.NegativeInfinity

    // Pipeline por simulação: gera pesos → avalia → filtra carteiraValida → atualiza melhor
    for _ in 1 .. nSim do
        match generateValidWeights rng n with
        | None -> ()
        | Some weights ->
            let r = avaliar weights          // avalia — puro
            if carteiraValida r && r.SharpeRatio > bestSharpe then
                bestSharpe <- r.SharpeRatio
                best       <- Some r
    best

/// Async wrapper — envelopa o cálculo puro para usar com Async.Parallel
let simulateBestPortfolioAsync
    (seed: int)
    (nSim: int)
    (tickers: string array)
    (subMat: float[,])
    (cov: float[,]) : Async<PortfolioResult option> =
    async {
        return simulateBestPortfolio seed nSim tickers subMat cov
    }

/// Run full optimization — paralelo via Async.Parallel (padrão funcional F#)
let runOptimization
    (allTickers: string array)
    (returnsMatrix: float[,])
    (nSelect: int)
    (nSimPerCombo: int)
    (progressInterval: int) : PortfolioResult option =

    let nTotal = allTickers.Length
    let combos = combinations nTotal nSelect |> Seq.toArray

    printfn "Total combinations C(%d,%d) = %d" nTotal nSelect combos.Length
    printfn "Simulations per combination: %d" nSimPerCombo
    printfn "Total simulations: %d" (int64 combos.Length * int64 nSimPerCombo)
    printfn "CPUs available: %d" Environment.ProcessorCount
    printfn "Starting parallel Monte Carlo optimization..."

    let mutable processed = 0

    // Async.Parallel — padrão funcional F#, cada async é puro
    let results =
        combos
        |> Array.mapi (fun i indices ->
            async {
                let subTickers = indices |> Array.map (fun idx -> allTickers.[idx])
                let subMat     = subMatrix returnsMatrix indices
                let cov        = covarianceMatrix subMat
                let result     = simulateBestPortfolio i nSimPerCombo subTickers subMat cov

                let current = Interlocked.Increment(&processed)
                if current % progressInterval = 0 || current = combos.Length then
                    let pct = float current / float combos.Length * 100.0
                    printfn "[%s] Progress: %d/%d (%.1f%%)"
                        (DateTime.Now.ToString("HH:mm:ss")) current combos.Length pct

                return result
            })
        |> Async.Parallel
        |> Async.RunSynchronously

    // Pipeline funcional: filtra válidas → pega a melhor
    results
    |> Array.choose id
    |> Array.filter carteiraValida
    |> function
       | [||] -> None
       | valid -> valid |> Array.maxBy (fun r -> r.SharpeRatio) |> Some

/// Sequential version for benchmarking comparison
let runOptimizationSequential
    (allTickers: string array)
    (returnsMatrix: float[,])
    (nSelect: int)
    (nSimPerCombo: int)
    (progressInterval: int) : PortfolioResult option =

    let nTotal = allTickers.Length
    let combos = combinations nTotal nSelect |> Seq.toArray

    printfn "[SEQUENTIAL] Starting sequential optimization..."

    let mutable bestSharpe = Double.NegativeInfinity
    let mutable bestResult : PortfolioResult option = None
    let mutable processed  = 0

    for i in 0 .. combos.Length - 1 do
        let indices    = combos.[i]
        let subTickers = indices |> Array.map (fun idx -> allTickers.[idx])
        let subMat     = subMatrix returnsMatrix indices
        let cov        = covarianceMatrix subMat
        let result     = simulateBestPortfolio i nSimPerCombo subTickers subMat cov

        match result with
        | Some r ->
            if carteiraValida r && r.SharpeRatio > bestSharpe then
                bestSharpe <- r.SharpeRatio
                bestResult <- Some r
        | None -> ()

        processed <- processed + 1
        if processed % progressInterval = 0 then
            printfn "[SEQ] Progress: %d/%d" processed combos.Length

    bestResult

/// Benchmark: sequencial PRIMEIRO (hardware frio), paralelo depois
let benchmark
    (allTickers: string array)
    (returnsMatrix: float[,])
    (nSelect: int)
    (nSimPerCombo: int)
    (nRuns: int)
    (subsetSize: int) : unit =

    let combos = combinations allTickers.Length nSelect |> Seq.truncate subsetSize |> Seq.toArray

    printfn "\n=== BENCHMARK: %d runs, %d combinations, %d sims each ===" nRuns subsetSize nSimPerCombo

    let runSeq () =
        let sw = Diagnostics.Stopwatch.StartNew()
        for i in 0 .. combos.Length - 1 do
            let indices    = combos.[i]
            let subTickers = indices |> Array.map (fun idx -> allTickers.[idx])
            let subMat     = subMatrix returnsMatrix indices
            let cov        = covarianceMatrix subMat
            simulateBestPortfolio i nSimPerCombo subTickers subMat cov |> ignore
        sw.Elapsed.TotalSeconds

    let runPar () =
        let sw = Diagnostics.Stopwatch.StartNew()
        combos
        |> Array.mapi (fun i indices ->
            async {
                let subTickers = indices |> Array.map (fun idx -> allTickers.[idx])
                let subMat     = subMatrix returnsMatrix indices
                let cov        = covarianceMatrix subMat
                return simulateBestPortfolio i nSimPerCombo subTickers subMat cov
            })
        |> Async.Parallel
        |> Async.RunSynchronously
        |> ignore
        sw.Elapsed.TotalSeconds

    // Sequencial primeiro (hardware frio), paralelo depois
    printfn "\n→ Modo SEQUENCIAL:"
    let sequentialTimes = Array.init nRuns (fun i ->
        let t = runSeq ()
        printfn "  [SEQ] run %d: %.2f s" (i + 1) t
        t)

    printfn "\n→ Modo PARALELO:"
    let parallelTimes = Array.init nRuns (fun i ->
        let t = runPar ()
        printfn "  [PAR] run %d: %.2f s" (i + 1) t
        t)

    let avgParallel   = Array.average parallelTimes
    let avgSequential = Array.average sequentialTimes
    let speedup       = avgSequential / avgParallel

    printfn "\n════════════════════════════════════════════════"
    printfn " RESULTADOS (%d runs, %d combos × %d sims)" nRuns subsetSize nSimPerCombo
    printfn "════════════════════════════════════════════════"
    printfn " Sequencial : média = %.2f s | min = %.2f s | max = %.2f s"
        avgSequential (Array.min sequentialTimes) (Array.max sequentialTimes)
    printfn " Paralelo   : média = %.2f s | min = %.2f s | max = %.2f s"
        avgParallel (Array.min parallelTimes) (Array.max parallelTimes)
    printfn " Speedup    : %.2fx" speedup
    printfn " CPUs       : %d" Environment.ProcessorCount
    printfn "════════════════════════════════════════════════"