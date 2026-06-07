// These integration tests share one in-process server, one SQLite database, and a broker with
// a single completion slot, so they must run sequentially — never in parallel.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
