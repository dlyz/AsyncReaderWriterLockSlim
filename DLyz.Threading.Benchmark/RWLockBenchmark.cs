using BenchmarkDotNet.Attributes;
using Microsoft.VisualStudio.Threading;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DLyz.Threading.Benchmark
{
	[MemoryDiagnoser]
	[ThreadingDiagnoser]
	public class RWLockBenchmark
	{
		public static void RunManual(double writerProb)
		{
			var l = new AsyncReaderWriterLockSlim(new() { RunContinuationsAsynchronously = true });
			var set = CreateWorkers(new AdapterAsyncRWLockSlim(l), 4);

			var sw = new Stopwatch();
			sw.Start();

			set.RunAsync(writerProb, 100000).GetAwaiter().GetResult();

			Console.WriteLine("Done in {0} ms", sw.ElapsedMilliseconds);
		}

		private static IEnumerable<Runner> CreateRunners(int workers)
		{
			yield return CreateWorkers(new AdapterAsyncRWLockSlim(), workers, "ARWLockSlim-NoCache", () =>
			{
				if (AsyncReaderWriterLockSlim.IsCacheEnabled)
				{
					AsyncReaderWriterLockSlim.IsCacheEnabled = false;
					return () => AsyncReaderWriterLockSlim.IsCacheEnabled = true;
				}
				return null;
			});
			yield return CreateWorkers(new AdapterAsyncRWLockSlim(), workers, "ARWLockSlim", () => {
				if (!AsyncReaderWriterLockSlim.IsCacheEnabled)
				{
					AsyncReaderWriterLockSlim.IsCacheEnabled = true;
					return () => AsyncReaderWriterLockSlim.IsCacheEnabled = false;
				}
				return null;
			});
			yield return CreateWorkers(new AdapterAsyncRWLockSlim(new AsyncReaderWriterLockSlim(new() { RunContinuationsAsynchronously = false })), workers, "ARWLockSlim-SyncCont");

			yield return CreateWorkers<AdapterOldAsyncRWLock, AdapterOldAsyncRWLock.State>(new AdapterOldAsyncRWLock(), workers, "OldAsyncRWLock");
			yield return CreateWorkers(new AdapterAsyncRWLockSlim1(), workers, "ARWLockSlim1");
			yield return CreateWorkers(new AdapterAsyncRWLockSlim1(new AsyncReaderWriterLockSlim1(runContinuationsAsynchronously: false)), workers, "ARWLockSlim1-SyncCont");

			yield return CreateWorkers<AdapterVSAsyncRWLock, AdapterVSAsyncRWLock.State>(new AdapterVSAsyncRWLock(), workers, "VSAsyncRWLock");
			yield return CreateWorkers(new AdapterMonitor(), workers, "Monitor");
			yield return CreateWorkers(new AdapterRWLockSlim(), workers, "RWLockSlim");
			yield return CreateWorkers(new AdapterSemaphoreSlim(), workers, "SemaphoreSlim");
			yield return CreateWorkers(new AdapterSpinLock(), workers, "SpinLock");

			// todo: may hang your machine
			//yield return CreateWorkers(new AdapterRWLock(), workers, "RWLock");

		}


		private static IEnumerable<double> CreateWriterProbabilities()
		{
			yield return 0;
			yield return 0.01;
			//yield return 0.1;
			//yield return 0.5;
			yield return 1;
		}

		public static IEnumerable<object[]> GetArgs()
		{
			const int operations = 1000000;
			var runners = Enumerable.Empty<Runner>();
			runners = runners.Concat(CreateRunners(4));
			//runners = runners.Concat(CreateRunners(16));

			foreach (var wp in CreateWriterProbabilities())
			{
				foreach (var runner in runners)
				{
					if (wp == 1 || runner.ReaderSupported)
					{
						yield return new object[] { runner, runner.Workers, operations, wp };
					}
				}
			}
		}


		[Benchmark]
		[ArgumentsSource(nameof(GetArgs))]
		public Task Benchmark(Runner runner, int workers, int ops, double writep)
		{
			return runner.RunAsync(writep, ops);
		}


		public abstract class Runner
		{
			public abstract Task RunAsync(double writerProbability, int operations);

			public abstract int Workers { get; }


			public abstract bool ReaderSupported { get; }
		}

		private static Runner CreateWorkers<TLock>(TLock l, int count, string name = "", Func<Action?>? initAction = null)
			where TLock : ILock
		{
			return new WorkerSet<StatefulLockAdapter<TLock>, object?>(new StatefulLockAdapter<TLock>(l), count, name, initAction);
		}

		private static Runner CreateWorkers<TLock, TState>(TLock l, int count, string name = "", Func<Action?>? initAction = null)
			where TState : class?, new()
			where TLock : IStatefulLock<TState>
		{
			return new WorkerSet<TLock, TState>(l, count, name, initAction);
		}

		private class WorkerSet<TLock, TState> : Runner
			where TState : class?, new()
			where TLock : IStatefulLock<TState>
		{
			public override string ToString()
			{
				return _name;
			}

			public WorkerSet(TLock l, int count, string name = "", Func<Action?>? initAction = null)
			{
				_l = l;
				_name = name;
				_initAction = initAction;
				_workers = new Worker[count];
				for (int i = 0; i < count; i++)
				{
					_workers[i] = new Worker(this, i);
				}

				//_logger = new Logger(count);
			}

			public override int Workers => _workers.Length;

			public override bool ReaderSupported => _l.ReaderSupported;

			//private static Logger _logger;
			private readonly TLock _l;
			private readonly string _name;
			private readonly Func<Action?>? _initAction;
			private readonly Worker[] _workers;
			private int _currentValue;

			public override async Task RunAsync(double writerProbability, int operations)
			{
				var deinit = _initAction?.Invoke();

				var tasks = new Task[_workers.Length];

				for (int i = 0; i < _workers.Length; i++)
				{
					tasks[i] = _workers[i].RunAsync(writerProbability, operations);
				}

				await Task.WhenAll(tasks).ConfigureAwait(false);

				deinit?.Invoke();
			}


			private class Worker
			{
				private readonly WorkerSet<TLock, TState> _set;
				private readonly int _id;
				private readonly Random _rnd;

				public Worker(WorkerSet<TLock, TState> set, int id)
				{
					_set = set;
					_id = id;
					_rnd = new Random(id);
				}

				public async Task RunAsync(double writerProbability, int operations)
				{
					try
					{
						var state = new TState();

						var l = _set._l;
						var rnd = _rnd;

						await Task.Yield();
						for (int i = 0; i < operations; i++)
						{
							if (rnd.NextDouble() < writerProbability)
							{
								Log(i, true, "before acquire");
								await l.AcquireWriterLockAsync(state).ConfigureAwait(false);
								Log(i, true, "after acquire");
								try
								{
									DoWriter();
								}
								finally
								{
									l.ReleaseWriterLock(state);
									Log(i, true, "after release");
								}
							}
							else
							{
								Log(i, false, "before acquire");
								await l.AcquireReaderLockAsync(state).ConfigureAwait(false);
								Log(i, false, "after acquire");
								try
								{
									DoReader();
								}
								finally
								{
									l.ReleaseReaderLock(state);
									Log(i, false, "after release");
								}
							}
						}
					}
					catch (Exception ex)
					{
						Console.WriteLine("Worker aborted: {0}", ex);
						throw;
					}

					//Console.WriteLine("Worker {0} done", _id);
				}

				private void Log(int op, bool writer, string text)
				{
					//_logger.Log(new LogMessage
					//{
					//	WorkerId = _id,
					//	OpId = op,
					//	IsWriter = writer,
					//	OpName = text,
					//});

					//Console.WriteLine($"thread_{_id} op_{op} {(writer ? "w" : "r")} {text}");
				}


				private int? _lastRead;

				private void DoWriter()
				{
					foreach (var item in _set._workers)
					{
						item.EnsureLastReaded(_set._currentValue);
						item._lastRead = null;
					}

					++_set._currentValue;
				}

				private void DoReader()
				{
					_lastRead = _set._currentValue;
				}

				public void EnsureLastReaded(int value)
				{
					if (_lastRead.HasValue && _lastRead.Value != value)
					{
						throw new Exception("Invariant broken.");
					}
				}
			}
		}


		#region adapters

		internal interface ILockCapabilities
		{
			bool ReaderSupported { get; }

			bool TrySupported { get; }
		}

		internal interface ILock : ILockCapabilities
		{
			ValueTask AcquireReaderLockAsync(CancellationToken cancellationToken = default);

			ValueTask AcquireWriterLockAsync(CancellationToken cancellationToken = default);

			void ReleaseReaderLock();

			void ReleaseWriterLock();

			bool TryAcquireReaderLock();

			bool TryAcquireWriterLock();
		}

		internal interface IStatefulLock<TState> : ILockCapabilities
			where TState : class?
		{
			ValueTask AcquireReaderLockAsync(TState state, CancellationToken cancellationToken = default);

			ValueTask AcquireWriterLockAsync(TState state, CancellationToken cancellationToken = default);

			void ReleaseReaderLock(TState state);

			void ReleaseWriterLock(TState state);

			bool TryAcquireReaderLock(TState state);

			bool TryAcquireWriterLock(TState state);
		}


		internal sealed class StatefulLockAdapter<TLock> : IStatefulLock<object?>, ILockCapabilities
			where TLock : ILock, ILockCapabilities
		{
			public StatefulLockAdapter(TLock @lock)
			{
				Lock = @lock;
			}

			public TLock Lock { get; }

			public bool ReaderSupported => Lock.ReaderSupported;

			public bool TrySupported => Lock.TrySupported;

			public ValueTask AcquireReaderLockAsync(object? state, CancellationToken cancellationToken = default)
			{
				return Lock.AcquireReaderLockAsync(cancellationToken);
			}

			public ValueTask AcquireWriterLockAsync(object? state, CancellationToken cancellationToken = default)
			{
				return Lock.AcquireWriterLockAsync(cancellationToken);
			}

			public void ReleaseReaderLock(object? state)
			{
				Lock.ReleaseReaderLock();
			}

			public void ReleaseWriterLock(object? state)
			{
				Lock.ReleaseWriterLock();
			}

			public bool TryAcquireReaderLock(object? state)
			{
				return Lock.TryAcquireReaderLock();
			}

			public bool TryAcquireWriterLock(object? state)
			{
				return Lock.TryAcquireWriterLock();
			}
		}


		internal class AdapterVSAsyncRWLock : IStatefulLock<AdapterVSAsyncRWLock.State>
		{
			public class State
			{ 
				public AsyncReaderWriterLock.Releaser Releaser { get; set; }
			}

			private readonly AsyncReaderWriterLock _l;


			public bool ReaderSupported => true;

			public bool TrySupported => false;

			public AdapterVSAsyncRWLock(AsyncReaderWriterLock? l = null)
			{
#pragma warning disable VSTHRD012 // Provide JoinableTaskFactory where allowed
				_l = l ?? new AsyncReaderWriterLock(captureDiagnostics: false);
#pragma warning restore VSTHRD012 // Provide JoinableTaskFactory where allowed
			}

			public async ValueTask AcquireReaderLockAsync(State state, CancellationToken cancellationToken = default)
			{
				state.Releaser = await _l.ReadLockAsync(cancellationToken);
			}

			public async ValueTask AcquireWriterLockAsync(State state, CancellationToken cancellationToken = default)
			{
				state.Releaser = await _l.WriteLockAsync(cancellationToken);
			}

			public void ReleaseReaderLock(State state)
			{
				state.Releaser.Dispose();
			}

			public void ReleaseWriterLock(State state)
			{
				state.Releaser.Dispose();
			}

			public bool TryAcquireReaderLock(State state)
			{
				throw new NotSupportedException();
			}

			public bool TryAcquireWriterLock(State state)
			{
				throw new NotSupportedException();
			}
		}


		internal class AdapterAsyncRWLockSlim : ILock
		{
			private readonly AsyncReaderWriterLockSlim _l;


			public bool ReaderSupported => true;

			public bool TrySupported => true;

			public AdapterAsyncRWLockSlim(AsyncReaderWriterLockSlim? l = null)
			{
				_l = l ?? new AsyncReaderWriterLockSlim();
			}

			public ValueTask AcquireReaderLockAsync(CancellationToken cancellationToken = default)
			{
				return (_l).AcquireReaderLockAsync(cancellationToken);
			}

			public ValueTask AcquireWriterLockAsync(CancellationToken cancellationToken = default)
			{
				return (_l).AcquireWriterLockAsync(cancellationToken);
			}

			public void ReleaseReaderLock()
			{
				(_l).ReleaseReaderLock();
			}

			public void ReleaseWriterLock()
			{
				(_l).ReleaseWriterLock();
			}

			public bool TryAcquireReaderLock()
			{
				return (_l).TryAcquireReaderLock();
			}

			public bool TryAcquireWriterLock()
			{
				return (_l).TryAcquireWriterLock();
			}
		}

		internal class AdapterAsyncRWLockSlim1 : ILock
		{
			private readonly AsyncReaderWriterLockSlim1 _l;


			public bool ReaderSupported => true;

			public bool TrySupported => true;

			public AdapterAsyncRWLockSlim1(AsyncReaderWriterLockSlim1? l = null)
			{
				_l = l ?? new AsyncReaderWriterLockSlim1();
			}

			public ValueTask AcquireReaderLockAsync(CancellationToken cancellationToken = default)
			{
				return (_l).AcquireReaderLockAsync(cancellationToken);
			}

			public ValueTask AcquireWriterLockAsync(CancellationToken cancellationToken = default)
			{
				return (_l).AcquireWriterLockAsync(cancellationToken);
			}

			public void ReleaseReaderLock()
			{
				(_l).ReleaseReaderLock();
			}

			public void ReleaseWriterLock()
			{
				(_l).ReleaseWriterLock();
			}

			public bool TryAcquireReaderLock()
			{
				return (_l).TryAcquireReaderLock();
			}

			public bool TryAcquireWriterLock()
			{
				return (_l).TryAcquireWriterLock();
			}
		}


		internal class AdapterOldAsyncRWLock : IStatefulLock<AdapterOldAsyncRWLock.State>
		{
			public class State
			{
				public IDisposable? Disposable { get; set; }
			}

			private readonly OldAsyncReaderWriterLock _l;			


			public bool ReaderSupported => true;

			public bool TrySupported => false;

			public AdapterOldAsyncRWLock(OldAsyncReaderWriterLock? l = null)
			{
				_l = l ?? new OldAsyncReaderWriterLock();
			}

			public async ValueTask AcquireReaderLockAsync(State state, CancellationToken cancellationToken = default)
			{
				state.Disposable = await _l.ReaderLockAsync(cancellationToken).ConfigureAwait(false);
			}

			public async ValueTask AcquireWriterLockAsync(State state, CancellationToken cancellationToken = default)
			{
				state.Disposable = await _l.WriterLockAsync(cancellationToken).ConfigureAwait(false);
			}

			public void ReleaseReaderLock(State state)
			{
				state.Disposable!.Dispose();
			}

			public void ReleaseWriterLock(State state)
			{
				state.Disposable!.Dispose();
			}

			public bool TryAcquireReaderLock(State state)
			{
				throw new NotSupportedException();
			}

			public bool TryAcquireWriterLock(State state)
			{
				throw new NotSupportedException();
			}
		}


		internal class AdapterMonitor : ILock
		{
			public bool ReaderSupported => false;

			public bool TrySupported => true;

			public ValueTask AcquireReaderLockAsync(CancellationToken cancellationToken = default)
			{
				Monitor.Enter(this);
				return default;
			}

			public ValueTask AcquireWriterLockAsync(CancellationToken cancellationToken = default)
			{
				Monitor.Enter(this);
				return default;
			}

			public void ReleaseReaderLock()
			{
				Monitor.Exit(this);
			}

			public void ReleaseWriterLock()
			{
				Monitor.Exit(this);
			}

			public bool TryAcquireReaderLock()
			{
				return Monitor.TryEnter(this);
			}

			public bool TryAcquireWriterLock()
			{
				return Monitor.TryEnter(this);
			}
		}

		internal class AdapterSpinLock : ILock
		{
			private SpinLock _lock = new SpinLock();

			public bool ReaderSupported => false;

			public bool TrySupported => true;

			public ValueTask AcquireReaderLockAsync(CancellationToken cancellationToken = default)
			{
				var ok = false;
				while (!ok)
				{
					_lock.Enter(ref ok);
				}
				return default;
			}

			public ValueTask AcquireWriterLockAsync(CancellationToken cancellationToken = default)
			{
				var ok = false;
				while (!ok)
				{
					_lock.Enter(ref ok);
				}
				return default;
			}

			public void ReleaseReaderLock()
			{
				_lock.Exit();
			}

			public void ReleaseWriterLock()
			{
				_lock.Exit();
			}

			public bool TryAcquireReaderLock()
			{
				var ok = false;
				_lock.TryEnter(ref ok);
				return ok;
			}

			public bool TryAcquireWriterLock()
			{
				var ok = false;
				_lock.TryEnter(ref ok);
				return ok;
			}
		}

		internal class AdapterRWLockSlim : ILock
		{
			private readonly ReaderWriterLockSlim _rwlock = new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion);


			public bool ReaderSupported => true;

			public bool TrySupported => true;

			public ValueTask AcquireReaderLockAsync(CancellationToken cancellationToken = default)
			{
				_rwlock.EnterReadLock();
				return default;
			}

			public ValueTask AcquireWriterLockAsync(CancellationToken cancellationToken = default)
			{
				_rwlock.EnterWriteLock();
				return default;
			}

			public void ReleaseReaderLock()
			{
				_rwlock.ExitReadLock();
			}

			public void ReleaseWriterLock()
			{
				_rwlock.ExitWriteLock();
			}

			public bool TryAcquireReaderLock()
			{
				return _rwlock.TryEnterReadLock(-1);
			}

			public bool TryAcquireWriterLock()
			{
				return _rwlock.TryEnterWriteLock(-1);
			}
		}


		internal class AdapterRWLock : ILock
		{
			private readonly ReaderWriterLock _rwlock = new ReaderWriterLock();

			public bool ReaderSupported => true;

			public bool TrySupported => false;

			public ValueTask AcquireReaderLockAsync(CancellationToken cancellationToken = default)
			{
				_rwlock.AcquireReaderLock(-1);
				return default;
			}

			public ValueTask AcquireWriterLockAsync(CancellationToken cancellationToken = default)
			{
				_rwlock.AcquireWriterLock(-1);
				return default;
			}

			public void ReleaseReaderLock()
			{
				_rwlock.ReleaseReaderLock();
			}

			public void ReleaseWriterLock()
			{
				_rwlock.ReleaseWriterLock();
			}

			public bool TryAcquireReaderLock()
			{
				throw new NotSupportedException();
			}

			public bool TryAcquireWriterLock()
			{
				throw new NotSupportedException();
			}
		}

		internal class AdapterSemaphoreSlim : ILock
		{
			private SemaphoreSlim _s = new SemaphoreSlim(1, 1);

			public bool ReaderSupported => false;

			public bool TrySupported => false;

			public ValueTask AcquireReaderLockAsync(CancellationToken cancellationToken = default)
			{
				return new ValueTask(_s.WaitAsync(cancellationToken));
			}


			public ValueTask AcquireWriterLockAsync(CancellationToken cancellationToken = default)
			{
				return new ValueTask(_s.WaitAsync(cancellationToken));
			}

			public void ReleaseReaderLock()
			{
				_s.Release();
			}

			public void ReleaseWriterLock()
			{
				_s.Release();
			}

			public bool TryAcquireReaderLock()
			{
				throw new NotSupportedException();
			}

			public bool TryAcquireWriterLock()
			{
				throw new NotSupportedException();
			}
		}

		#endregion
	}
}
