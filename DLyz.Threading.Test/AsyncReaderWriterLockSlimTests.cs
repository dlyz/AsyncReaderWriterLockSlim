using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Xunit.Sdk;

namespace DLyz.Threading.Test
{
	public class AsyncReaderWriterLockSlimTests
	{
		[Fact]
		public void BasicTest()
		{
			var l = new AsyncReaderWriterLockSlim();
			var vt1 = l.AcquireReaderLockAsync();
			var vt2 = l.AcquireReaderLockAsync();
			var vt3 = l.AcquireWriterLockAsync();

			Assert.True(vt1.IsCompletedSuccessfully);
			Assert.True(vt2.IsCompletedSuccessfully);
			Assert.False(vt3.IsCompleted);

			vt1.GetAwaiter().GetResult();
			vt2.GetAwaiter().GetResult();
			l.ReleaseReaderLock();
			l.ReleaseReaderLock();


			Assert.True(vt3.IsCompletedSuccessfully);

			vt1 = l.AcquireReaderLockAsync();
			vt2 = l.AcquireReaderLockAsync();
			Assert.False(vt1.IsCompleted);
			Assert.False(vt2.IsCompleted);

			vt3.GetAwaiter().GetResult();
			l.ReleaseWriterLock();

			Assert.True(vt1.IsCompletedSuccessfully);
			Assert.True(vt2.IsCompletedSuccessfully);
		}


		[Theory]
		[InlineData(false)]
		[InlineData(true)]
		public void BasicCancellationTest(bool runContinuationsAsynchronously)
		{
			var l = new AsyncReaderWriterLockSlim(new() { RunContinuationsAsynchronously = runContinuationsAsynchronously });
			var wt = l.AcquireWriterLockAsync();
			Assert.True(wt.IsCompletedSuccessfully);
			wt.GetAwaiter().GetResult();

			var cts = new CancellationTokenSource();
			var rt = l.AcquireReaderLockAsync(cts.Token);
			Assert.False(rt.IsCompleted);

			cts.Cancel();
			Assert.True(rt.IsCanceled);
			Assert.Throws<OperationCanceledException>(() => rt.GetAwaiter().GetResult());

			rt = l.AcquireReaderLockAsync();
			Assert.False(rt.IsCompleted);
			l.ReleaseWriterLock();
			Assert.True(rt.IsCompletedSuccessfully);
			rt.GetAwaiter().GetResult();
		}



		[Fact]
		public void OverflowReadersTest()
		{
			var l = new AsyncReaderWriterLockSlim();
			for (int i = 0; i < int.MaxValue - 1; i++)
			{
				var vt = l.AcquireReaderLockAsync();
				Assert.True(vt.IsCompletedSuccessfully);
			}

			Assert.Throws<InvalidOperationException>(() => l.AcquireReaderLockAsync());
		}

		// can not test that because it is required to allocate about 2^31 nodes in queue
		// and this is too many memory for a machine
		//[Fact]
		//public void OverflowReadersQueueTest()
		//{
		//	var l = new AsyncReaderWriterLockSlim();
		//	var wl = l.AcquireWriterLockAsync();
		//	Assert.True(wl.IsCompletedSuccessfully);

		//	for (int i = 0; i < int.MaxValue - 1; i++)
		//	{
		//		var vt = l.AcquireReaderLockAsync();
		//		Assert.True(!vt.IsCompleted);
		//	}

		//	Assert.Throws<InvalidOperationException>(() => l.AcquireReaderLockAsync());
		//}



		[Fact]
		public void ContinuationExceptionOnCancellationTest()
		{
			var l = new AsyncReaderWriterLockSlim(new() { RunContinuationsAsynchronously = false });
			var wt = l.AcquireWriterLockAsync();
			Assert.True(wt.IsCompletedSuccessfully);
			wt.GetAwaiter().GetResult();

			var cts = new CancellationTokenSource();
			var rt = l.AcquireReaderLockAsync(cts.Token);
			Assert.False(rt.IsCompleted);

			rt.ConfigureAwait(false).GetAwaiter().UnsafeOnCompleted(() => throw new TestException());

			var ex = Assert.Throws<AggregateException>(() => cts.Cancel());
			Assert.IsType<TestException>(ex.InnerException);


			rt = l.AcquireReaderLockAsync();
			Assert.False(rt.IsCompleted);
			l.ReleaseWriterLock();
			Assert.True(rt.IsCompletedSuccessfully);
			rt.GetAwaiter().GetResult();
		}


		[Fact]
		public void ContinuationExceptionTest()
		{
			var syncContext = new TestSyncContext();
			SynchronizationContext.SetSynchronizationContext(syncContext);

			var l = new AsyncReaderWriterLockSlim(new() { RunContinuationsAsynchronously = false });
			var wt = l.AcquireWriterLockAsync();
			Assert.True(wt.IsCompletedSuccessfully);
			wt.GetAwaiter().GetResult();

			var rt = l.AcquireReaderLockAsync();
			Assert.False(rt.IsCompleted);

			rt.ConfigureAwait(true).GetAwaiter().UnsafeOnCompleted(() => throw new TestException());

			l.ReleaseWriterLock();


			Assert.Null(syncContext.WaitForCompletionAsync().Result);
			Assert.NotNull(syncContext.Exception);
		}

		private class TestException : Exception
		{
		}


		private class TestSyncContext : AsyncTestSyncContext
		{
			public TestSyncContext()
				: base(null)
			{
			}

			public override void Post(SendOrPostCallback d, object state)
			{
				base.Post(WrapCallback(d, state), null);
			}

			public override void Send(SendOrPostCallback d, object state)
			{
				base.Send(WrapCallback(d, state), null);
			}

			private SendOrPostCallback WrapCallback(SendOrPostCallback d, object state)
			{
				return _ =>
				{
					try
					{
						d(state);
					}
					catch (TestException ex)
					{
						Exception = ex;
					}
				};
			}

			public TestException? Exception { get; private set; }

		}
	}
}
