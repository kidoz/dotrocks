using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using DotRocks.Data;
using DotRocks.Data.Protocol.Handshake;
using DotRocks.Data.Protocol.Results;
using DotRocks.Data.Protocol.Serialization;
using Xunit;

namespace DotRocks.Protocol.Tests.Serialization;

/// <summary>
/// Fuzz and regression coverage for the packet decoders: hostile or truncated server input must
/// fail with a controlled <see cref="MalformedPacketException"/> (or a sanitized
/// <see cref="DotRocksException"/>) and never an uncontrolled crash such as
/// <see cref="IndexOutOfRangeException"/> or <see cref="OverflowException"/>. New regression inputs
/// for any fixed parser defect should be appended to <see cref="RegressionCorpus"/>.
/// </summary>
public sealed class ProtocolFuzzTests
{
    // Deterministic: a fixed set of seeds so a failure is always reproducible from the seed.
    public static TheoryData<int> Seeds()
    {
        var data = new TheoryData<int>();
        for (int seed = 0; seed < 64; seed++)
        {
            data.Add(seed);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    [SuppressMessage(
        "Security",
        "CA5394:Do not use insecure randomness",
        Justification = "A seeded, reproducible PRNG is required for deterministic fuzzing; it is not security-sensitive."
    )]
    public void Decoders_DoNotCrashOnRandomInput(int seed)
    {
        var random = new Random(seed);
        for (int iteration = 0; iteration < 64; iteration++)
        {
            byte[] payload = new byte[random.Next(0, 96)];
            random.NextBytes(payload);
            AssertControlledFailure(payload);
        }
    }

    [Theory]
    [MemberData(nameof(RegressionCorpus))]
    public void Decoders_HandleRegressionCorpus(byte[] payload) => AssertControlledFailure(payload);

    public static TheoryData<byte[]> RegressionCorpus()
    {
        var data = new TheoryData<byte[]>();

        // Empty and single-byte payloads.
        data.Add([]);
        data.Add([0x00]);
        data.Add([0xFF]);

        // Length-encoded integer prefixes that promise more bytes than remain.
        data.Add([0xFC]); // 2-byte prefix, no value
        data.Add([0xFD, 0x01]); // 3-byte prefix, truncated
        data.Add([0xFE, 0x01, 0x02, 0x03]); // 8-byte prefix, truncated
        data.Add([0xFB]); // NULL marker alone

        // Length-encoded value claiming a huge length with no body.
        data.Add([0xFE, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]);

        // An error-shaped packet header (0xFF) with a truncated body.
        data.Add([0xFF, 0x10]);

        // An OK-shaped packet header (0x00) with truncated length-encoded fields.
        data.Add([0x00, 0xFC]);

        // A handshake-shaped payload (protocol version byte) that is otherwise truncated.
        data.Add([0x0A]);
        data.Add([0x0A, 0x35, 0x2E]);

        return data;
    }

    private static void AssertControlledFailure(byte[] payload)
    {
        RunParser(payload, static p => ServerHandshake.Parse(p));
        RunParser(payload, static p => ResultPacket.ReadOk(p));
        RunParser(payload, static p => ResultPacket.ReadError(p, connectionId: null));
        RunReaderSequence(payload);
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "The fuzz harness intentionally catches every exception to fail on any uncontrolled crash."
    )]
    private static void RunParser(byte[] payload, ParseAction parse)
    {
        try
        {
            parse(payload);
        }
        catch (MalformedPacketException)
        {
            // Expected: a bounds or shape violation surfaced as a controlled protocol error.
        }
        catch (DotRocksException)
        {
            // Expected: a sanitized server/driver-level error (for example an error packet).
        }
        catch (Exception exception)
        {
            Assert.Fail(
                $"Unexpected {exception.GetType().Name} decoding payload [{ToHex(payload)}]: {exception.Message}"
            );
        }
    }

    // Drives a randomized but bounds-safe sequence of ProtocolReader reads over the payload.
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "The fuzz harness intentionally catches every exception to fail on any uncontrolled crash."
    )]
    private static void RunReaderSequence(byte[] payload)
    {
        try
        {
            var reader = new ProtocolReader(payload);
            while (!reader.IsAtEnd)
            {
                int operation = payload[reader.Position] % 5;
                switch (operation)
                {
                    case 0:
                        reader.ReadByte();
                        break;
                    case 1:
                        reader.ReadLengthEncodedInteger(out _);
                        break;
                    case 2:
                        reader.ReadLengthEncodedString(Encoding.UTF8, out _);
                        break;
                    case 3:
                        reader.ReadFixedInteger(Math.Min(8, Math.Max(1, reader.Remaining)));
                        break;
                    default:
                        reader.ReadNullTerminatedString(Encoding.UTF8);
                        break;
                }
            }
        }
        catch (MalformedPacketException)
        {
            // Expected.
        }
        catch (Exception exception)
        {
            Assert.Fail(
                $"Unexpected {exception.GetType().Name} in ProtocolReader sequence over [{ToHex(payload)}]: {exception.Message}"
            );
        }
    }

    private static string ToHex(byte[] payload)
    {
        var builder = new StringBuilder(payload.Length * 2);
        foreach (byte value in payload)
        {
            builder.Append(value.ToString("X2", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    private delegate void ParseAction(ReadOnlySpan<byte> payload);
}
