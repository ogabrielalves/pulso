using Xunit;

// Loc and Settings are process-wide statics, so test classes must not run in parallel.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
