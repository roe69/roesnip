using Xunit;

// FileLog (RoeSnip.Core.Diagnostics) is a process-wide static sink with a single shared
// _filePath field - correct in production (one process, one Initialize call at startup) but not
// safe under xUnit's default cross-class parallelism: FileLogTests points that shared static at
// its own temp directory and reads the file back mid-test, while an unrelated, concurrently
// running test elsewhere in this assembly can exercise ToneMapper (which calls FileLog.Write on
// every tonemap - see Color/ToneMapper.cs) and append an unrelated line into that same temp
// file out from under it. Observed as intermittent FileLogTests failures (wrong content / wrong
// rotation state) only when the full suite ran, never when FileLogTests ran alone. Disabling
// parallelization for this whole assembly removes the race without having to track every current
// or future FileLog call site individually - the suite is well under a second either way, so
// there is no meaningful cost to running it serially.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
