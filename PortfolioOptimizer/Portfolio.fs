module Portfolio

open System

/// Pure: compute portfolio daily returns given weight vector and return matrix (subset of columns)
let portfolioReturns (weights: float array) (returnsMatrix: float[,]) : float array =
    let nDays = Array2D.length1 returnsMatrix
    Array.init nDays (fun d ->
        weights
        |> Array.mapi (fun t w -> w * returnsMatrix.[d, t])
        |> Array.sum)

/// Pure: annualized mean return (252 trading days)
let annualizedReturn (dailyReturns: float array) : float =
    let mean = dailyReturns |> Array.average
    mean * 252.0

/// Pure: compute covariance matrix from returns matrix
let covarianceMatrix (returnsMatrix: float[,]) : float[,] =
    let nDays = Array2D.length1 returnsMatrix
    let nT    = Array2D.length2 returnsMatrix
    let means = Array.init nT (fun t ->
        Array.init nDays (fun d -> returnsMatrix.[d, t]) |> Array.average)

    Array2D.init nT nT (fun i j ->
        let sum =
            Array.init nDays (fun d ->
                (returnsMatrix.[d, i] - means.[i]) * (returnsMatrix.[d, j] - means.[j]))
            |> Array.sum
        sum / float (nDays - 1))

/// Pure: portfolio variance using w^T * C * w
let portfolioVariance (weights: float array) (cov: float[,]) : float =
    let n = weights.Length
    let wC = Array.init n (fun i ->
        Array.init n (fun j -> weights.[j] * cov.[i, j]) |> Array.sum)
    Array.init n (fun i -> wC.[i] * weights.[i]) |> Array.sum

/// Pure: annualized volatility
let annualizedVolatility (weights: float array) (cov: float[,]) : float =
    let variance = portfolioVariance weights cov
    Math.Sqrt(variance * 252.0)

/// Pure: Sharpe ratio (ignoring risk-free rate for comparison purposes)
let sharpeRatio (weights: float array) (returnsMatrix: float[,]) (cov: float[,]) : float =
    let pReturns = portfolioReturns weights returnsMatrix
    let mu       = annualizedReturn pReturns
    let sigma    = annualizedVolatility weights cov
    if sigma = 0.0 then Double.NegativeInfinity
    else mu / sigma

/// Pure: generate a random valid weight vector for n assets
/// Weights sum to 1, each in [0, 0.2] (long-only, max 20% concentration)
let generateValidWeights (rng: Random) (n: int) : float array option =
    // Strategy: draw from Dirichlet-like distribution then clip and renormalize
    // Generate raw uniform [0, 0.2], normalize to sum = 1
    // Rejection sample until constraints satisfied
    let maxIter = 50
    let rec tryGenerate iter =
        if iter >= maxIter then None
        else
            // Draw n uniform values
            let raw = Array.init n (fun _ -> rng.NextDouble())
            let total = Array.sum raw
            let normalized = Array.map (fun x -> x / total) raw
            // Check max concentration constraint
            if Array.forall (fun w -> w <= 0.2) normalized then
                Some normalized
            else
                // Clip at 0.2 and renormalize iteratively
                let clipped = Array.map (fun w -> min w 0.2) normalized
                let clippedSum = Array.sum clipped
                if clippedSum <= 0.0 then tryGenerate (iter + 1)
                else
                    let rescaled = Array.map (fun w -> w / clippedSum) clipped
                    if Array.forall (fun w -> w <= 0.2 + 1e-9) rescaled then
                        Some rescaled
                    else
                        tryGenerate (iter + 1)
    tryGenerate 0

type PortfolioResult = {
    Tickers: string array
    Weights: float array
    AnnualizedReturn: float
    AnnualizedVolatility: float
    SharpeRatio: float
}

/// Pure: evaluate a portfolio given weights, returns matrix, covariance matrix
let evaluatePortfolio
    (tickers: string array)
    (weights: float array)
    (subMatrix: float[,])
    (cov: float[,]) : PortfolioResult =
    let pRets   = portfolioReturns weights subMatrix
    let mu      = annualizedReturn pRets
    let sigma   = annualizedVolatility weights cov
    let sharpe  = if sigma = 0.0 then Double.NegativeInfinity else mu / sigma
    { Tickers = tickers; Weights = weights
      AnnualizedReturn = mu; AnnualizedVolatility = sigma; SharpeRatio = sharpe }

/// Pure: extract sub-matrix for given column indices
let subMatrix (matrix: float[,]) (indices: int array) : float[,] =
    let nDays = Array2D.length1 matrix
    let n     = indices.Length
    Array2D.init nDays n (fun d i -> matrix.[d, indices.[i]])
