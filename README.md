# Portfolio Optimizer — Dow Jones 30

> **Programação Funcional — Projeto 2 | Insper 2026-1**

Otimização de carteiras de ações do índice Dow Jones via simulação de Monte Carlo paralela, implementada em **F#** (linguagem funcional).

---

## Contexto

Um portfolio manager deseja descobrir a melhor alocação entre 20 das 30 ações do Dow Jones, maximizando o **Sharpe Ratio**:

$$SR = \frac{\mu - r_{free}}{\sigma}$$

Como todas as carteiras usam a mesma taxa livre de risco, ela é desconsiderada na comparação, e o objetivo passa a ser:

$$\max_w \frac{\mu}{\sigma} \quad \text{s.t.} \quad \sum w_i = 1,\; 0 \le w_i \le 0{,}2$$

### Restrições

| Restrição | Valor |
|-----------|-------|
| Ações selecionadas | 20 de 30 |
| Long-only | $w_i \geq 0$ |
| Concentração máxima | $w_i \leq 0{,}20$ |
| Combinações possíveis | $\binom{30}{20} \approx 30$ milhões |
| Simulações por combinação | 1.000.000 |

---

## Arquitetura

```
PortfolioOptimizer/
├── DataFetcher.fs     # Busca dados via Yahoo Finance API + cache CSV
├── Portfolio.fs       # Funções puras: retorno, volatilidade, Sharpe
├── Simulation.fs      # Monte Carlo paralelo + benchmark
├── Program.fs         # Entry point com modos: optimize / benchmark / test
└── PortfolioOptimizer.fsproj
```

### Por que F#?

- **Funções puras** eliminam efeitos colaterais e tornam o raciocínio sobre concorrência trivial
- **Ausência de estado compartilhado** permite paralelismo seguro sem locks desnecessários
- Abstrações de **map/filter/reduce** se encaixam perfeitamente na estrutura do problema
- Paradigma ideal para pipelines massivamente paralelas e determinísticas

### Pipeline funcional

```fsharp
carteirasPossiveis
|> Array.Parallel.map avaliarCombinação   // paralelismo entre combinações
|> Array.maxBy (fun r -> r.SharpeRatio)  // melhor carteira global
```

### Funções puras (sem efeitos colaterais)

| Função | Descrição |
|--------|-----------|
| `portfolioReturns` | $r_p = r \cdot w$ — retorno diário da carteira |
| `annualizedReturn` | $\mu = \bar{r}_p \times 252$ |
| `covarianceMatrix` | Matriz de covariância $C$ dos retornos |
| `annualizedVolatility` | $\sigma = \sqrt{w^T C w \times 252}$ |
| `sharpeRatio` | $SR = \mu / \sigma$ |
| `generateValidWeights` | Gera pesos aleatórios válidos (soma=1, max=0.2) |
| `simulateBestPortfolio` | Melhor carteira de N simulações para uma combinação |

### Paralelismo

O paralelismo é aplicado **entre combinações** usando `Parallel.For` do .NET com funções **puras** — cada thread recebe seu próprio seed, sua própria sub-matriz e não compartilha estado mutável. O único estado compartilhado é protegido por um `lock` mínimo para atualizar o melhor resultado global.

```
Combinação 1 ─── Thread A ─── simulateBestPortfolio(seed=0, ...) ─┐
Combinação 2 ─── Thread B ─── simulateBestPortfolio(seed=1, ...) ─┤─► max Sharpe
Combinação 3 ─── Thread C ─── simulateBestPortfolio(seed=2, ...) ─┤
     ...                                                            ┘
```

---

## Instalação

### Pré-requisitos

- [.NET SDK 8.0+](https://dotnet.microsoft.com/download) — verifique com `dotnet --version`
- Acesso à internet (para buscar dados do Yahoo Finance na primeira execução)

### Clone e build

```bash
git clone <url-do-repositorio>
cd portfolio-optimizer/PortfolioOptimizer
dotnet restore
dotnet build -c Release
```

---

## Como Executar

Todos os comandos devem ser rodados dentro de `PortfolioOptimizer/`.

### 1. Otimização completa (modo principal)

```bash
dotnet run -c Release -- optimize
```

Ou após build:

```bash
dotnet bin/Release/net8.0/PortfolioOptimizer.dll optimize
```

- Busca dados do Yahoo Finance (2º sem. 2025) e salva em `returns_cache.csv`
- Roda $\binom{30}{20} \approx 30M$ combinações × 1M simulações em paralelo
- Imprime a melhor carteira ao final e salva em `best_portfolio.txt`

> ⚠️ **Aviso de tempo**: a execução completa pode levar horas dependendo do hardware. Para testar rapidamente, reduza `nSimPerCombo` no `Program.fs`.

### 2. Benchmark: paralelo vs sequencial

```bash
dotnet run -c Release -- benchmark
```

Executa 5 rodadas com as primeiras 100 combinações e 10.000 simulações cada, comparando tempo paralelo vs sequencial. Exibe speedup e número de CPUs utilizadas.

### 3. Teste out-of-sample (Q1 2025)

```bash
dotnet run -c Release -- test
```

Requer `best_portfolio.txt` gerado pelo modo `optimize`. Avalia a melhor carteira encontrada no período de treino (2º sem. 2025) sobre dados do 1º trimestre de 2025.

---

## Dados

Os dados são obtidos **automaticamente via Yahoo Finance API** (`query1.finance.yahoo.com`) na primeira execução e salvos em CSV para reutilização:

| Arquivo | Conteúdo |
|---------|----------|
| `returns_cache.csv` | Retornos diários — treino (jul–dez 2025) |
| `returns_test_cache.csv` | Retornos diários — teste (jan–mar 2025) |
| `best_portfolio.txt` | Melhor carteira encontrada |

### Ações do Dow Jones utilizadas

```
AAPL  AMGN  AXP   BA    CAT   CRM   CSCO  CVX   DIS   DOW
GS    HD    HON   IBM   INTC  JNJ   JPM   KO    MCD   MMM
MRK   MSFT  NKE   PG    SHW   TRV   UNH   V     VZ    WMT
```

---

## Resultados Esperados

### Exemplo de saída — otimização

```
════════════════════════════════════════
 BEST PORTFOLIO FOUND
════════════════════════════════════════
 Sharpe Ratio:          1.8432
 Annualized Return:     24.71%
 Annualized Volatility: 13.41%
 Selected Tickers (20):
   UNH     20.00%
   GS      18.73%
   MSFT    15.22%
   ...
════════════════════════════════════════
```

### Exemplo de saída — benchmark

```
=== BENCHMARK: 5 runs, first 100 combinations, 10000 sims each ===

Avg Parallel:   0.412 s
Avg Sequential: 3.187 s
Speedup:        7.74x
CPUs available: 8
```

### Teste out-of-sample

O teste avalia se a carteira otimizada em dados passados generaliza para o período seguinte, medindo retorno, volatilidade e Sharpe no 1º trimestre de 2025.

---

## Dependências

Apenas bibliotecas **nativas do .NET 8** — sem pacotes externos:

| Namespace | Uso |
|-----------|-----|
| `System.Net.Http` | Requisições HTTP para Yahoo Finance |
| `System.Text.Json` | Parse da resposta JSON |
| `System.Threading.Tasks` | `Parallel.For` para paralelismo |
| `System.Threading` | `Interlocked` para contadores thread-safe |

---

## Estrutura de Módulos

```
DataFetcher.fs
  fetchPrices          : ticker → período → Async<preços>
  computeReturns       : preços → retornos diários          [pura]
  fetchAllReturns      : tickers[] → período → matriz       [I/O]
  saveToCSV / loadFromCSV                                   [I/O]

Portfolio.fs
  portfolioReturns     : pesos → matriz → retornos[]        [pura]
  annualizedReturn     : retornos[] → float                 [pura]
  covarianceMatrix     : matriz → float[,]                  [pura]
  annualizedVolatility : pesos → cov → float                [pura]
  sharpeRatio          : pesos → matriz → cov → float       [pura]
  generateValidWeights : rng → n → float[] option           [pura*]
  evaluatePortfolio    : ... → PortfolioResult              [pura]

Simulation.fs
  combinations         : n → k → seq<int[]>                 [pura]
  simulateBestPortfolio: seed → nSim → ... → result option  [pura]
  runOptimization      : ... → PortfolioResult option       [paralelo]
  benchmark            : ... → unit                         [I/O]
```

*`generateValidWeights` recebe o `Random` como argumento — sem estado global.

---

