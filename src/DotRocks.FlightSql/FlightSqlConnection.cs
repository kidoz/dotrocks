using System.Text;
using Apache.Arrow.Flight.Client;
using Apache.Arrow.Flight.Sql;
using Apache.Arrow.Flight.Sql.Client;
using Grpc.Core;
using Grpc.Net.Client;

namespace DotRocks.FlightSql;

internal sealed class FlightSqlConnection : IDisposable
{
    private readonly GrpcChannel _channel;
    private readonly string _authorizationValue;

    public FlightSqlConnection(Uri address, string userName, string password)
    {
        _channel = GrpcChannel.ForAddress(address);
        Client = new FlightClient(_channel);
        SqlClient = new FlightSqlClient(Client);
        _authorizationValue = CreateBasicAuthorizationValue(userName, password);
    }

    public FlightClient Client { get; }

    public FlightSqlClient SqlClient { get; }

    public Metadata CreateHeaders() => new() { { "authorization", _authorizationValue } };

    public void Dispose() => _channel.Dispose();

    internal static string CreateBasicAuthorizationValue(string userName, string password)
    {
        string credentials = Convert.ToBase64String(
            Encoding.UTF8.GetBytes(userName + ":" + password)
        );
        return "Basic " + credentials;
    }
}
