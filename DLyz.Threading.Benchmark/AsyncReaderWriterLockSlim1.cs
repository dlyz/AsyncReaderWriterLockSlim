using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;

namespace DLyz.Threading
{
	/// <summary>
	/// Compared to main AsyncReaderWriterLockSlim version:
	/// - little bit faster,
	/// - does not support nodes pooling due to lock-free queue implementation
	/// - queue is actually lock free compared to spin-locks in main
	/// - cancellation does not remove the node from the queue
	/// - does not support readers elevation
	/// </summary>
	[DebuggerDisplay("{_state}")]
	public class AsyncReaderWriterLockSlim1
	{
		/// <summary>
		/// Creates a new lock.
		/// </summary>
		/// <param name="elevatedWrites">When true, writes will higher priority then not-started reads.</param>
		/// <param name="runContinuationsAsynchronously">When false, lock awaits will continue on lock release thread if possible.</param>
		public AsyncReaderWriterLockSlim1(
			bool elevatedWrites = false,
			bool runContinuationsAsynchronously = true
			)
		{
			_runContinuationsAsynchronously = runContinuationsAsynchronously;
			_queue = new LockFreeQueue(CreateQueueSentinelNode());

			if (elevatedWrites)
			{
				_writersQueue = new LockFreeQueue(CreateQueueSentinelNode());
			}
		}

		private readonly bool _runContinuationsAsynchronously;

		private /*not readonly!*/ StateSource _state;

		// todo: check readers limit
		private /*not readonly!*/ LockFreeQueue _queue;

		// used only when elevatedWrites is true
		private /*not readonly!*/ LockFreeQueue _writersQueue;


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
				if (!isWriterActive && !isQueueChanged)
				{
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

			// todo: wrap into task?
			cancellationToken.ThrowIfCancellationRequested();
			var node = CreateNode(isWriter: false, cancellationToken);

			while (true)
			{
				if (_queue.TryEnqueueIteration(node))
				{
					// while adding to queue, all writers already completes
					// anyway we have to set queue changed flag
					if (TryAcquireReaderLock(ref spinWait, onReaderQueued: true))
					{
						// may be completed by cancellation token,
						// or with the lock acquire in background.
						// in the second case we have captured two reader locks for the request,
						// so we should release one of them
						if (node.TryCompleteAndReleaseValueTaskReferenceBeforeUsage(out var alreadyAcquired))
						{
							// completed here
						}
						else if (alreadyAcquired)
						{
							// already acquired, releasing one extra lock
							ReleaseReaderLocks(1);
						}

						return default;
					}
					// after complete writer will drain the queue
					else
					{
						return node.GetValueTask();
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

			// todo: wrap into task?
			cancellationToken.ThrowIfCancellationRequested();
			var node = CreateNode(isWriter: true, cancellationToken);

			while (true)
			{
				ref var queue = ref (_writersQueue.IsDefault ? ref _queue : ref _writersQueue);

				if (queue.TryEnqueueIteration(node))
				{
					// while adding to queue, lock became free
					// plus we have to indicate a writer in the queue in any way
					if (TryAcquireWriterLock(ref spinWait, onWriterQueued: true))
					{
						// may be completed by cancellation token,
						// but cannot with the lock acquire because we just captured the lock above.
						node.TryCompleteAndReleaseValueTaskReferenceBeforeUsage(out var alreadyAcquired);
						Debug.Assert(!alreadyAcquired);

						return default;
					}
					// after complete writer will drain the queue
					else
					{
						return node.GetValueTask();
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

#if DEBUG
		private int _writerThread;
#endif

		private void CompleteAwaitsUnderWriterLock(ref SpinWait spinWait)
		{
			Debug.Assert(_state.Read().IsWriterActive);

#if DEBUG
			_writerThread = Thread.CurrentThread.ManagedThreadId;
			void AssertStillIn()
			{
				Debug.Assert(_writerThread == Thread.CurrentThread.ManagedThreadId);
			}
#else
			void AssertStillIn()
			{
			}
#endif

			int readersCount = 0;
			Node? readersHead = null;
			Node? readersTail = null;

			var ds = new DequeueUserState();

			while (true)
			{
				AssertStillIn();

				if (!_writersQueue.IsDefault)
				{
					while (_queue.TryDequeue(ref ds, ref spinWait))
					{
						AssertStillIn();

						ds.RemovedHeadNode!.ReleaseQueueReference();

						// todo: auto complete recursion possible
						if (ds.DequeuedNode!.TryCompleteAcquired())
						{
							return;
						}

					}
				}

				while (_queue.TryDequeue(ref ds, ref spinWait))
				{
					AssertStillIn();

					var dequeued = ds.DequeuedNode!;
					// todo: remove
					//Debug.Assert(!dequeued.IsCompleted);

					if (dequeued.IsWriter)
					{
						ds.RemovedHeadNode!.ReleaseQueueReference();

						// todo: auto complete recursion possible
						if (dequeued.TryCompleteAcquired())
						{
							return;
						}
					}
					else
					{
						if (readersCount == 0)
						{
							ds.RemovedHeadNode!.ReleaseQueueReference();

							readersCount = 1;
							readersHead = readersTail = dequeued;
							ds.ReadersOnly = true;
						}
						else
						{
							// can not ReleaseQueueReference here because ds.RemovedHeadNode is the current tail
							// of the invocation list.
							// if the node in the list already cancelled and consumed by user,
							// ReleaseQueueReference here may return the node to the pool,
							// and break invocation list.

							++readersCount;
							Debug.Assert(readersTail!.InvocationNext == null && dequeued.InvocationNext == null);
							readersTail!.InvocationNext = dequeued;
							readersTail = dequeued;
						}
					}
				}

				AssertStillIn();
				if (ds.HasNextWriter)
				{

					Debug.Assert(readersCount > 0);
					Debug.Assert(_state.Read().IsWriterActive);

					_state.Write(State.FromReaders(readersCount).SetQueueChanged());

					CompleteReaders();
					return;
				}

				var state = _state.Read();
				Debug.Assert(state.IsWriterActive);

				while (true)
				{
					AssertStillIn();

					if (state.QueueChangedFlag)
					{
						// clearing queue changed flag
						_state.Write(State.WriteLocked);
						break;
					}

					var prev = _state.CompareExchange(comparand: state, value: State.FromReaders(readersCount));
					if (prev == state)
					{
						if (readersCount != 0)
						{
							CompleteReaders();
						}

						return;
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

			void CompleteReaders()
			{
				int phantomReaders = 0;

				Node? prevNode = null;
				while (readersHead != null)
				{
					AssertStillIn();

					prevNode?.ReleaseQueueReference();

					prevNode = readersHead;
					readersHead = prevNode.InvocationNext;
					prevNode.InvocationNext = null;

					if (!prevNode.TryCompleteAcquired())
					{
						++phantomReaders;
					}
				}

				// prevNode ReleaseQueueReference may not be called here!
				// it is still a sentinel head of the queue.

				// todo: remove
				//Debug.Assert(phantomReaders == 0);

				if (phantomReaders != 0)
				{
					ReleaseReaderLocks(phantomReaders);
				}
			}



		}

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

			// 1-nd (highest) bit - queue changed (readers can not acquire the lock)
			// 2-rd to 32-nd bits - count of active readers
			// 0b?111...1111 - special value means writer lock
			internal readonly uint _state;

			private const uint _firstBit = 0x80000000;
			private const uint _allExceptFirstBits = 0x7fffffff;

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
						return unchecked((int)cnt);
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
				return _state + 1;
			}

			public State SetQueueChanged()
			{
				return _state | _firstBit;
			}

			public static State WriteLocked => _allExceptFirstBits;

			public bool DebugCheckAfterReadersRelease(int count)
			{
				return !IsWriterActive && !new State(unchecked(_state + (uint)count)).IsWriterActive;
			}

			public static State FromReaders(int count)
			{
				// todo: range validation
				return unchecked((uint)count);
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
				return unchecked((uint)Interlocked.CompareExchange(
					ref Unsafe.As<uint, int>(ref _state),
					unchecked((int)value._state),
					unchecked((int)comparand._state)
					));
			}

			public State Exchange(State value)
			{
				return unchecked((uint)Interlocked.Exchange(
					ref Unsafe.As<uint, int>(ref _state),
					unchecked((int)value._state)
					));
			}

			public State Decrement(int count)
			{
				//// todo: check endianess
				return unchecked((uint)Interlocked.Add(
					ref Unsafe.As<uint, int>(ref _state),
					-count
					));
			}
		}

		#endregion


		#region Nodes

		private Node CreateNode(bool isWriter, CancellationToken cancellationToken)
		{
			// Couldn't quickly get a cached instance, so create a new instance.
			var box = new Node(this);
			box.Initialize(isWriter, cancellationToken);
			return box;
		}

		private Node CreateQueueSentinelNode()
		{
			var box = new Node(this);
			box.Initialize(default, default);
			bool isCompletedNow = box.TryCompleteAndReleaseValueTaskReferenceBeforeUsage(out _);
			Debug.Assert(isCompletedNow);
			return box;
		}

		private void NodePrivate_TryReturnNode(Node node)
		{
		}

		private class Node : IValueTaskSource
		{
			private static Action<object?> _cancellationAction = (state) => ((Node)state!).OnCancelled();

			private readonly AsyncReaderWriterLockSlim1 _owner;
			internal Node? _qNext;
			private Node? _invocationNext;
			private bool _isWriter;

			private ManualResetValueTaskSourceCore<object?> _valueTaskSource;
			private CancellationTokenRegistration _cancellationRegistration;

			// 0 - not completed, 1 - completed, 2 - cancelled, 3 - cancelled from acquire method
			private int _isCompleted;

			// 2 references by default - queue and ValueTask
			private int _referenceCounter;

#if DEBUG
			private int _isActiveNode;
			private int _valueTaskReferenceReleased;
			private int _queueReferenceReleased;
			internal int _removedFromQueue;
#endif

			public bool IsCompleted => Volatile.Read(ref _isCompleted) != 0;

			internal Node(AsyncReaderWriterLockSlim1 owner)
			{
				_owner = owner;
				_valueTaskSource.RunContinuationsAsynchronously = owner._runContinuationsAsynchronously;
			}

			internal void Initialize(bool isWriter, CancellationToken cancellationToken)
			{
				Debug.Assert(_qNext is null);

				_isWriter = isWriter;
				_isCompleted = 0;
				_referenceCounter = 2;

#if DEBUG
				Debug.Assert(Interlocked.Exchange(ref _isActiveNode, 1) == 0);
				Volatile.Write(ref _valueTaskReferenceReleased, 0);
				Volatile.Write(ref _queueReferenceReleased, 0);
				Volatile.Write(ref _removedFromQueue, 0);
#endif

				_cancellationRegistration = cancellationToken.Register(_cancellationAction, this);
			}

			internal void Deinitialize()
			{
#if DEBUG
				Debug.Assert(Interlocked.Exchange(ref _isActiveNode, 0) == 1);
#endif
			}

			private void CheckActiveNode()
			{
#if DEBUG
				Debug.Assert(Volatile.Read(ref _isActiveNode) == 1);
#endif
			}

			public bool IsWriter => _isWriter;


			public Node? InvocationNext
			{
				get => _invocationNext;
				set => _invocationNext = value;
			}

			public ValueTask GetValueTask() => new ValueTask(this, _valueTaskSource.Version);

			void IValueTaskSource.GetResult(short token)
			{
				CheckActiveNode();

				if (token != _valueTaskSource.Version)
				{
					throw new InvalidOperationException();
				}

				try
				{
					_valueTaskSource.GetResult(token);
				}
				finally
				{
					ReleaseValueTaskReferenceUnsafe();
				}
			}

			ValueTaskSourceStatus IValueTaskSource.GetStatus(short token)
				=> _valueTaskSource.GetStatus(token);

			void IValueTaskSource.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
				=> _valueTaskSource.OnCompleted(continuation, state, token, flags);



			// node added to the queue, but user will never see this node (ValueTask).
			public bool TryCompleteAndReleaseValueTaskReferenceBeforeUsage(out bool alreadyAcquired)
			{
				CheckActiveNode();

				var prevCompletedValue = Interlocked.CompareExchange(ref _isCompleted, 3, 0);

				if (prevCompletedValue == 0)
				{
					UnregisterCancellation();
				}

				// value task reference
#if DEBUG
				Debug.Assert(Interlocked.Exchange(ref _valueTaskReferenceReleased, 2) == 0);
#endif
				ReleaseReference();

				alreadyAcquired = (prevCompletedValue == 1);
				return prevCompletedValue == 0;
			}

			// the node has been removed from the queue, but it still may be uncompleted cause user still using it.
			public void ReleaseQueueReference()
			{
				CheckActiveNode();

				//Debug.Assert(Volatile.Read(ref _qNext) is null);

#if DEBUG
				Debug.Assert(Interlocked.Exchange(ref _queueReferenceReleased, 1) == 0);
#endif

				ReleaseReference();
			}

			// the node has not been added to the queue, user will never see this node, so it can be destroyed right now
			public void ReleaseUnused()
			{
				CheckActiveNode();

				UnregisterCancellation();
				ReturnOrDropNode();
			}

			// called from GetResult.
			// there is a chance that with incorrect usage of ValueTask
			// this method will be called at any time
			// but probably, there is nothing we can do about this.
			private void ReleaseValueTaskReferenceUnsafe()
			{
				CheckActiveNode();

				// Reset the MRVTSC.  We can either do this here, in which case we may be paying the (small) overhead
				// to reset the box even if we're going to drop it, or we could do it while holding the lock, in which
				// case we'll only reset it if necessary but causing the lock to be held for longer, thereby causing
				// more contention.  For now at least, we do it outside of the lock. (This must not be done after
				// the lock is released, since at that point the instance could already be in use elsewhere.)
				// We also want to increment the version number even if we're going to drop it, to maximize the chances
				// that incorrectly double-awaiting a ValueTask will produce an error.
				_valueTaskSource.Reset();

#if DEBUG
				Debug.Assert(Interlocked.Exchange(ref _valueTaskReferenceReleased, 1) == 0);
#endif

				ReleaseReference();
			}

			private void ReleaseReference()
			{
				var decremented = Interlocked.Decrement(ref _referenceCounter);
				Debug.Assert(decremented >= 0);

				if (decremented == 0)
				{
					ReturnOrDropNode();
				}
			}

			public bool TryCompleteAcquired()
			{
				CheckActiveNode();

				if (Interlocked.CompareExchange(ref _isCompleted, 1, 0) != 0)
				{
					return false;
				}

				// todo: probably may throw?
				UnregisterCancellation();

				// todo: may throw! (user callback)
				_valueTaskSource.SetResult(null);

				return true;
			}

			private void OnCancelled()
			{
				CheckActiveNode();

				if (Interlocked.CompareExchange(ref _isCompleted, 2, 0) != 0)
				{
					return;
				}

				_cancellationRegistration = default;

				// may throw, but it's ok, exception will be propagated to the cancellation caller
				_valueTaskSource.SetException(new OperationCanceledException(_cancellationRegistration.Token));
			}


			private void UnregisterCancellation()
			{
				// todo: check that after dispose it is not possible to have cancellation callback
				_cancellationRegistration.Dispose();
				_cancellationRegistration = default;
			}


			private void ReturnOrDropNode()
			{
				//Debug.Assert(Volatile.Read(ref _qNext) is null, "Expected box to not be part of cached list or queue.");

				// Clear out associated context to avoid keeping arbitrary state referenced by
				// lifted locals.  We want to do this regardless of whether we end up caching the box or not, in case
				// the caller keeps the box alive for an arbitrary period of time.
				Deinitialize();

				// If reusing the object would result in potentially wrapping around its version number, just throw it away.
				// This provides a modicum of additional safety when ValueTasks are misused (helping to avoid the case where
				// a ValueTask is illegally re-awaited and happens to do so at exactly 2^16 uses later on this exact same instance),
				// at the expense of potentially incurring an additional allocation every 65K uses.
				if ((ushort)_valueTaskSource.Version == ushort.MaxValue)
				{
					return;
				}

				_owner.NodePrivate_TryReturnNode(this);
			}

		}

		#endregion

		#region queue

		// may not be used to store some queue info cause may be used with different queues
		private ref struct DequeueUserState
		{
			public bool ReadersOnly { get; set; }
			public bool HasNextWriter => _hasNextWriter;

			public Node? DequeuedNode => _dequeued;
			public Node? RemovedHeadNode => _removedHead;

			public bool HasNode => _dequeued != null;

			internal bool _hasNextWriter;
			internal Node? _dequeued;
			internal Node? _removedHead;
		}

		// lock free queue based on Michael/Scott algorithm.
		// links:
		// https://habr.com/ru/post/219201/, 
		// http://www.cs.rochester.edu/~scott/papers/1996_PODC_queues.pdf,
		// https://neerc.ifmo.ru/wiki/index.php?title=%D0%9E%D1%87%D0%B5%D1%80%D0%B5%D0%B4%D1%8C_%D0%9C%D0%B0%D0%B9%D0%BA%D0%BB%D0%B0_%D0%B8_%D0%A1%D0%BA%D0%BE%D1%82%D1%82%D0%B0
		private struct LockFreeQueue
		{
			private Node _head;
			private Node _tail;

			public bool IsDefault => _head == null;

			public LockFreeQueue(Node sentinelHead)
			{
				_head = _tail = sentinelHead;
			}

			public void Enqueue(Node newNode, ref SpinWait spinWait)
			{
				while (!TryEnqueueIteration(newNode))
				{
					spinWait.SpinOnce();
				}
			}

			public bool TryEnqueueIteration(Node newNode)
			{
				var oldTail = Volatile.Read(ref _tail);
				var oldTailNext = Volatile.Read(ref oldTail._qNext);

				// алгоритм допускает, 
				// что _tail может указывать не на хвост,
				// и надеется, что следующие вызовы настроят 
				// хвост правильно. Типичный пример многопоточной взаимопомощи				  
				if (oldTailNext != null)
				{
					// Упс! Требуется зачистить хвост 
					// (в прямом смысле) за другим потоком
					Cas(ref _tail!, oldTail, oldTailNext);


					// Нужно начинать все сначала, даже если CAS не удачен
					// CAS неудачен — значит, _tail кто-то 
					// успел поменять после того, как мы его прочитали.
					return false;
				}

				// old tail actually can be already removed from queue
				if (Cas(ref oldTail._qNext, null, newNode))
				{
					// Успешно добавили новый элемент в очередь.

					// Наконец, пытаемся изменить сам хвост _tail.
					// Нас здесь не интересует, получится у нас или нет, — если нет, 
					// за нами почистят, см. 'Упс!' выше и ниже, в dequeue				
					Cas(ref _tail!, oldTail, newNode);

					return true;
				}

				// У нас ничего не получилось — CAS не сработал.
				// Это значит, что кто-то успел влезть перед нами.
				// Обнаружена конкуренция — дабы не усугублять её, 
				// отступим на очень краткий момент времени
				return false;
			}


			public bool TryDequeue(ref DequeueUserState state, ref SpinWait spinWait)
			{
				while (!TryDequeueIteration(ref state))
				{
					spinWait.SpinOnce();
				}

				//Debug.Assert(!state.HasNode || state.RemovedHeadNode!._qNext == null);

				return state.HasNode;
			}

			/// <summary>
			/// If dequeue succeeded, oldHeadNext will be not null and oldHead may be disposed.
			/// oldHeadNext may not be disposed, it's still in the queue as a head sentinel.
			/// </summary>
			/// <param name="oldHeadNext"></param>
			/// <param name="oldHead"></param>
			/// <param name="readersOnly"></param>
			/// <returns></returns>
			public bool TryDequeueIteration(ref DequeueUserState state)
			{
				// !!! А вот это интересный момент!
				// Мы возвращаем элемент, следующий за [бывшей] головой
				// Заметим, что oldHeadNext все еще в очереди — 
				// это наша новая голова!
				ref var oldHead = ref state._removedHead;
				ref var oldHeadNext = ref state._dequeued;

				oldHead = Volatile.Read(ref _head);
				oldHeadNext = Volatile.Read(ref oldHead._qNext);

				// Проверяем: то, что мы только что прочли, 
				// осталось связанным?..
				if (Volatile.Read(ref _head) != oldHead)
				{
					// нет, - кто-то успел нам все испортить... 
					// Начинаем сначала
					return false;
				}

				// Признак пустоты очереди. 
				// Отметим, что в отличие от хвоста голова всегда 
				// находится на правильном месте					
				if (oldHeadNext == null)
				{
					return true;
				}

				if (state.ReadersOnly && oldHeadNext.IsWriter)
				{
					state._hasNextWriter = true;
					oldHeadNext = null;
					return true;
				}

				var oldTail = Volatile.Read(ref _tail);

				if (oldHead == oldTail)
				{
					// Упс! Хвост не на месте: есть голова,
					// есть следующий за головой элемент,
					// а хвост указывает на голову.
					// Требуется помочь нерадивым товарищам...		    
					Cas(ref _tail!, oldTail, oldHeadNext);

					// После помощи все приходится начинать заново - 
					// поэтому нам не важен результат CAS
					return false;
				}

				// Самый важный момент — линкуем новую голову
				// То есть продвигаемся по списку ниже
				if (Cas(ref _head!, oldHead, oldHeadNext))
				{
					// disconnecting node to allow independent GC
					// todo: can not disconnect cause if in some enqueue this node is obsoleted tail, it will be used
					//Volatile.Write(ref oldHead._qNext, null);
					//oldHead._qNext = null;

#if DEBUG
					Debug.Assert(Interlocked.Exchange(ref oldHead._removedFromQueue, 1) == 0);
#endif


					// Удалось — завершаем наш бесконечный цикл
					return true;
				}

				// Не удалось... Значит, кто-то нам помешал. 
				// Чтобы не мешать другим, отвлечемся на мгновение
				return false;
			}

			private static bool Cas(ref Node? location, Node? comparand, Node? newValue)
			{
				// non generic version of Interlocked.CompareExchange to avoid problems with AOT compilation?
				return ReferenceEquals(comparand, Interlocked.CompareExchange(ref location, newValue, comparand));
			}
		}

		#endregion
	}
}
