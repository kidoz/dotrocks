### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|------
DTR0005 | Usage | Warning | Avoid unsupported EF EnsureCreated and EnsureDeleted APIs.
DTR0006 | Usage | Warning | Avoid unsupported EF ExecuteUpdate and ExecuteDelete APIs.
DTR0007 | Usage | Warning | Avoid range changes followed by one EF SaveChanges call.
DTR0008 | Usage | Warning | Avoid composite primary keys; DotRocks requires a single-column key.
DTR0009 | Security | Warning | Avoid building DotRocks CommandText with string concatenation or interpolation.
DTR0010 | Usage | Warning | Pass an available CancellationToken to async DotRocks calls.
DTR0011 | Usage | Warning | Avoid blocking on async DotRocks operations with .Result/.Wait()/.GetAwaiter().GetResult().
DTR0012 | Security | Warning | Avoid embedding a literal password in a DotRocks connection string.
