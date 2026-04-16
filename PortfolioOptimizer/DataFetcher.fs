module DataFetcher

open System
open System.Net.Http
open System.Text.Json

/// Dow Jones 30 tickers
let dowJonesTickers = [|
    "AAPL"; "AMGN"; "AXP"; "BA"; "CAT"; "CRM"; "CSCO"; "CVX"; "DIS"; "DOW"
    "GS"; "HD"; "HON"; "IBM"; "INTC"; "JNJ"; "JPM"; "KO"; "MCD"; "MMM"
    "MRK"; "MSFT"; "NKE"; "PG"; "SHW"; "TRV"; "UNH"; "V"; "VZ"; "WMT"
|]

type DailyReturn = {
    Date: DateTime
    Ticker: string
    Return: float
}

/// Fetch historical prices from Yahoo Finance (pure data fetch, side-effectful by necessity)
let fetchPrices (ticker: string) (startDate: DateTime) (endDate: DateTime) : Async<(DateTime * float) array> =
    async {
        let startUnix = DateTimeOffset(startDate).ToUnixTimeSeconds()
        let endUnix   = DateTimeOffset(endDate).ToUnixTimeSeconds()
        let url = $"https://query1.finance.yahoo.com/v8/finance/chart/{ticker}?interval=1d&period1={startUnix}&period2={endUnix}"

        use client = new HttpClient()
        client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0")
        client.Timeout <- TimeSpan.FromSeconds(30.0)

        try
            let! response = client.GetStringAsync(url) |> Async.AwaitTask
            use doc = JsonDocument.Parse(response)
            let root = doc.RootElement
            let result = root.GetProperty("chart").GetProperty("result").[0]
            let timestamps = result.GetProperty("timestamp").EnumerateArray() |> Seq.map (fun t -> t.GetInt64()) |> Seq.toArray
            let closes = result.GetProperty("indicators").GetProperty("adjclose").[0].GetProperty("adjclose").EnumerateArray()
                         |> Seq.map (fun p -> if p.ValueKind = JsonValueKind.Null then Double.NaN else p.GetDouble())
                         |> Seq.toArray

            let pairs =
                Array.zip timestamps closes
                |> Array.filter (fun (_, p) -> not (Double.IsNaN p))
                |> Array.map (fun (ts, p) -> DateTimeOffset.FromUnixTimeSeconds(ts).UtcDateTime.Date, p)

            return pairs
        with ex ->
            eprintfn "Warning: failed to fetch %s: %s" ticker ex.Message
            return [||]
    }

/// Compute daily log-returns from price series (pure function)
let computeReturns (prices: (DateTime * float) array) : (DateTime * float) array =
    if prices.Length < 2 then [||]
    else
        prices
        |> Array.pairwise
        |> Array.map (fun ((_, p0), (d1, p1)) -> d1, Math.Log(p1 / p0))

/// Fetch all tickers and build returns matrix aligned by date
let fetchAllReturns (tickers: string array) (startDate: DateTime) (endDate: DateTime) : Async<string array * float[,]> =
    async {
        printfn "Fetching data for %d tickers from Yahoo Finance..." tickers.Length

        let! allReturns =
            tickers
            |> Array.map (fun ticker ->
                async {
                    let! prices = fetchPrices ticker startDate endDate
                    let returns = computeReturns prices
                    return ticker, returns
                })
            |> Async.Parallel

        // Find common dates across all tickers
        let dateSetPerTicker =
            allReturns
            |> Array.map (fun (_, rets) -> rets |> Array.map fst |> Set.ofArray)

        let commonDates =
            dateSetPerTicker
            |> Array.reduce Set.intersect
            |> Set.toArray
            |> Array.sort

        printfn "Found %d common trading days." commonDates.Length

        // Build lookup maps
        let returnMaps =
            allReturns
            |> Array.map (fun (ticker, rets) ->
                ticker, rets |> Array.map (fun (d,r) -> d, r) |> Map.ofArray)

        let nDays    = commonDates.Length
        let nTickers = tickers.Length
        let matrix   = Array2D.create nDays nTickers 0.0

        for t in 0 .. nTickers - 1 do
            let ticker, rmap = returnMaps.[t]
            for d in 0 .. nDays - 1 do
                match Map.tryFind commonDates.[d] rmap with
                | Some r -> matrix.[d, t] <- r
                | None   -> matrix.[d, t] <- 0.0

        // Filter out tickers with all-zero columns (fetch failed)
        let validIdx =
            [| 0 .. nTickers - 1 |]
            |> Array.filter (fun t ->
                let col = Array.init nDays (fun d -> matrix.[d, t])
                col |> Array.exists (fun v -> v <> 0.0))

        let validTickers = validIdx |> Array.map (fun i -> fst returnMaps.[i])
        let filteredMatrix = Array2D.init nDays validIdx.Length (fun d i -> matrix.[d, validIdx.[i]])

        printfn "Valid tickers: %d" validTickers.Length
        return validTickers, filteredMatrix
    }

/// Save returns matrix to CSV for caching
let saveToCSV (path: string) (tickers: string array) (matrix: float[,]) =
    use sw = new System.IO.StreamWriter(path)
    sw.WriteLine(tickers |> String.concat ",")
    let nDays = Array2D.length1 matrix
    let nT    = Array2D.length2 matrix
    for d in 0 .. nDays - 1 do
        let row = Array.init nT (fun t -> string matrix.[d, t])
        sw.WriteLine(row |> String.concat ",")

/// Load returns matrix from CSV cache
let loadFromCSV (path: string) : (string array * float[,]) option =
    try
        let lines = System.IO.File.ReadAllLines(path)
        if lines.Length < 2 then None
        else
            let tickers = lines.[0].Split(',')
            let nDays   = lines.Length - 1
            let nT      = tickers.Length
            let matrix  = Array2D.create nDays nT 0.0
            for d in 0 .. nDays - 1 do
                let parts = lines.[d + 1].Split(',')
                for t in 0 .. nT - 1 do
                    matrix.[d, t] <- float parts.[t]
            Some (tickers, matrix)
    with _ -> None
