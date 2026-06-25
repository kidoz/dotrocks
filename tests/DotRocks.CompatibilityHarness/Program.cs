using System.Globalization;
using System.Net.Sockets;
using System.Text.Json;
using DotRocks.Data;
using DotRocks.Data.Loading;
using DotRocks.Data.Pooling;
using DotRocks.Data.Protocol.Framing;
using DotRocks.Data.Protocol.Handshake;
using DotRocks.Data.Protocol.Results;
using DotRocks.Data.Protocol.Serialization;

namespace DotRocks.CompatibilityHarness;

/// <summary>
/// Phase-Zero compatibility harness. Connects to a StarRocks FE query endpoint, reads the initial
/// handshake using the DotRocks framing and handshake layers, and records the observed protocol
/// version, capability flags, character set, status flags, and authentication plugin. The
/// authentication challenge bytes are never printed or persisted — only their length.
/// </summary>
internal static class Program
{
    private static readonly JsonSerializerOptions ReportJsonOptions = new()
    {
        WriteIndented = true,
    };

    private static async Task<int> Main(string[] args)
    {
        string host =
            GetArg(args, 0) ?? Environment.GetEnvironmentVariable("DOTROCKS_HOST") ?? "127.0.0.1";
        int port = ParseInt(
            GetArg(args, 1) ?? Environment.GetEnvironmentVariable("DOTROCKS_PORT"),
            9030
        );
        int timeoutSeconds = ParseInt(
            Environment.GetEnvironmentVariable("DOTROCKS_TIMEOUT_SECONDS"),
            10
        );
        string reportPath =
            Environment.GetEnvironmentVariable("DOTROCKS_REPORT") ?? "handshake-report.json";

        // Phase-Zero characterization for the binary prepared-statement protocol. Opt in with
        // `--prepare-probe` and a DOTROCKS_CONNECTION_STRING; it opens an authenticated physical
        // connection and reports the COM_STMT_PREPARE response (or the server error).
        if (Array.IndexOf(args, "--prepare-probe") >= 0)
        {
            return await ProbePrepareAsync(timeoutSeconds).ConfigureAwait(false);
        }

        await Console
            .Out.WriteLineAsync(
                $"DotRocks compatibility harness — probing StarRocks handshake at {host}:{port} (timeout {timeoutSeconds}s)"
            )
            .ConfigureAwait(false);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            using NetworkStream stream = client.GetStream();

            var reader = new PacketReader(stream);
            byte[] payload = await reader.ReadPayloadAsync(cts.Token).ConfigureAwait(false);
            ServerHandshake handshake = ServerHandshake.Parse(payload);

            HandshakeReport report = BuildReport(host, port, payload.Length, handshake);
            await PrintSummaryAsync(report).ConfigureAwait(false);
            await WriteReportAsync(reportPath, report, cts.Token).ConfigureAwait(false);
            await Console
                .Out.WriteLineAsync($"Wrote handshake report to {Path.GetFullPath(reportPath)}")
                .ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException)
        {
            await Console
                .Error.WriteLineAsync(
                    $"Timed out after {timeoutSeconds}s connecting to or reading from {host}:{port}."
                )
                .ConfigureAwait(false);
            return 2;
        }
        catch (SocketException ex)
        {
            await Console
                .Error.WriteLineAsync(
                    $"Could not connect to {host}:{port} ({ex.SocketErrorCode}). "
                        + "Is StarRocks running? Start it with `just starrocks-up`."
                )
                .ConfigureAwait(false);
            return 2;
        }
        catch (MalformedPacketException ex)
        {
            await Console
                .Error.WriteLineAsync($"The server bytes are not a valid handshake: {ex.Message}")
                .ConfigureAwait(false);
            return 3;
        }
        catch (IOException ex)
        {
            await Console
                .Error.WriteLineAsync($"I/O error talking to {host}:{port}: {ex.Message}")
                .ConfigureAwait(false);
            return 2;
        }
    }

    private static async Task<int> ProbePrepareAsync(int timeoutSeconds)
    {
        string? connectionString = Environment.GetEnvironmentVariable("DOTROCKS_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            await Console
                .Error.WriteLineAsync("Set DOTROCKS_CONNECTION_STRING for the prepare probe.")
                .ConfigureAwait(false);
            return 2;
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        DotRocksConnectionOptions options = DotRocksConnectionOptions.Parse(connectionString);
        using DotRocksPhysicalConnection connection = await DotRocksPhysicalConnection
            .OpenAsync(options, cts.Token)
            .ConfigureAwait(false);

        foreach (string sql in (string[])["SELECT 1 AS one", "SELECT ? + ? AS total"])
        {
            try
            {
                StatementPrepareResult result = await connection
                    .PrepareAsync(sql, cts.Token)
                    .ConfigureAwait(false);
                await Console
                    .Out.WriteLineAsync(
                        $"PREPARE OK '{sql}': statementId={result.StatementId}, params={result.ParameterCount}, columns={result.ColumnCount}"
                    )
                    .ConfigureAwait(false);
                await connection
                    .ClosePreparedStatementAsync(result.StatementId, cts.Token)
                    .ConfigureAwait(false);
            }
            catch (DotRocksException ex)
            {
                await Console
                    .Out.WriteLineAsync($"PREPARE FAILED '{sql}': {ex.Message}")
                    .ConfigureAwait(false);
                return 1;
            }
        }

        return 0;
    }

    private static HandshakeReport BuildReport(
        string host,
        int port,
        int rawPayloadLength,
        ServerHandshake handshake
    )
    {
        List<string> capabilities = [];
        foreach (CapabilityFlags flag in Enum.GetValues<CapabilityFlags>())
        {
            if (flag != CapabilityFlags.None && handshake.Capabilities.HasFlag(flag))
            {
                capabilities.Add(flag.ToString());
            }
        }

        return new HandshakeReport(
            host,
            port,
            DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            handshake.ProtocolVersion,
            handshake.ServerVersion,
            handshake.ConnectionId,
            handshake.CharacterSet,
            handshake.StatusFlags,
            handshake.AuthPluginName,
            handshake.AuthPluginData.Length,
            rawPayloadLength,
            capabilities
        );
    }

    private static async Task PrintSummaryAsync(HandshakeReport report)
    {
        await Console
            .Out.WriteLineAsync($"  protocol version : {report.ProtocolVersion}")
            .ConfigureAwait(false);
        await Console
            .Out.WriteLineAsync($"  server version   : {report.ServerVersion}")
            .ConfigureAwait(false);
        await Console
            .Out.WriteLineAsync($"  connection id    : {report.ConnectionId}")
            .ConfigureAwait(false);
        await Console
            .Out.WriteLineAsync($"  character set    : {report.CharacterSet}")
            .ConfigureAwait(false);
        await Console
            .Out.WriteLineAsync($"  status flags     : 0x{report.StatusFlags:X4}")
            .ConfigureAwait(false);
        await Console
            .Out.WriteLineAsync(
                $"  auth plugin      : {report.AuthPluginName ?? "(none advertised)"}"
            )
            .ConfigureAwait(false);
        await Console
            .Out.WriteLineAsync(
                $"  auth data length : {report.AuthPluginDataLength} byte(s) (redacted)"
            )
            .ConfigureAwait(false);
        await Console
            .Out.WriteLineAsync($"  capabilities     : {string.Join(", ", report.Capabilities)}")
            .ConfigureAwait(false);
    }

    private static async Task WriteReportAsync(
        string path,
        HandshakeReport report,
        CancellationToken cancellationToken
    )
    {
        string json = JsonSerializer.Serialize(report, ReportJsonOptions);
        await File.WriteAllTextAsync(path, json, cancellationToken).ConfigureAwait(false);
    }

    private static string? GetArg(string[] args, int index) =>
        index < args.Length ? args[index] : null;

    private static int ParseInt(string? value, int fallback) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : fallback;
}
