using BenchmarkDotNet.Running;
using System;

namespace DLyz.Threading.Benchmark
{
	class Program
	{
		static void Main(string[] args)
		{
			BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
			return;

#pragma warning disable CS0162 // Unreachable code detected
			var rnd = new Random();
			for (int i = 0; i < 1000000; i++)
			{
				var r = rnd.NextDouble();
				var p = r < 0.33 ? rnd.NextDouble() * 0.01 : r < 0.66 ? rnd.NextDouble() * 0.1 : rnd.NextDouble();
				RWLockBenchmark.RunManual(p);
			}
#pragma warning restore CS0162 // Unreachable code detected
		}
	}
}
