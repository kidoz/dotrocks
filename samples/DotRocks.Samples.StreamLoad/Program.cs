using System.Text;
using DotRocks.Data.Loading;

// Stream Load sample: upload CSV rows over HTTP without buffering the whole payload in memory.
// The payload here is a MemoryStream for brevity; in production pass any forward-only Stream
// (a file, a pipe, or a generator) so rows flow straight to StarRocks.
string connectionString =
    Environment.GetEnvironmentVariable("DOTROCKS_CONNECTION_STRING")
    ?? "Server=127.0.0.1;Port=9030;User ID=root;Stream Load Endpoint=https://127.0.0.1:8030";

using var client = new DotRocksStreamLoadClient(connectionString);

byte[] csv = Encoding.UTF8.GetBytes("1,login\n2,logout\n");
await using var payload = new MemoryStream(csv);

DotRocksStreamLoadResult result = await client.LoadCsvAsync(
    databaseName: "dotrocks_sample",
    tableName: "events",
    payload: payload,
    options: new DotRocksStreamLoadOptions { Columns = "id,event_name" }
);

Console.WriteLine(
    $"Loaded {result.NumberLoadedRows} rows (label {result.Label}, status {result.Status})."
);
