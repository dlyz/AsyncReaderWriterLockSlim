using System;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;

namespace DLyz.Threading
{
	[DebuggerDisplay("{_state}")]
	public class AsyncReaderWriterLockSlim
	{
		/// <summary>
		/// <see cref="AsyncReaderWriterLockSlim"/> options set.
		/// </summary>
		public readonly struct Options
		{
			private readonly bool _elevatedWriters;
			private readonly bool _elevatedReaders;
			private readonly bool _inverseRunContinuationsAsynchronously;

			private static ArgumentException CreateReadersAndWritersElevatedError()
			{
				return new ArgumentException("Writers and readers can not both be elevated.");
			}

			/// <summary>
			/// When true, writes will have higher priority then not-started reads.
			/// Both this property and <see cref="ElevatedReaders"/> can not be <see langword="true" /> for one lock,
			/// when both of the properties are <see langword="false" />, fair locking strategy used.
			/// Default is <see langword="false" />.
			/// </summary>
			public readonly bool ElevatedWriters
			{
				get => _elevatedWriters;
				init => _elevatedWriters = _elevatedReaders ? throw CreateReadersAndWritersElevatedError() : value;
			}

			/// <summary>
			/// When true, readers will have higher priority then not-started writes.
			/// Both this property and <see cref="ElevatedWriters"/> can not be <see langword="true" /> for one lock.
			/// when both of the properties are <see langword="false" />, fair locking strategy used.
			/// Default is <see langword="false" />.
			/// </summary>
			public readonly bool ElevatedReaders
			{
				get => _elevatedReaders;
				init => _elevatedReaders = _elevatedWriters ? throw CreateReadersAndWritersElevatedError() : value;
			}

			/// <summary>
			/// When <see langword="false" />, lock awaits will continue on lock release thread if possible.
			/// Default is <see langword="true" />.
			/// </summary>
			public readonly bool RunContinuationsAsynchronously
			{
				get => !_inverseRunContinuationsAsynchronously;
				init => _inverseRunContinuationsAsynchronously = !value;
			}
		}

		/// <summary>
		/// Creates a new lock.
		/// </summary>
		public AsyncReaderWriterLockSlim(
			Options options = default
			)
		{
			if (options.ElevatedWriters)
			{
				_elevatedKind = ElevatedKind.Writers;
			}
			else if (options.ElevatedReaders)
			{
				_elevatedKind = ElevatedKind.Readers;
			}

			_runContinuationsAsynchronously = options.RunContinuationsAsynchronously;

			_queueLock = new QueueLock(null);

			////#if DEBUG
			////			_queueLock = new SpinLock(enableThreadOwnerTracking: true);
			////#else
			////			_queueLock = new SpinLock(enableThreadOwnerTracking: false);
			////#endif
		}

		private enum ElevatedKind
		{
			None = 0,
			Writers = 1,
			Readers = 2,
		}

		private readonly bool _runContinuationsAsynchronously;

		private readonly ElevatedKind _elevatedKind;

		private /*not readonly!*/ StateSource _state;

		private /*not readonly*/ QueueLock _queueLock;

		private /*not readonly!*/ Queue _queue;

		// used only when elevatedWrites or elvatedReads is true
		private /*not readonly!*/ Queue _elevatedQueue;

		private ref Queue ReadersQueue => ref (_elevatedKind == ElevatedKind.Readers ? ref _elevatedQueue : ref _queue);

		private ref Queue WritersQueue => ref (_elevatedKind == ElevatedKind.Writers ? ref _elevatedQueue : ref _queue);

		private ref Queue GetQueue(Node node)
		{
			if (_elevatedKind == ElevatedKind.None || (node.IsWriter ? ElevatedKind.Writers : ElevatedKind.Readers) != _elevatedKind)
			{
				return ref _queue;
			}
			else
			{
				return ref _elevatedQueue;
			}
		}

		private bool TryRemoveFromQueue(Node node, ref SpinWait spinner)
		{
			ref var queue = ref GetQueue(node);
			var lockTaken = false;
			try
			{
				_queueLock.Enter(ref lockTaken, ref spinner);
				return queue.TryRemoveUnsafe(node);
			}
			finally
			{
				if (lockTaken)
				{
					_queueLock.Exit();
				}
			}
		}

		public bool TryAcquireReaderLock()
		{
			var spinWait = new SpinWait();
			return TryAcquireReaderLock(ref spinWait);
		}

		private bool TryAcquireReaderLock(ref SpinWait spinWait, bool onReaderQueued = false)
		{
			var state = _state.Read();
			while (true)
			{
				State prev;
				var isWriterActive = state.IsWriterActive;
				var isQueueChanged = state.QueueChangedFlag;
				var isReadersElevated = _elevatedKind == ElevatedKind.Readers;
				if (!isWriterActive && (!isQueueChanged || isReadersElevated))
				{
					if (state.IsMaxReaders)
					{
						throw new InvalidOperationException($"Can not acquire reader lock because active readers limit ({State.MaxReaders}) reached.");
					}

					prev = _state.CompareExchange(comparand: state, value: state.AddReader());
					if (prev == state)
					{
						return true;
					}
				}
				else if (onReaderQueued)
				{
					if (isWriterActive && !isQueueChanged)
					{
						prev = _state.CompareExchange(comparand: state, value: state.SetQueueChanged());
						if (prev == state)
						{
							return false;
						}
					}
					else
					{
						return false;
					}
				}
				else
				{
					return false;
				}

				var rereadState = spinWait.NextSpinWillYield;
				spinWait.SpinOnce();

				if (rereadState)
				{
					state = _state.Read();
				}
				else
				{
					state = prev;
				}
			}
		}

		public ValueTask AcquireReaderLockAsync(CancellationToken cancellationToken = default)
		{
			var spinWait = new SpinWait();

			if (TryAcquireReaderLock(ref spinWait))
			{
				return default;
			}

			if (cancellationToken.IsCancellationRequested)
			{
				return new ValueTask(Task.FromException(new OperationCanceledException(cancellationToken)));
			}

			var node = NodePool.CreateNode(this, isWriter: false);

			ref var queue = ref ReadersQueue;

			while (true)
			{
				// TryEnqueue may throw on overflow
				if (queue.TryEnqueue(node, ref _queueLock))
				{
					// while adding to queue, all writers may already complete
					// anyway we have to set queue changed flag
					if (TryAcquireReaderLock(ref spinWait, onReaderQueued: true))
					{
						if (TryRemoveFromQueue(node, ref spinWait))
						{
							// not yet used
							node.ReleaseUnused();
						}
						else
						{
							// may be completed by lock acquire in background.
							// in this case we have captured two reader locks for the request,
							// so we should release one of them
							node.AfterExtraEnqueued(out var alreadyAcquired);
							if (alreadyAcquired)
							{
								// already acquired, releasing one extra lock
								ReleaseReaderLocks(1);
							}
						}

						return default;
					}
					// after complete writer will drain the queue
					else
					{
						return node.AfterEnqueuedBeforeReturnTask(cancellationToken);
					}
				}

				var tryReaquire = spinWait.NextSpinWillYield;
				// enqueue failed due to race condition
				spinWait.SpinOnce();

				if (tryReaquire)
				{
					// while adding to queue, all writers already completes
					if (TryAcquireReaderLock(ref spinWait))
					{
						node.ReleaseUnused();
						return default;
					}
				}
			}
		}


		public bool TryAcquireWriterLock()
		{
			var spinWait = new SpinWait();
			return TryAcquireWriterLock(ref spinWait);
		}

		private bool TryAcquireWriterLock(ref SpinWait spinWait, bool onWriterQueued = false)
		{
			var state = _state.Read();

			while (true)
			{
				State prev;

				if (state.IsFree)
				{
					prev = _state.CompareExchange(comparand: state, value: State.WriteLocked);
					if (prev == state)
					{
						return true;
					}
				}
				else if (onWriterQueued)
				{
					if (!state.QueueChangedFlag)
					{
						// forbid readers to acquire or if write-locked, mark queue as changed
						prev = _state.CompareExchange(comparand: state, value: state.SetQueueChanged());
						if (prev == state)
						{
							return false;
						}
					}
					else
					{
						return false;
					}
				}
				else
				{
					return false;
				}

				var rereadState = spinWait.NextSpinWillYield;
				spinWait.SpinOnce();

				if (rereadState)
				{
					state = _state.Read();
				}
				else
				{
					state = prev;
				}
			}
		}

		public ValueTask AcquireWriterLockAsync(CancellationToken cancellationToken = default)
		{
			var spinWait = new SpinWait();

			if (TryAcquireWriterLock(ref spinWait))
			{
				return default;
			}

			if (cancellationToken.IsCancellationRequested)
			{
				return new ValueTask(Task.FromException(new OperationCanceledException(cancellationToken)));
			}

			var node = NodePool.CreateNode(this, isWriter: true);

			ref var queue = ref WritersQueue;

			while (true)
			{
				// TryEnqueue may throw on overflow
				if (queue.TryEnqueue(node, ref _queueLock))
				{
					// while adding to queue, lock may become free
					// plus we have to indicate a writer in the queue in any way
					if (TryAcquireWriterLock(ref spinWait, onWriterQueued: true))
					{
						// have to stay in the queue because we just have captured the lock above.
						var removed = TryRemoveFromQueue(node, ref spinWait);
						Debug.Assert(removed);
						node.ReleaseUnused();
						return default;
					}
					// after complete writer will drain the queue
					else
					{
						return node.AfterEnqueuedBeforeReturnTask(cancellationToken);
					}
				}

				var tryReaquire = spinWait.NextSpinWillYield;
				// enqueue failed due to race condition
				spinWait.SpinOnce();

				if (tryReaquire)
				{
					// while adding to queue, lock became free
					if (TryAcquireWriterLock(ref spinWait))
					{
						node.ReleaseUnused();
						return default;
					}
				}
			}
		}

		public void ReleaseReaderLock()
		{
			ReleaseReaderLocks(1);
		}

		private void ReleaseReaderLocks(int readersCount)
		{
			var state = _state.Decrement(readersCount);
			Debug.Assert(state.DebugCheckAfterReadersRelease(readersCount));

			if (state.CanStartWritersAfterReaderRelease)
			{
				var prev = _state.CompareExchange(comparand: state, State.WriteLocked);

				Debug.Assert(_elevatedKind == ElevatedKind.Readers || prev == state);

				if (prev == state)
				{
					var spinWait = new SpinWait();
					CompleteAwaitsUnderWriterLock(ref spinWait);
				}

			}
		}


		public void ReleaseWriterLock()
		{
			var spinWait = new SpinWait();
			CompleteAwaitsUnderWriterLock(ref spinWait);
		}

		#region unlocking

#if DEBUG
		private int _writerThread;
#endif

		private void CompleteAwaitsUnderWriterLock(ref SpinWait spinWait)
		{
#if DEBUG
			Volatile.Write(ref _writerThread, Thread.CurrentThread.ManagedThreadId);
			void AssertStillIn()
			{
				Debug.Assert(_state.Read().IsWriterActive);
				Debug.Assert(Volatile.Read(ref _writerThread) == Thread.CurrentThread.ManagedThreadId);
			}
#else
			void AssertStillIn()
			{
			}
#endif

			var dequeueState = new DequeueState();

			while (true)
			{
				AssertStillIn();

				dequeueState.HasNextWriter = false;

				Node? writer;

				var elevatedMode = _elevatedKind;
				var lockTaken = false;
				try
				{
					_queueLock.Enter(ref lockTaken, ref spinWait);
					if (elevatedMode == ElevatedKind.None)
					{
						writer = DequeueUnsafe(ref dequeueState);
					}
					else if (elevatedMode == ElevatedKind.Writers)
					{
						writer = DequeueWithElevatedWritersUnsafe(ref dequeueState);
					}
					else
					{
						Debug.Assert(elevatedMode == ElevatedKind.Readers);
						writer = DequeueWithElevatedReadersUnsafe(ref dequeueState);
					}

				}
				finally
				{
					if (lockTaken)
					{
						_queueLock.Exit();
					}
				}


				AssertStillIn();
				if (writer != null)
				{
					Debug.Assert(writer.IsWriter);
					if (writer.TryCompleteAcquired())
					{
						return;
					}
					else
					{
						continue;
					}
				}


				// at this point we either have only readers (queues may change right now though),
				// or we have a writer, but is't fairly follows some readers, or readers are elevated.


				// this is a fair case with writer:
				// we can ignore parallel queue changes, cause anyway we'll return here
				// when all dequeued readers complete for that enqueued writer
				// (even in case of writer cancellation, cause queue changed flag won't be unset).
				// otherwise we have to dequeue new readers if queue has changed.
				if (elevatedMode != ElevatedKind.Readers && dequeueState.HasNextWriter)
				{
					Debug.Assert(dequeueState.ReadersCount > 0);

					_state.Write(State.FromReaders(dequeueState.ReadersCount).SetQueueChanged());

					CompleteReaders(ref dequeueState);
					return;
				}

				var state = _state.Read();
				AssertStillIn();

				if (state.QueueChangedFlag)
				{
					// clearing queue changed flag
					// and go to dequeue new nodes
					_state.Write(State.WriteLocked);
				}
				else
				{
					var nextState = State.FromReaders(dequeueState.ReadersCount);
					if (dequeueState.HasNextWriter)
					{
						Debug.Assert(elevatedMode == ElevatedKind.Readers);
						Debug.Assert(dequeueState.ReadersCount != 0);
						nextState = nextState.SetQueueChanged();
					}

					var prev = _state.CompareExchange(comparand: state, value: nextState);
					if (prev == state)
					{
						if (dequeueState.ReadersCount != 0)
						{
							CompleteReaders(ref dequeueState);
						}

						return;
					}
					else
					{
						// only reason for background state change cause we are under writer lock
						Debug.Assert(prev.QueueChangedFlag);
					}
				}


				// we are not using spinWait in the end of the cycle iteration
				// cause in should have higher priority then any AcquireMethod
				// cause it unloads the queue
			}

		}

		private void CompleteReaders(ref DequeueState dequeueState)
		{
			int phantomReaders = 0;

			while (dequeueState.ReadersHead != null)
			{
				var current = dequeueState.ReadersHead;
				current.DebugCheckActiveNode();

				dequeueState.ReadersHead = current._qNext;
				current._qNext = null;

				if (!current.TryCompleteAcquired())
				{
					++phantomReaders;
				}
			}

			if (phantomReaders != 0)
			{
				ReleaseReaderLocks(phantomReaders);
			}
		}

		private Node? DequeueWithElevatedWritersUnsafe(ref DequeueState state)
		{
			if (state.AllowWriter)
			{
				var writer = _elevatedQueue.TryDequeueUnsafe();
				if (writer != null)
				{
					return writer;
				}
			}
			else
			{
				if (!_elevatedQueue.IsEmptyUnsafe())
				{
					state.HasNextWriter = true;
					return null;
				}
			}

			_queue.DequeueAllAsReadersUnsafe(ref state);

			return null;
		}


		private Node? DequeueWithElevatedReadersUnsafe(ref DequeueState state)
		{
			_elevatedQueue.DequeueAllAsReadersUnsafe(ref state);

			if (state.AllowWriter)
			{
				return _queue.TryDequeueUnsafe();
			}
			else
			{
				if (!_queue.IsEmptyUnsafe())
				{
					state.HasNextWriter = true;
				}

				return null;
			}
		}

		private Node? DequeueUnsafe(ref DequeueState state)
		{
			var node = _queue.DequeueWriterOrReadersChainUnsafe(ref state);

			if (node != null && node.IsWriter)
			{
				Debug.Assert(state.ReadersCount == 0);
				return node;
			}

			return null;
		}


		#endregion

		#region state

		[DebuggerDisplay("{DebuggerView}")]
		private readonly struct State
		{
			public State(uint value)
			{
				_state = value;
			}

			public static implicit operator uint(State state)
			{
				return state._state;
			}

			public static implicit operator State(uint value)
			{
				return new State(value);
			}

			// 1-st (highest) bit - queue changed (readers can not acquire the lock)
			// 2-rd to 32-nd bits - count of active readers
			// 0b?111...1111 - special value means writer lock
			internal readonly uint _state;

			private const uint _firstBit = 0x80000000;
			private const uint _allExceptFirstBits = 0x7fffffff;

			public const int MaxReaders = (int)_allExceptFirstBits - 1;

			public bool IsMaxReaders => _state == MaxReaders;

			private uint CountPart => _state & _allExceptFirstBits;

			public int ActiveReadersCount
			{
				get
				{
					var cnt = CountPart;
					if (cnt == _allExceptFirstBits)
					{
						return 0;
					}
					else
					{
						return (int)cnt;
					}
				}
			}

			public bool IsWriterActive => CountPart == _allExceptFirstBits;

			internal string DebuggerView
			{
				get
				{
					var str = new StringBuilder();
					if (IsWriterActive)
					{
						str.Append("Locked by writer");
						if (QueueChangedFlag)
						{
							str.Append(", queue changed");
						}
					}
					else if (ActiveReadersCount != 0)
					{
						str.Append("Locked by ").Append(ActiveReadersCount).Append(" readers");
						if (QueueChangedFlag)
						{
							str.Append(", writer(s) awaiting");
						}
					}
					else
					{
						str.Append("Free");
						if (QueueChangedFlag)
						{
							str.Append(", writer(s) awaiting");
						}
					}

					return str.ToString();
				}
			}

			public bool QueueChangedFlag => (_state & _firstBit) != 0;

			public bool IsFree => _state == 0;

			public bool CanStartWritersAfterReaderRelease => _state == _firstBit;

			public State AddReader()
			{
				Debug.Assert(!IsWriterActive && _state != MaxReaders, "Can not add reader.");
				return _state + 1;
			}

			public State SetQueueChanged()
			{
				return _state | _firstBit;
			}

			public static State WriteLocked => _allExceptFirstBits;

			public bool DebugCheckAfterReadersRelease(int count)
			{
				return !IsWriterActive && !new State(_state + (uint)count).IsWriterActive;
			}

			public static State FromReaders(int count)
			{
				Debug.Assert(count >= 0 && count <= MaxReaders, "Count may not be negative and intmax");
				return (uint)count;
			}
		}

		[DebuggerDisplay("{Value}")]
		private struct StateSource
		{
			private uint _state;

			private State Value => _state;

			public State Read()
			{
				return Volatile.Read(ref _state);
			}

			public void Write(State value)
			{
				Volatile.Write(ref _state, value);
			}

			public bool Cas(State comparand, State value)
			{
				return comparand == CompareExchange(comparand, value);
			}

			public State CompareExchange(State comparand, State value)
			{
				return Interlocked.CompareExchange(ref _state, value._state, comparand._state);
			}

			public State Exchange(State value)
			{
				return Interlocked.Exchange(ref _state, value._state);
			}

			public State Decrement(int count)
			{
				return Interlocked.Add(ref _state, (uint)(-count));
			}
		}

		#endregion


		#region Nodes

		// for tests only
		internal static bool IsCacheEnabled
		{
			get => Volatile.Read(ref NodePool._isCacheEnabled);
			set => Volatile.Write(ref NodePool._isCacheEnabled, value);
		}

		private static class NodePool
		{

			private static readonly int _maxCacheSize = Environment.ProcessorCount * 8;

			/// <summary>Lock used to protect the shared cache of boxes.</summary>
			/// <remarks>The code that uses this assumes a runtime without thread aborts.</remarks>
			private static int _nodeCacheLock;

			private static bool TryEnterLock()
			{
				return Interlocked.Exchange(ref _nodeCacheLock, 1) == 0;
			}

			private static void ExitLock()
			{
				Volatile.Write(ref _nodeCacheLock, 0);
			}

			/// <summary>Singly-linked list cache of boxes.</summary>
			/// <remarks>
			/// Cache can not be static cause queue will always require one node
			/// (as a sentinel head which will change during lock lifetime),
			/// so in case of many lock instance the cache may be exhausted only on this sentinel nodes,
			/// so the cache will be useless.
			/// </remarks>
			private static Node? _nodeCache;

			/// <summary>The number of items stored in <see cref="_nodeCache"/>.</summary>
			private static int _nodeCacheSize;

			internal static bool _isCacheEnabled = !(AppContext.TryGetSwitch("AsyncReaderWriterLockSlim.CacheDisabled", out var value) && value);



			public static Node CreateNode(AsyncReaderWriterLockSlim owner, bool isWriter)
			{
				// Try to acquire the lock to access the cache.  If there's any contention, don't use the cache.
				if (_isCacheEnabled && TryEnterLock())
				{
					// If there are any instances cached, take one from the cache stack and use it.
					Node? node = _nodeCache;
					if (!(node is null))
					{
						node.DebugCheckCached();
						_nodeCache = node._qNext;

						_nodeCacheSize--;
						Debug.Assert(_nodeCacheSize >= 0, "Expected the cache size to be non-negative.");

						// Release the lock and return the box.
						ExitLock();


						node.DebugCheckCached();

						node._qNext = null;

						node.Initialize(owner, isWriter);

						node.DebugCheckActiveNode();

						return node;
					}

					// No objects were cached.  We'll just create a new instance.
					Debug.Assert(_nodeCacheSize == 0, "Expected cache size to be 0.");

					// Release the lock.
					ExitLock();
				}

				{
					// Couldn't quickly get a cached instance, so create a new instance.
					var node = new Node();
					node.Initialize(owner, isWriter);
					return node;
				}
			}

			public static void ReturnNode(Node node)
			{
				// Try to acquire the cache lock.  If there's any contention, or if the cache is full, we just throw away the object.
				if (_isCacheEnabled && TryEnterLock())
				{
					Debug.Assert(Volatile.Read(ref node._qNext) is null, "Expected box to not be part of cached list or queue.");
					Debug.Assert(Volatile.Read(ref node._qPrev) is null, "Expected box to not be part of cached list or queue.");

					if (_nodeCacheSize < _maxCacheSize)
					{
						// Push the box onto the cache stack for subsequent reuse.
						node.SetCachedFlag();
						node._qNext = _nodeCache;
						_nodeCache = node;
						_nodeCacheSize++;
						Debug.Assert(_nodeCacheSize > 0 && _nodeCacheSize <= _maxCacheSize, "Expected cache size to be within bounds.");
					}

					// Release the lock.
					ExitLock();
				}
			}
		}

		private struct Dummy { }

		private class Node : IValueTaskSource
		{
			private static Action<object?> _cancellationAction = (state) => ((Node)state!).OnCancelled();

			// for external usage
			internal Node? _qPrev;
			internal Node? _qNext;

			private bool _isWriter;
			// 0 - not completed, 1 - completed, 3 - cancelled from acquire method
			private int _isCompleted;

			private ManualResetValueTaskSourceCore<Dummy> _valueTaskSource;
			private CancellationTokenRegistration _cancellationRegistration;
			private AsyncReaderWriterLockSlim _owner = null!;

			public bool IsCompleted => Volatile.Read(ref _isCompleted) != 0;

#if DEBUG
			private int _initCount;
			private int _deinitCount;
			private bool _isCached;
#endif

			internal void Initialize(
				AsyncReaderWriterLockSlim owner,
				bool isWriter
				)
			{
#if DEBUG
				Debug.Assert(++_initCount == _deinitCount + 1);
				_isCached = false;
				//Debug.Assert(Interlocked.Increment(ref _initCount) == Volatile.Read(ref _deinitCount) + 1);
#endif
				Debug.Assert(_qNext is null);
				Debug.Assert(_qPrev is null);
				Debug.Assert(_owner is null);

				_owner = owner;
				_isWriter = isWriter;
				_isCompleted = 0;
				_valueTaskSource.RunContinuationsAsynchronously = owner._runContinuationsAsynchronously;
			}

			internal void Deinitialize()
			{
#if DEBUG
				Debug.Assert(++_deinitCount == _initCount);
				//Debug.Assert(Interlocked.Increment(ref _deinitCount) == Volatile.Read(ref _initCount));
#endif
				DebugCheckActiveNode();

				Debug.Assert(_qNext is null, "Expected box to not be part of cached list or queue.");
				Debug.Assert(_qPrev is null, "Expected box to not be part of cached list or queue.");
				_owner = null!;
			}

			public void DebugCheckActiveNode()
			{
				Debug.Assert(_owner != null);
#if DEBUG
				Debug.Assert(!_isCached);
#endif
			}

			public void SetCachedFlag()
			{
#if DEBUG
				Debug.Assert(!_isCached);
				_isCached = true;
#endif
			}

			public void DebugCheckCached()
			{
#if DEBUG
				Debug.Assert(_isCached);
#endif
			}

			public bool IsWriter => _isWriter;

			void IValueTaskSource.GetResult(short token)
			{
				DebugCheckActiveNode();

				if (token != _valueTaskSource.Version)
				{
					throw new InvalidOperationException();
				}


				// may not throw
				UnregisterCancellation();

				try
				{
					Debug.Assert(token == _valueTaskSource.Version);
					_valueTaskSource.GetResult(token);
				}
#if DEBUG
				catch (OperationCanceledException)
				{
					throw;
				}
				catch (Exception ex)
				{
					Debug.Assert(ex == null);
				}
#endif
				finally
				{
					ReturnOrDropNode(resetValueTask: true);
				}
			}

			ValueTaskSourceStatus IValueTaskSource.GetStatus(short token)
				=> _valueTaskSource.GetStatus(token);

			void IValueTaskSource.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
				=> _valueTaskSource.OnCompleted(continuation, state, token, flags);


			// node added to the queue and my be concerrently acquired
			public ValueTask AfterEnqueuedBeforeReturnTask(CancellationToken cancellationToken)
			{
				DebugCheckActiveNode();
				// may not throw
				_cancellationRegistration = cancellationToken.Register(_cancellationAction, this);

				return new ValueTask(this, _valueTaskSource.Version);
			}


			// node added to the queue, but user will never see this node (ValueTask).
			public void AfterExtraEnqueued(out bool alreadyAcquired)
			{
				DebugCheckActiveNode();

				// after this we cannot use the node if it is !alreadyAcquired
				// cause it may be released from TryCompleteAcquired
				var prevCompletedValue = Interlocked.CompareExchange(ref _isCompleted, 3, 0);
				alreadyAcquired = (prevCompletedValue == 1);

				if (alreadyAcquired)
				{
					// this is rare situation, so we can just drop the node to the gc
					////// can not return node here cause TryCompleteAcquired
					////// may in parallel use _valueTaskSource.
					////// so emulating user ValueTask usage
					////var token = _valueTaskSource.Version;
					////var status = _valueTaskSource.GetStatus(token);
					////if (status == ValueTaskSourceStatus.Succeeded)
					////{
					////	ReturnOrDropNode(resetValueTask: true);
					////}
					////else
					////{
					////	Debug.Assert(status == ValueTaskSourceStatus.Pending);
					////	_valueTaskSource.RunContinuationsAsynchronously = false;
					////	_valueTaskSource.OnCompleted(
					////		me => ((Node)me!).ReturnOrDropNode(resetValueTask: true),
					////		this,
					////		_valueTaskSource.Version,
					////		ValueTaskSourceOnCompletedFlags.None
					////		);
					////}
				}
				else
				{
					Debug.Assert(prevCompletedValue == 0);
				}
			}

			public bool TryCompleteAcquired()
			{
				DebugCheckActiveNode();

				var prevCompletedValue = Interlocked.CompareExchange(ref _isCompleted, 1, 0);

				// if this is an 'extra' node
				if (prevCompletedValue != 0)
				{
					Debug.Assert(prevCompletedValue == 3);
					ReturnOrDropNode(resetValueTask: false);
					return false;
				}

				try
				{
					// must be the last node instruction: after it the node may be already in the pool or even reused
					_valueTaskSource.SetResult(default);
				}
				catch (Exception ex)
				{
					// notes about exceptions handling:
					// _valueTaskSource.SetResult() may throw when
					// (RunContinuationsAsynchronously == false) && (_continuation is not null) && (_executionContext is null) && (_capturedContext is null)
					// and _continuation call throws.
					// but we cannot throw from the release methods.
					// so emulating exception in async void method, that is emulating unhandled exception in worker thread.

					var edi = ExceptionDispatchInfo.Capture(ex);
					ThreadPool.QueueUserWorkItem(static state => ((ExceptionDispatchInfo)state!).Throw(), edi);
				}

				return true;
			}

			private void OnCancelled()
			{
				DebugCheckActiveNode();

				_cancellationRegistration = default;

				var spinner = new SpinWait();
				if (_owner.TryRemoveFromQueue(this, ref spinner))
				{
					// may throw, but it's ok, exception will be propagated to the cancellation caller,
					// the state of the lock remains consistent.
					// must be the last instruction: after it the node may be already in the pool or even reused
					_valueTaskSource.SetException(new OperationCanceledException(_cancellationRegistration.Token));
					return;
				}
			}

			private void UnregisterCancellation()
			{
				// after dispose it is not possible to have cancellation callback
				_cancellationRegistration.Dispose();
				_cancellationRegistration = default;
			}



			// the node has not been added to the queue (or has been removed before any usage),
			// user will never see this node, so it can be destroyed right now
			public void ReleaseUnused()
			{
				ReturnOrDropNode(resetValueTask: false);
			}


			private void ReturnOrDropNode(bool resetValueTask)
			{
				// Clear out associated context to avoid keeping arbitrary state referenced by
				// lifted locals.  We want to do this regardless of whether we end up caching the box or not, in case
				// the caller keeps the box alive for an arbitrary period of time.
				Deinitialize();

				if (resetValueTask)
				{
					// If reusing the object would result in potentially wrapping around its version number, just throw it away.
					// This provides a modicum of additional safety when ValueTasks are misused (helping to avoid the case where
					// a ValueTask is illegally re-awaited and happens to do so at exactly 2^16 uses later on this exact same instance),
					// at the expense of potentially incurring an additional allocation every 65K uses.
					if ((ushort)_valueTaskSource.Version == ushort.MaxValue)
					{
						return;
					}

					// Reset the MRVTSC.  We can either do this here, in which case we may be paying the (small) overhead
					// to reset the box even if we're going to drop it, or we could do it while holding the lock, in which
					// case we'll only reset it if necessary but causing the lock to be held for longer, thereby causing
					// more contention.  For now at least, we do it outside of the lock. (This must not be done after
					// the lock is released, since at that point the instance could already be in use elsewhere.)
					// We also want to increment the version number even if we're going to drop it, to maximize the chances
					// that incorrectly double-awaiting a ValueTask will produce an error.
					_valueTaskSource.Reset();
				}

				NodePool.ReturnNode(this);
			}

		}

		#endregion

		#region queue

		private ref struct DequeueState
		{
			public Node? ReadersHead;
			public Node? ReadersTail;

			public int ReadersCount;
			public bool HasNextWriter;
			public bool AllowWriter => ReadersCount == 0;

			public void ConcatHeadBeforeTailChange(Node head)
			{
				head.DebugCheckActiveNode();

				if (ReadersHead is null)
				{
					Debug.Assert(ReadersTail == null);
					ReadersHead = head;
				}
				else
				{
					Debug.Assert(ReadersTail != null);
					Debug.Assert(ReadersTail!._qNext == null);
					ReadersTail!.DebugCheckActiveNode();
					ReadersTail!._qNext = head;
				}
			}
		}

		private struct QueueLock
		{
			private int _locked;

			public QueueLock(object? dummy)
			{
				_locked = 0;
			}

			public void TryEnter(ref bool lockTaken)
			{
				lockTaken = Interlocked.Exchange(ref _locked, 1) == 0;
			}

			public void Enter(ref bool lockTaken)
			{
				var spinner = new SpinWait();
				Enter(ref lockTaken, ref spinner);
			}

			public void Enter(ref bool lockTaken, ref SpinWait spinner)
			{
				while (true)
				{
					TryEnter(ref lockTaken);
					if (lockTaken)
					{
						return;
					}

					spinner.SpinOnce();
				}
			}

			public void Exit()
			{
				Volatile.Write(ref _locked, 0);
			}

		}

		private struct Queue
		{
			private Node? _head;
			private Node? _tail;
			private int _count;

			public bool TryEnqueue(Node node, ref QueueLock mutex)
			{
				bool lockTaken = false;
				try
				{
					mutex.TryEnter(ref lockTaken);
					if (lockTaken)
					{
						EnqueueUnsafe(node);
					}

					return lockTaken;
				}
				finally
				{
					if (lockTaken)
					{
						mutex.Exit();
					}
				}
			}

			private void EnqueueUnsafe(Node node)
			{
				Debug.Assert(node._qNext == null);
				Debug.Assert(node._qPrev == null);

				if (_count == State.MaxReaders)
				{
					throw new InvalidOperationException($"Can not enqueue new node because queue size limit ({State.MaxReaders}) reached.");
				}

				++_count;

				if (_tail == null)
				{
					Debug.Assert(_head == null);

					_head = _tail = node;
				}
				else
				{
					Debug.Assert(_head != null);
					Debug.Assert(_tail._qNext == null);

					_tail._qNext = node;
					node._qPrev = _tail;
					_tail = node;
				}
			}

			public bool TryRemoveUnsafe(Node node)
			{
				var prev = node._qPrev;
				if (prev != null)
				{
					node._qPrev = null;

					var next = node._qNext;
					prev._qNext = next;

					if (next == null)
					{
						_tail = prev;
					}
					else
					{
						next._qPrev = prev;
						node._qNext = null;
					}

					--_count;
					return true;
				}
				else if (node == _head)
				{
					var next = node._qNext;
					_head = next;

					if (next == null)
					{
						_tail = null;
					}
					else
					{
						next._qPrev = null;
						node._qNext = null;
					}

					--_count;
					return true;
				}
				else
				{
					return false;
				}
			}


			public bool IsEmptyUnsafe()
			{
				return _head == null;
			}

			public Node? TryDequeueUnsafe()
			{
				var head = _head;
				if (head == null)
				{
					return null;
				}

				DoDequeueUnsafe(head);
				return head;
			}

			private void DoDequeueUnsafe(Node head)
			{
				Debug.Assert(head._qPrev == null);

				--_count;
				var next = head._qNext;
				if (next is null)
				{
					_head = _tail = null;
				}
				else
				{
					_head = next;
					next._qPrev = null;
					head._qNext = null;
				}
			}


			public void DequeueAllAsReadersUnsafe(ref DequeueState dequeueState)
			{
				var head = _head;
				if (head == null)
				{
					return;
				}

				Debug.Assert(head._qPrev == null);

				++dequeueState.ReadersCount;
				--_count;
				dequeueState.ConcatHeadBeforeTailChange(head);

				var current = head._qNext;
				while (current != null)
				{
					--_count;
					++dequeueState.ReadersCount;
					current._qPrev = null;
					current = current._qNext;
				}

				dequeueState.ReadersTail = _tail;
				_head = _tail = null;
			}

			public Node? DequeueWriterOrReadersChainUnsafe(ref DequeueState dequeueState)
			{
				var head = _head;
				if (head == null)
				{
					return null;
				}

				if (head.IsWriter)
				{
					if (!dequeueState.AllowWriter)
					{
						return null;
					}

					DoDequeueUnsafe(head);
				}
				else
				{
					DoDequeueReadersChainUnsafe(head, ref dequeueState);
				}

				return head;
			}

			private void DoDequeueReadersChainUnsafe(
				Node head,
				ref DequeueState dequeueState
				)
			{
				--_count;
				++dequeueState.ReadersCount;
				dequeueState.ConcatHeadBeforeTailChange(head);

				var current = head._qNext;

				while (current != null && !current.IsWriter)
				{
					--_count;
					++dequeueState.ReadersCount;
					current._qPrev = null;
					current = current._qNext;
				}

				if (current == null)
				{
					dequeueState.ReadersTail = _tail;
					// all queue removed
					_head = _tail = null;
				}
				else
				{
					Debug.Assert(current.IsWriter);
					dequeueState.ReadersTail = current._qPrev;
					dequeueState.ReadersTail!._qNext = null;
					dequeueState.HasNextWriter = true;
					current._qPrev = null;
					_head = current;
				}
			}

		}

		#endregion
	}
}
