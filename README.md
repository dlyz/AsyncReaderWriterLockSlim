# AsyncReaderWriterLockSlim

[AsyncReaderWriterLockSlim](./DLyz.Threading/AsyncReaderWriterLockSlim.cs) is a C# implementation of async reader-writer lock
that supports locking across asynchronous calls unlike [ReaderWriterLockSlim](https://docs.microsoft.com/en-us/dotnet/api/system.threading.readerwriterlockslim).

## Features

- quite performant implementation
- 0-alloc during locking (amortized)
- supports fair, writers-elevated and readers-elevated strategies
- supports cancellation
- does not suport locks upgrade
- poor tested, may contain bugs
- complicated source code
- no embedded diagnostic tools sush as deadlock detection

## Benchmark

```text
BenchmarkDotNet=v0.12.1, OS=Windows 10.0.19042
Intel Core i5-6600 CPU 3.30GHz (Skylake), 1 CPU, 4 logical and 4 physical cores
.NET Core SDK=5.0.300-preview.21180.15
  [Host]     : .NET Core 5.0.5 (CoreCLR 5.0.521.16609, CoreFX 5.0.521.16609), X64 RyuJIT
```

|                runner | workers |     ops | writep |        Mean |    Error |    StdDev |      Median |        Gen 0 |      Gen 1 |     Gen 2 |     Allocated | Completed Work Items | Lock Contentions |
|---------------------- |-------- |-------- |------- |------------:|---------:|----------:|------------:|-------------:|-----------:|----------:|--------------:|---------------------:|-----------------:|
|  ARWLockSlim-SyncCont |       4 | 1000000 |      0 |    207.5 ms |  4.53 ms |  13.37 ms |    204.0 ms |            - |          - |         - |       1.52 KB |               5.0000 |                - |
|   ARWLockSlim-NoCache |       4 | 1000000 |      0 |    215.2 ms |  5.78 ms |  17.05 ms |    213.4 ms |            - |          - |         - |       1.52 KB |               6.0000 |                - |
|           ARWLockSlim |       4 | 1000000 |      0 |    240.4 ms |  7.64 ms |  22.52 ms |    236.2 ms |            - |          - |         - |       1.52 KB |               6.0000 |                - |
|            RWLockSlim |       4 | 1000000 |      0 |    255.8 ms |  4.80 ms |   6.24 ms |    256.9 ms |            - |          - |         - |       1.35 KB |               5.0000 |                - |
|         VSAsyncRWLock |       4 | 1000000 |      0 | 16,584.2 ms | 92.17 ms |  86.22 ms | 16,591.8 ms |  791000.0000 |  1000.0000 |         - | 2406251.52 KB |               6.0000 |      523286.0000 |
|  ARWLockSlim-SyncCont |       4 | 1000000 |   0.01 |    176.3 ms |  0.65 ms |   0.61 ms |    176.4 ms |            - |          - |         - |       1.33 KB |               5.6667 |                - |
|           ARWLockSlim |       4 | 1000000 |   0.01 |    262.7 ms | 14.58 ms |  42.54 ms |    260.9 ms |            - |          - |         - |     121.52 KB |            5820.0000 |                - |
|            RWLockSlim |       4 | 1000000 |   0.01 |    270.6 ms |  5.29 ms |   8.98 ms |    269.0 ms |            - |          - |         - |       1.35 KB |               5.0000 |                - |
|   ARWLockSlim-NoCache |       4 | 1000000 |   0.01 |    371.9 ms | 22.28 ms |  65.68 ms |    374.4 ms |    2000.0000 |          - |         - |    7279.92 KB |           35776.0000 |                - |
|         VSAsyncRWLock |       4 | 1000000 |   0.01 | 18,336.3 ms | 83.08 ms |  77.71 ms | 18,372.3 ms |  866000.0000 |  1000.0000 |         - | 2632252.28 KB |          189333.0000 |      568903.0000 |
|  ARWLockSlim-SyncCont |       4 | 1000000 |      1 |    268.0 ms |  2.39 ms |   2.11 ms |    268.3 ms |            - |          - |         - |       1.88 KB |               6.0000 |                - |
|            RWLockSlim |       4 | 1000000 |      1 |    319.4 ms |  6.21 ms |   8.28 ms |    319.5 ms |            - |          - |         - |       1.52 KB |               6.0000 |                - |
|               Monitor |       4 | 1000000 |      1 |    357.2 ms |  7.01 ms |  11.12 ms |    357.2 ms |            - |          - |         - |       1.52 KB |               5.0000 |        4452.0000 |
|              SpinLock |       4 | 1000000 |      1 |    673.4 ms | 11.71 ms |  10.38 ms |    671.5 ms |            - |          - |         - |       1.52 KB |               6.0000 |                - |
|   ARWLockSlim-NoCache |       4 | 1000000 |      1 |  2,927.1 ms | 56.56 ms |  79.29 ms |  2,941.5 ms |  153000.0000 |          - |         - |  468267.19 KB |         3995752.0000 |                - |
|           ARWLockSlim |       4 | 1000000 |      1 |  2,985.3 ms | 26.73 ms |  25.01 ms |  2,978.3 ms |    2000.0000 |          - |         - |    6671.62 KB |         3985175.0000 |                - |
|         SemaphoreSlim |       4 | 1000000 |      1 |  3,182.6 ms | 16.19 ms |  15.14 ms |  3,179.4 ms |  113000.0000 |          - |         - |  343748.95 KB |         3999862.0000 |         228.0000 |
|         VSAsyncRWLock |       4 | 1000000 |      1 | 51,534.8 ms | 43.21 ms |  36.08 ms | 51,526.1 ms | 3267000.0000 |  5000.0000 |         - | 9937473.79 KB |         8000005.0000 |        6294.0000 |

- `runner` — type of benchmarking lock
- `workers` — number of workers, performing reader and writer locking operations in a cycle
- `ops` — total number of operations per worker
- `writep` — probability (0 to 1) of writer lock operation
- `1 - writep` — probability (0 to 1) of reader lock operation
