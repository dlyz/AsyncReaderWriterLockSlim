# AsyncReaderWriterLockSlim

[AsyncReaderWriterLockSlim](./DLyz.Threading/AsyncReaderWriterLockSlim.cs) is a C# implementation of async reader-writer lock
that supports locking across asynchronous calls unlike [ReaderWriterLockSlim](https://docs.microsoft.com/en-us/dotnet/api/system.threading.readerwriterlockslim).

## Features

- quite performant implementation
- 0-alloc during locking (amortized)
- supports fair, writers-elevated and readers-elevated strategies
- supports cancellation
- supports TryAcquire methods
- does not suport locks upgrade
- poorly tested, may contain bugs
- complicated source code
- no embedded diagnostic tools such as deadlock detection

## Benchmarks

Benchmarks of locks may produce quite different result even on same machine depending on many circumstances, but mean exponent is roughly the same.

- `runner` — type of benchmarking lock
- `workers` — number of workers, performing reader and writer locking operations in a cycle
- `ops` — total number of operations per worker
- `writep` — probability (0 to 1) of writer lock operation
- `1 - writep` — probability (0 to 1) of reader lock operation


```text
BenchmarkDotNet=v0.12.1, OS=Windows 10.0.19042
Intel Core i5-6600 CPU 3.30GHz (Skylake), 1 CPU, 4 logical and 4 physical cores
.NET Core SDK=6.0.100-preview.3.21202.5
  [Host]     : .NET Core 5.0.5 (CoreCLR 5.0.521.16609, CoreFX 5.0.521.16609), X64 RyuJIT
  DefaultJob : .NET Core 5.0.5 (CoreCLR 5.0.521.16609, CoreFX 5.0.521.16609), X64 RyuJIT
```

|               runner | workers |     ops | writep |        Mean |    Error |   StdDev |        Gen 0 |      Gen 1 |     Gen 2 |     Allocated | Completed Work Items | Lock Contentions |
|--------------------- |-------- |-------- |------- |------------:|---------:|---------:|-------------:|-----------:|----------:|--------------:|---------------------:|-----------------:|
|  ARWLockSlim-NoCache |       4 | 1000000 |      0 |    219.4 ms |  5.21 ms | 15.28 ms |            - |          - |         - |       1.52 KB |               6.0000 |                - |
| ARWLockSlim-SyncCont |       4 | 1000000 |      0 |    224.2 ms |  4.45 ms | 12.33 ms |            - |          - |         - |       1.52 KB |               6.0000 |                - |
|          ARWLockSlim |       4 | 1000000 |      0 |    233.2 ms |  7.36 ms | 21.59 ms |            - |          - |         - |       1.52 KB |               6.0000 |                - |
|           RWLockSlim |       4 | 1000000 |      0 |    256.3 ms |  3.66 ms |  3.25 ms |            - |          - |         - |          2 KB |               5.0000 |                - |
|      NitoAsyncRWLock |       4 | 1000000 |      0 |  1,569.5 ms | 21.36 ms | 19.98 ms |  409000.0000 |          - |         - | 1250001.52 KB |               6.0000 |        8197.0000 |
|        VSAsyncRWLock |       4 | 1000000 |      0 |  6,525.5 ms | 18.63 ms | 17.43 ms |  267000.0000 |          - |         - |  812502.83 KB |               6.0000 |       39144.0000 |
| ARWLockSlim-SyncCont |       4 | 1000000 |   0.01 |    173.0 ms |  0.84 ms |  0.74 ms |            - |          - |         - |       3.97 KB |               5.3333 |                - |
|           RWLockSlim |       4 | 1000000 |   0.01 |    266.4 ms |  5.22 ms |  6.79 ms |            - |          - |         - |       1.52 KB |               6.0000 |                - |
|  ARWLockSlim-NoCache |       4 | 1000000 |   0.01 |    277.7 ms | 17.09 ms | 50.11 ms |    1000.0000 |          - |         - |     3019.8 KB |           14739.0000 |                - |
|          ARWLockSlim |       4 | 1000000 |   0.01 |    303.2 ms | 19.49 ms | 56.85 ms |            - |          - |         - |     124.69 KB |            7540.0000 |                - |
|      NitoAsyncRWLock |       4 | 1000000 |   0.01 |  1,649.1 ms | 14.06 ms | 12.46 ms |  424000.0000 |          - |         - | 1290815.56 KB |          136205.0000 |        8333.0000 |
|        VSAsyncRWLock |       4 | 1000000 |   0.01 |  6,703.6 ms | 45.01 ms | 42.10 ms |  282000.0000 |          - |         - |  858065.62 KB |          191565.0000 |       44716.0000 |
| ARWLockSlim-SyncCont |       4 | 1000000 |      1 |    261.3 ms |  4.01 ms |  3.35 ms |            - |          - |         - |       1.52 KB |               7.0000 |                - |
|           RWLockSlim |       4 | 1000000 |      1 |    336.6 ms |  6.32 ms | 12.92 ms |            - |          - |         - |       1.52 KB |               6.0000 |                - |
|              Monitor |       4 | 1000000 |      1 |    355.9 ms |  7.06 ms | 13.44 ms |            - |          - |         - |       1.52 KB |               6.0000 |        2621.0000 |
|             SpinLock |       4 | 1000000 |      1 |    648.0 ms | 12.90 ms | 21.90 ms |            - |          - |         - |       1.52 KB |               6.0000 |                - |
|  ARWLockSlim-NoCache |       4 | 1000000 |      1 |  2,864.2 ms | 34.60 ms | 32.37 ms |  153000.0000 |          - |         - |  468570.74 KB |         3998374.0000 |                - |
|          ARWLockSlim |       4 | 1000000 |      1 |  3,035.5 ms | 59.51 ms | 66.14 ms |    1000.0000 |          - |         - |    5484.65 KB |         3966909.0000 |                - |
|        SemaphoreSlim |       4 | 1000000 |      1 |  3,202.9 ms | 30.24 ms | 28.28 ms |  113000.0000 |          - |         - |  343733.29 KB |         3999711.0000 |        1650.0000 |
|      NitoAsyncRWLock |       4 | 1000000 |      1 |  5,942.8 ms | 41.58 ms | 38.89 ms | 1045000.0000 |          - |         - | 3187457.73 KB |         3999880.0000 |         240.0000 |
|        VSAsyncRWLock |       4 | 1000000 |      1 | 10,081.9 ms | 12.84 ms | 12.01 ms |  791000.0000 |  1000.0000 |         - | 2406251.52 KB |         7999983.0000 |          91.0000 |

```text
BenchmarkDotNet=v0.12.1, OS=Windows 10.0.19042
Intel Core i5-6600 CPU 3.30GHz (Skylake), 1 CPU, 4 logical and 4 physical cores
.NET Core SDK=6.0.100-preview.3.21202.5
  [Host]     : .NET Core 6.0.0 (CoreCLR 6.0.21.20104, CoreFX 6.0.21.20104), X64 RyuJIT
  DefaultJob : .NET Core 6.0.0 (CoreCLR 6.0.21.20104, CoreFX 6.0.21.20104), X64 RyuJIT
```

|               runner | workers |     ops | writep |       Mean |    Error |   StdDev |        Gen 0 |      Gen 1 |     Gen 2 |     Allocated |
|--------------------- |-------- |-------- |------- |-----------:|---------:|---------:|-------------:|-----------:|----------:|--------------:|
|  ARWLockSlim-NoCache |       4 | 1000000 |      0 |   194.1 ms |  3.85 ms |  6.74 ms |            - |          - |         - |       1.51 KB |
| ARWLockSlim-SyncCont |       4 | 1000000 |      0 |   200.8 ms |  4.00 ms |  5.34 ms |            - |          - |         - |       1.28 KB |
|          ARWLockSlim |       4 | 1000000 |      0 |   204.5 ms |  4.07 ms | 10.86 ms |            - |          - |         - |       1.51 KB |
|           RWLockSlim |       4 | 1000000 |      0 |   242.3 ms |  3.07 ms |  2.87 ms |            - |          - |         - |       2.21 KB |
|      NitoAsyncRWLock |       4 | 1000000 |      0 | 1,416.7 ms | 25.84 ms | 24.17 ms |  410000.0000 |          - |         - | 1250001.51 KB |
|        VSAsyncRWLock |       4 | 1000000 |      0 | 6,106.9 ms | 51.27 ms | 47.96 ms |  267000.0000 |          - |         - |  812501.51 KB |
| ARWLockSlim-SyncCont |       4 | 1000000 |   0.01 |   167.9 ms |  2.71 ms |  2.54 ms |            - |          - |         - |       2.27 KB |
|           RWLockSlim |       4 | 1000000 |   0.01 |   239.8 ms |  2.34 ms |  2.08 ms |            - |          - |         - |       2.21 KB |
|  ARWLockSlim-NoCache |       4 | 1000000 |   0.01 |   262.3 ms | 15.56 ms | 45.39 ms |            - |          - |         - |     226.27 KB |
|          ARWLockSlim |       4 | 1000000 |   0.01 |   289.5 ms | 17.72 ms | 52.23 ms |            - |          - |         - |     339.29 KB |
|      NitoAsyncRWLock |       4 | 1000000 |   0.01 | 1,514.9 ms | 12.99 ms | 12.15 ms |  425000.0000 |          - |         - | 1292735.02 KB |
|        VSAsyncRWLock |       4 | 1000000 |   0.01 | 6,695.3 ms | 19.43 ms | 18.18 ms |  283000.0000 |          - |         - |  857829.35 KB |
| ARWLockSlim-SyncCont |       4 | 1000000 |      1 |   250.6 ms |  1.77 ms |  1.66 ms |            - |          - |         - |        1.7 KB |
|           RWLockSlim |       4 | 1000000 |      1 |   262.1 ms |  5.21 ms |  7.13 ms |            - |          - |         - |       1.51 KB |
|              Monitor |       4 | 1000000 |      1 |   327.0 ms |  4.23 ms |  3.96 ms |            - |          - |         - |       1.51 KB |
|             SpinLock |       4 | 1000000 |      1 |   588.6 ms | 11.71 ms | 24.45 ms |            - |          - |         - |       1.51 KB |
|          ARWLockSlim |       4 | 1000000 |      1 | 1,578.4 ms | 18.75 ms | 17.53 ms |            - |          - |         - |     193.25 KB |
|  ARWLockSlim-NoCache |       4 | 1000000 |      1 | 1,608.3 ms | 31.43 ms | 33.62 ms |  145000.0000 |          - |         - |  444939.46 KB |
|        SemaphoreSlim |       4 | 1000000 |      1 | 1,679.7 ms | 14.79 ms | 13.84 ms |  113000.0000 |          - |         - |  343750.99 KB |
|      NitoAsyncRWLock |       4 | 1000000 |      1 | 4,754.5 ms | 37.01 ms | 34.62 ms | 1047000.0000 |          - |         - | 3187478.58 KB |
|        VSAsyncRWLock |       4 | 1000000 |      1 | 9,500.9 ms | 33.74 ms | 31.56 ms |  793000.0000 |          - |         - | 2406251.51 KB |

