### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|------
DTR0005 | Usage | Warning | Avoid unsupported EF EnsureCreated and EnsureDeleted APIs.
DTR0006 | Usage | Warning | Avoid unsupported EF ExecuteUpdate and ExecuteDelete APIs.
DTR0007 | Usage | Warning | Avoid range changes followed by one EF SaveChanges call.
DTR0008 | Usage | Warning | Avoid composite primary keys; DotRocks requires a single-column key.
