using System.Globalization;
using System.Net.Sockets;
using System.Text.Json;
using DotRocks.Data.Protocol.Framing;
using DotRocks.Data.Protocol.Handshake;
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

        Console.WriteLine(
            $"DotRocks compatibility harness — probing StarRocks handshake at {host}:{port} (timeout {timeoutSeconds}s)"
        );

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, cts.Token);
            await using NetworkStream stream = client.GetStream();

            var reader = new PacketReader(stream);
            byte[] payload = await reader.ReadPayloadAsync(cts.Token);
            ServerHandshake handshake = ServerHandshake.Parse(payload);

            HandshakeReport report = BuildReport(host, port, payload.Length, handshake);
            PrintSummary(report);
            await WriteReportAsync(reportPath, report, cts.Token);
            Console.WriteLine($"Wrote handshake report to {Path.GetFullPath(reportPath)}");
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine(
                $"Timed out after {timeoutSeconds}s connecting to or reading from {host}:{port}."
            );
            return 2;
        }
        catch (SocketException ex)
        {
            Console.Error.WriteLine(
                $"Could not connect to {host}:{port} ({ex.SocketErrorCode}). "
                    + "Is StarRocks running? Start it with `just starrocks-up`."
            );
            return 2;
        }
        catch (MalformedPacketException ex)
        {
            Console.Error.WriteLine($"The server bytes are not a valid handshake: {ex.Message}");
            return 3;
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"I/O error talking to {host}:{port}: {ex.Message}");
            return 2;
        }
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

    private static void PrintSummary(HandshakeReport report)
    {
        Console.WriteLine($"  protocol version : {report.ProtocolVersion}");
        Console.WriteLine($"  server version   : {report.ServerVersion}");
        Console.WriteLine($"  connection id    : {report.ConnectionId}");
        Console.WriteLine($"  character set    : {report.CharacterSet}");
        Console.WriteLine($"  status flags     : 0x{report.StatusFlags:X4}");
        Console.WriteLine($"  auth plugin      : {report.AuthPluginName ?? "(none advertised)"}");
        Console.WriteLine($"  auth data length : {report.AuthPluginDataLength} byte(s) (redacted)");
        Console.WriteLine($"  capabilities     : {string.Join(", ", report.Capabilities)}");
    }

    private static async Task WriteReportAsync(
        string path,
        HandshakeReport report,
        CancellationToken cancellationToken
    )
    {
        string json = JsonSerializer.Serialize(report, ReportJsonOptions);
        await File.WriteAllTextAsync(path, json, cancellationToken);
    }

    private static string? GetArg(string[] args, int index) =>
        index < args.Length ? args[index] : null;

    private static int ParseInt(string? value, int fallback) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : fallback;
}
