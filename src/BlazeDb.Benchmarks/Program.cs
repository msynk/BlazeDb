using BenchmarkDotNet.Running;

BenchmarkSwitcher.FromAssembly(typeof(BlazeDb.Benchmarks.ReadBenchmarks).Assembly).Run(args);
