// Several tests here prove what the driver strips from the child's environment, which means mutating this
// process's own environment — shared state that xUnit's default cross-class parallelism happily runs another
// test against. That produced a genuine intermittent failure (all three scrub cases reporting an empty child
// environment in one full run, green in the next five), so the assembly runs its collections one at a time.
// Cheap here: the whole suite is a second, and the fakes do no real I/O.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
