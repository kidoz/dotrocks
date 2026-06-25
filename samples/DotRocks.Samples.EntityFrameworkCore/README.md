# DotRocks EF Core Sample

Run with a StarRocks 4.0.7+ server and a database selected in the connection string:

```bash
DOTROCKS_CONNECTION_STRING="Server=127.0.0.1;Port=9030;User ID=root;Database=dotrocks_sample" \
dotnet run --project samples/DotRocks.Samples.EntityFrameworkCore
```

The sample demonstrates:

- `UseStarRocks(...)`.
- `ServerVersion(...)` pinned in EF Core provider options.
- A supported writable entity with a single explicit primary key and `ValueGeneratedNever()`.
- StarRocks table-shape configuration for key model, hash distribution, and replication.
- `SaveChangesAsync` insert, update, and delete.
- Minimal `MigrateAsync()` usage with a hand-authored `CREATE TABLE` migration.

Current unsupported EF behavior includes generated values, composite keys, navigations,
owned types, concurrency tokens, binary/varbinary mapping, idempotent migration scripts,
and migration schema mutations beyond conservative table creation/drop.
