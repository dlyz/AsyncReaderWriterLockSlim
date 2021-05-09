#pragma warning disable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DLyz.Threading
{
	/// <summary>
	/// Reader/writer lock with writer priority:
	/// - as long as there is no writer, many readers can acquire lock;
	/// - as long as there are writers enqueued or acquired lock, new readers will be enqueued;
	/// 
	/// inspired by
	/// https://blogs.msdn.microsoft.com/pfxteam/2012/02/12/building-async-coordination-primitives-part-7-asyncreaderwriterlock/
	/// and
	/// https://github.com/StephenCleary/AsyncEx/blob/master/src/Nito.AsyncEx.Coordination/AsyncReaderWriterLock.cs
	/// </summary>
	public class OldAsyncReaderWriterLock
	{
		public OldAsyncReaderWriterLock(
			TaskCreationOptions writerCreationOptions = TaskCreationOptions.RunContinuationsAsynchronously,
			TaskCreationOptions readerCreationOptions = TaskCreationOptions.RunContinuationsAsynchronously
			)
		{
			_writerCreationOptions = writerCreationOptions;
			_readerCreationOptions = readerCreationOptions;
		}

		private readonly TaskCreationOptions _writerCreationOptions;
		private readonly TaskCreationOptions _readerCreationOptions;
		private readonly object _lock = new object();

		private bool CanUseSimplifiedReaderCall => _readerCreationOptions.HasFlag(TaskCreationOptions.RunContinuationsAsynchronously);


		/// <summary>
		/// 0 - free, -1 - locked by writer, > 0 - lockad by _status readers
		/// </summary>
		private int _status = 0;

		private int _waitingReaders = 0;

		private TaskCompletionSource<object> _basicReadersQueue = new TaskCompletionSource<object>();
		private readonly ItemQueue _readersQueue = new ItemQueue();
		private readonly ItemQueue _writersQueue = new ItemQueue();

		private static TaskContinuationOptions ToContiuationOptions(TaskCreationOptions creationOptions)
		{
			return TaskContinuationOptions.ExecuteSynchronously | (TaskContinuationOptions)creationOptions;
		}

		public Task<IDisposable> ReaderLockAsync(CancellationToken cancellationToken = default)
		{
			QueueItem item;
			lock (_lock)
			{
				if (_status >= 0 && _writersQueue.Count == 0)
				{
					++_status;
					return Task.FromResult<IDisposable>(new Releaser(this, isWriter: false));
				}
				else if (CanUseSimplifiedReaderCall && !cancellationToken.CanBeCanceled)
				{
					++_waitingReaders;
					return _basicReadersQueue.Task.ContinueWith<IDisposable>(
						t => new Releaser(this, isWriter: false),
						ToContiuationOptions(_readerCreationOptions)
						);
				}
				else
				{
					item = new QueueItem(this, false, _readerCreationOptions, cancellationToken);
					_readersQueue.Enqueue(item);
				}
			}

			item.SubscribeToCancellation();
			return item.Task;
		}

		public Task<IDisposable> WriterLockAsync(CancellationToken cancellationToken = default)
		{
			QueueItem item;
			lock (_lock)
			{
				if (_status == 0)
				{
					_status = -1;
					return Task.FromResult<IDisposable>(new Releaser(this, isWriter: true));
				}
				else
				{
					item = new QueueItem(this, true, _writerCreationOptions, cancellationToken);
					_writersQueue.Enqueue(item);
				}
			}

			item.SubscribeToCancellation();
			return item.Task;
		}

		private void ReleaseReader()
		{
			QueueItem writerToWake = null;
			lock (_lock)
			{
				--_status;
				if (_status == 0 && _writersQueue.Count > 0)
				{
					_status = -1;
					writerToWake = _writersQueue.Dequeue();
				}
			}
			writerToWake?.Activate();
		}

		private void ReleaseWriter()
		{
			QueueItem writerToWake = null;
			TaskCompletionSource<object> readerToWake = null;
			QueueItem[] readersToWake = null;
			lock (_lock)
			{
				if (_writersQueue.Count > 0)
				{
					_status = -1;
					writerToWake = _writersQueue.Dequeue();
				}
				else if (_waitingReaders + _readersQueue.Count > 0)
				{
					_status = _waitingReaders + _readersQueue.Count;

					if (_readersQueue.Count > 0)
					{
						readersToWake = _readersQueue.ExtractAll();
					}

					if (_waitingReaders > 0)
					{
						_waitingReaders = 0;
						readerToWake = _basicReadersQueue;
						_basicReadersQueue = new TaskCompletionSource<object>();
					}
				}
				else
				{
					_status = 0;
				}
			}

			if (writerToWake != null)
			{
				writerToWake.Activate();
			}
			else
			{
				readerToWake?.SetResult(null);
				if (readersToWake != null)
				{
					foreach (var item in readersToWake)
					{
						item.Activate();
					}
				}
			}
		}

		private void TryCancel(QueueItem item)
		{
			var queue = item.IsWriter ? _writersQueue : _readersQueue;

			QueueItem itemToCancel = null;
			lock (_lock)
			{
				if (queue.TryRemove(item))
				{
					itemToCancel = item;
				}
			}
			itemToCancel?.Cancel();
		}

		private class Releaser : IDisposable
		{
			protected OldAsyncReaderWriterLock _owner;

			public bool IsWriter { get; }

			public Releaser(
				OldAsyncReaderWriterLock owner,
				bool isWriter
				)
			{
				_owner = owner;
				IsWriter = isWriter;
			}

			public void Dispose()
			{
				var owner = Interlocked.Exchange(ref _owner, null);
				if (owner != null)
				{
					if (IsWriter)
					{
						owner.ReleaseWriter();
					}
					else
					{
						owner.ReleaseReader();
					}
				}
			}
		}

		private class QueueItem : Releaser
		{
			private readonly TaskCompletionSource<IDisposable> _tcs;
			private IDisposable _cancellationRegistration;
			private readonly CancellationToken _cancellationToken;

			private static readonly Action<object> _cacnelAction = Cancel;

			private static void Cancel(object state)
			{
				var item = (QueueItem)state;
				item._owner.TryCancel(item);
			}

			public QueueItem(
				OldAsyncReaderWriterLock owner,
				bool isWriter,
				TaskCreationOptions taskCreationOptions,
				CancellationToken cancellationToken
				)
				: base(owner, isWriter)
			{
				_tcs = new TaskCompletionSource<IDisposable>(taskCreationOptions);
				_cancellationToken = cancellationToken;
			}

			public void SubscribeToCancellation()
			{
				if (_cancellationToken.CanBeCanceled)
				{
					_cancellationRegistration = _cancellationToken.Register(_cacnelAction, this);
				}
			}

			public Task<IDisposable> Task => _tcs.Task;

			public void Activate()
			{
				_cancellationRegistration?.Dispose();
				_tcs.SetResult(this);
			}

			public void Cancel()
			{
				_tcs.TrySetCanceled(_cancellationToken);
			}
		}

		private sealed class ItemQueue
		{
			private readonly LinkedList<QueueItem> _queue = new LinkedList<QueueItem>();

			public int Count => _queue.Count;

			public bool IsEmpty => _queue.Count == 0;

			public void Enqueue(QueueItem value)
			{
				_queue.AddLast(value);
			}

			public QueueItem Dequeue()
			{
				var result = _queue.First.Value;
				_queue.RemoveFirst();
				return result;
			}

			public bool TryRemove(QueueItem item)
			{
				if (!IsEmpty)
				{
					for (var it = _queue.First; it != null; it = it.Next)
					{
						if (it.Value == item)
						{
							item = it.Value;
							_queue.Remove(it);
							return true;
						}
					}
				}
				item = default;
				return false;
			}

			public QueueItem[] ExtractAll()
			{
				var result = new QueueItem[Count];
				_queue.CopyTo(result, 0);
				_queue.Clear();
				return result;
			}
		}
	}
}
