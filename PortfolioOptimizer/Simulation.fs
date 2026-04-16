module Simulation

open System
open System.Threading
open System.Threading.Tasks
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

/// Pure: simula nSim carteiras para uma combinação e retorna a melhor
/// Cada chamada recebe seu próprio seed — sem estado global, função pura
let simulateBestPortfolio
    (seed: int)
    (nSim: int)
    (tickers: string array)
    (subMat: float[,])
    (cov: float[,]) : PortfolioResult option =

    let rng = Random(seed)
    let n   = tickers.Length

    // Pipeline funcional explícito:
    // gera pesos → avalia carteira → filtra válidas (Sharpe > 0) → pega a melhor
    let avaliarCarteira weights =
        evaluatePortfolio tickers weights subMat cov

    let carteiraValida (r: PortfolioResult) =
        r.SharpeRatio > 0.0

    Array.init nSim (fun _ -> generateValidWeights rng n)
    |> Array.choose id                           // descarta pesos inválidos (None)
    |> Array.map avaliarCarteira                 // avalia cada carteira — puro
    |> Array.filter carteiraValida              // filtra carteiras com Sharpe positivo
    |> function
       | [||] -> None
       | valid -> valid |> Array.maxBy (fun r -> r.SharpeRatio) |> Some

/// Run full optimization in parallel across all combinations
let runOptimization
    (allTickers: string array)
    (returnsMatrix: float[,])
    (nSelect: int)
    (nSimPerCombo: int)
    (parallelDegree: int)
    (progressInterval: int) : PortfolioResult option =

    let nTotal = allTickers.Length
    let combos = combinations nTotal nSelect |> Seq.toArray

    printfn "Total combinations C(%d,%d) = %d" nTotal nSelect combos.Length
    printfn "Simulations per combination: %d" nSimPerCombo
    printfn "Total simulations: %d" (int64 combos.Length * int64 nSimPerCombo)
    printfn "Parallel degree: %s" (if parallelDegree = -1 then "max" else string parallelDegree)
    printfn "Starting parallel Monte Carlo optimization..."

    let mutable bestResult : PortfolioResult option = None
    let mutable bestSharpe = Double.NegativeInfinity
    let lockObj            = obj()
    let mutable processed  = 0

    let options = ParallelOptions(MaxDegreeOfParallelism = parallelDegree)

    Parallel.For(0, combos.Length, options, fun i ->
        let indices    = combos.[i]
        let subTickers = indices |> Array.map (fun idx -> allTickers.[idx])
        let subMat     = subMatrix returnsMatrix indices
        let cov        = covarianceMatrix subMat
        let result     = simulateBestPortfolio i nSimPerCombo subTickers subMat cov

        match result with
        | Some r ->
            lock lockObj (fun () ->
                if r.SharpeRatio > bestSharpe then
                    bestSharpe <- r.SharpeRatio
                    bestResult <- Some r)
        | None -> ()

        let current = Interlocked.Increment(&processed)
        if current % progressInterval = 0 || current = combos.Length then
            let pct = float current / float combos.Length * 100.0
            let currentBest =
                lock lockObj (fun () ->
                    match bestResult with
                    | Some r -> sprintf "%.4f" r.SharpeRatio
                    | None   -> "N/A")
            printfn "[%s] Progress: %d/%d (%.1f%%) | Best Sharpe so far: %s"
                (DateTime.Now.ToString("HH:mm:ss")) current combos.Length pct currentBest
    ) |> ignore

    bestResult

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
            if r.SharpeRatio > bestSharpe then
                bestSharpe <- r.SharpeRatio
                bestResult <- Some r
        | None -> ()

        processed <- processed + 1
        if processed % progressInterval = 0 then
            printfn "[SEQ] Progress: %d/%d" processed combos.Length

    bestResult

/// Benchmark: paralelo vs sequencial — 5 rodadas, subconjunto pequeno
let benchmark
    (allTickers: string array)
    (returnsMatrix: float[,])
    (nSelect: int)
    (nSimPerCombo: int)
    (nRuns: int)
    (subsetSize: int) : unit =

    let combos = combinations allTickers.Length nSelect |> Seq.truncate subsetSize |> Seq.toArray

    printfn "\n=== BENCHMARK: %d runs, %d combinations, %d sims each ===" nRuns subsetSize nSimPerCombo

    let parallelTimes =
        Array.init nRuns (fun _ ->
            let sw = Diagnostics.Stopwatch.StartNew()
            let options = ParallelOptions(MaxDegreeOfParallelism = -1)
            Parallel.For(0, combos.Length, options, fun i ->
                let indices    = combos.[i]
                let subTickers = indices |> Array.map (fun idx -> allTickers.[idx])
                let subMat     = subMatrix returnsMatrix indices
                let cov        = covarianceMatrix subMat
                simulateBestPortfolio i nSimPerCombo subTickers subMat cov |> ignore
            ) |> ignore
            sw.Elapsed.TotalSeconds)

    let sequentialTimes =
        Array.init nRuns (fun _ ->
            let sw = Diagnostics.Stopwatch.StartNew()
            for i in 0 .. combos.Length - 1 do
                let indices    = combos.[i]
                let subTickers = indices |> Array.map (fun idx -> allTickers.[idx])
                let subMat     = subMatrix returnsMatrix indices
                let cov        = covarianceMatrix subMat
                simulateBestPortfolio i nSimPerCombo subTickers subMat cov |> ignore
            sw.Elapsed.TotalSeconds)

    let avgParallel   = Array.average parallelTimes
    let avgSequential = Array.average sequentialTimes
    let speedup       = avgSequential / avgParallel

    printfn "\nParallel times (s):   %A" parallelTimes
    printfn "Sequential times (s): %A" sequentialTimes
    printfn "\nAvg Parallel:   %.3f s" avgParallel
    printfn "Avg Sequential: %.3f s" avgSequential
    printfn "Speedup:        %.2fx" speedup
    printfn "CPUs available: %d" Environment.ProcessorCount