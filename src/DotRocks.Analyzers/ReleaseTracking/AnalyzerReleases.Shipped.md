## Release 1.0.0

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|------
DTR0001 | Security | Warning | Avoid insecure Stream Load HTTP endpoints with credentials.
DTR0002 | Usage | Warning | Configure EF writable keys with ValueGeneratedNever().
DTR0003 | Usage | Warning | Avoid unsupported EF binary and varbinary mappings.
DTR0004 | Usage | Warning | Avoid completing a transaction variable more than once.
DTR0005 | Usage | Warning | Avoid unsupported EF EnsureCreated and EnsureDeleted APIs.
DTR0006 | Usage | Warning | Avoid unsupported EF ExecuteUpdate and ExecuteDelete APIs.
DTR0007 | Usage | Warning | Avoid range changes followed by one EF SaveChanges call.

## Release 1.0.1

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|------
DTR0008 | Usage | Warning | Avoid composite primary keys; DotRocks requires a single-column key.

## Release 1.2.0

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|------
DTR0009 | Security | Warning | Avoid building DotRocks CommandText with string concatenation or interpolation.
DTR0010 | Usage | Warning | Pass an available CancellationToken to async DotRocks calls.
DTR0011 | Usage | Warning | Avoid blocking on async DotRocks operations with .Result/.Wait()/.GetAwaiter().GetResult().
DTR0012 | Security | Warning | Avoid embedding a literal password in a DotRocks connection string.
