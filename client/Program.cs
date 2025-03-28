using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LibData;

public class ClientUDP
{
    // Configuration properties (populated from Settings.json)
    private string serverIP;
    private int serverPort;
    private string clientIP;
    private int clientPort;

    // UDP socket for communication
    private Socket socket;
    // JSON serialization options (to handle enum as string)
    private JsonSerializerOptions jsonOptions;

    public ClientUDP()
    {
        // Set up JSON options to serialize enums as strings for readability
        jsonOptions = new JsonSerializerOptions { WriteIndented = false };
        jsonOptions.Converters.Add(new JsonStringEnumConverter());  // MsgType enum as string
    }

    public static void Main()
    {
        ClientUDP client = new ClientUDP();
        client.Start();
    }

    /// <summary>
    /// Start the client: load configuration, set up socket, perform handshake and DNS lookups.
    /// </summary>
    public void Start()
    {
        try
        {
            LoadConfiguration();        // Read server/client settings from Settings.json
            InitializeSocket();         // Create and bind UDP socket

            PerformHandshake();         // Exchange Hello/Welcome with server

            PerformDNSLookups();        // Send multiple DNSLookup queries and handle replies/errors

            WaitForEndAndTerminate();   // Wait for End message from server and then close
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Client error: {ex.Message}");
        }
        finally
        {
            socket?.Close();  // Ensure socket is closed on exit
        }
    }

    /// <summary>
    /// Load server and client settings from Settings.json.
    /// </summary>
    private void LoadConfiguration()
    {
        string baseDir = AppContext.BaseDirectory;
        string settingsPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Setting.json");
        if (!System.IO.File.Exists(settingsPath))
        {
            throw new Exception($"Settings.json not found at {settingsPath}");
        }

        string json = System.IO.File.ReadAllText(settingsPath);
        // Define a simple Settings structure to parse JSON
        var settings = JsonSerializer.Deserialize<Settings>(json, jsonOptions);
        if (settings == null)
            throw new Exception("Failed to parse Settings.json");

        serverIP   = settings.ServerIP;
        serverPort = settings.ServerPort;
        clientIP   = string.IsNullOrEmpty(settings.ClientIP) ? "0.0.0.0" : settings.ClientIP;
        clientPort = settings.ClientPort;
    }

    /// <summary>
    /// Initialize the UDP socket and bind to the client IP/port (if specified).
    /// </summary>
    private void InitializeSocket()
    {
        // Create a UDP socket (IPv4)
        socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

        // Bind to client IP/port if specified (if clientPort is 0, an ephemeral port will be chosen)
        IPEndPoint clientEndPoint = new IPEndPoint(IPAddress.Parse(clientIP), clientPort);
        socket.Bind(clientEndPoint);

        Console.WriteLine($"Client: Socket initialized on {(socket.LocalEndPoint as IPEndPoint)?.ToString() ?? "unknown endpoint"}");
    }

    /// <summary>
    /// Perform the initial handshake: send Hello and receive Welcome.
    /// </summary>
    private void PerformHandshake()
    {
        // Construct Hello message
        Message helloMsg = new Message
        {
            MsgId = new Random().Next(1, 10000),    // random message ID
            MsgType = MessageType.Hello,
            Content = "Hello from client"
        };

        // Serialize Hello message to JSON and send to server
        byte[] helloData = JsonSerializer.SerializeToUtf8Bytes(helloMsg, jsonOptions);
        IPEndPoint serverEndPoint = new IPEndPoint(IPAddress.Parse(serverIP), serverPort);
        socket.SendTo(helloData, serverEndPoint);
        Console.WriteLine($"Client: Sent Hello (MsgId={helloMsg.MsgId}) to server.");

        // Receive Welcome response
        byte[] buffer = new byte[1024];
        EndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);
        int receivedBytes = socket.ReceiveFrom(buffer, ref remoteEP);
        string responseJson = Encoding.UTF8.GetString(buffer, 0, receivedBytes);
        // Parse the response JSON
        Message? responseMsg = JsonSerializer.Deserialize<Message>(responseJson, jsonOptions);
        if (responseMsg == null || responseMsg.MsgType != MessageType.Welcome)
        {
            throw new Exception("Protocol error: expected Welcome message from server.");
        }
        string welcomeText = responseMsg.Content != null ? ((JsonElement)responseMsg.Content).GetString() ?? "" : "";
        Console.WriteLine($"Client: Received Welcome (MsgId={responseMsg.MsgId}) with content: \"{welcomeText}\"");
    }

    /// <summary>
    /// Send multiple DNSLookup requests (at least 4) and handle the replies/errors.
    /// </summary>
    private void PerformDNSLookups()
    {
        // Prepare a list of DNS lookup queries (two valid and two invalid examples):
        var queries = new object[]
        {
            // Valid DNS lookup #1: e.g., Type A record for "www.example.com"
            new DNSRecord { Type = "A", Name = "www.example.com" },
            // Valid DNS lookup #2: e.g., Type MX record for "example.com"
            new DNSRecord { Type = "MX", Name = "example.com" },
            // Invalid DNS lookup #3: unknown domain (to trigger "Domain not found")
            "unknown.domain",  // content as string without type (assumes missing record)
            // Invalid DNS lookup #4: malformed request (missing Name field)
            new { Type = "A", Value = "www.example.com" }  // wrong content structure
        };

        Random rng = new Random();
        IPEndPoint serverEndPoint = new IPEndPoint(IPAddress.Parse(serverIP), serverPort);
        byte[] buffer = new byte[2048];
        EndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);

        foreach (var queryContent in queries)
        {
            // Construct DNSLookup message with a random MsgId
            Message dnsLookupMsg = new Message
            {
                MsgId = rng.Next(1000, 9999),
                MsgType = MessageType.DNSLookup,
                Content = queryContent
            };

            // Serialize and send the DNSLookup message
            byte[] queryData = JsonSerializer.SerializeToUtf8Bytes(dnsLookupMsg, jsonOptions);
            socket.SendTo(queryData, serverEndPoint);
            string queryDesc = DescribeLookupContent(queryContent);
            Console.WriteLine($"Client: Sent DNSLookup (MsgId={dnsLookupMsg.MsgId}) for {queryDesc}");

            // Receive response (DNSLookupReply or Error)
            remoteEP = new IPEndPoint(IPAddress.Any, 0);
            int receivedBytes = socket.ReceiveFrom(buffer, ref remoteEP);
            string respJson = Encoding.UTF8.GetString(buffer, 0, receivedBytes);
            Message? respMsg = JsonSerializer.Deserialize<Message>(respJson, jsonOptions);
            if (respMsg == null)
            {
                Console.WriteLine("Client: Received unrecognized response (not valid JSON).");
                continue;
            }

            // Handle response based on message type
            if (respMsg.MsgType == MessageType.DNSLookupReply)
            {
                // Successful lookup – content should be a DNSRecord
                if (respMsg.Content == null) throw new Exception("Received DNSLookupReply with null content");
                JsonElement contentElem = (JsonElement)respMsg.Content;
                DNSRecord? dnsRecord = contentElem.Deserialize<DNSRecord>(jsonOptions);
                if (dnsRecord == null)
                {
                    Console.WriteLine("Client: Received DNSLookupReply with invalid DNSRecord content.");
                }
                else
                {
                    // Print the DNS record details
                    Console.WriteLine($"Client: Received DNSLookupReply (MsgId={respMsg.MsgId}) => " +
                        $"{dnsRecord.Type} record for {dnsRecord.Name}: Value={dnsRecord.Value}, TTL={dnsRecord.TTL}" +
                        $"{(dnsRecord.Priority.HasValue ? $", Priority={dnsRecord.Priority}" : "")}");
                }
                // Send Ack for the received DNSLookupReply (content is the original query MsgId)
                Message ackMsg = new Message
                {
                    MsgId = rng.Next(10000, 99999),
                    MsgType = MessageType.Ack,
                    Content = respMsg.MsgId  // acknowledge the query ID
                };
                byte[] ackData = JsonSerializer.SerializeToUtf8Bytes(ackMsg, jsonOptions);
                socket.SendTo(ackData, serverEndPoint);
                Console.WriteLine($"Client: Sent Ack (MsgId={ackMsg.MsgId}) for DNSLookup MsgId={respMsg.MsgId}");
            }
            else if (respMsg.MsgType == MessageType.Error)
            {
                // Error response from server
                string errorText = respMsg.Content != null ? ((JsonElement)respMsg.Content).GetString() ?? "(no details)" : "(no content)";
                Console.WriteLine($"Client: Received Error (MsgId={respMsg.MsgId}) from server: \"{errorText}\"");
                // According to the protocol, do not send Ack for Error messages.
            }
            else
            {
                // Unexpected message type
                Console.WriteLine($"Client: Protocol error - unexpected response MsgType={respMsg.MsgType} (MsgId={respMsg.MsgId}).");
                // We can choose to break out or continue based on severity. Here, break out on serious error.
                break;
            }
        }
    }

    /// <summary>
    /// Wait for the End message from the server and close the client.
    /// </summary>
    private void WaitForEndAndTerminate()
    {
        // After sending all DNSLookup queries, expect an End message from server
        byte[] buffer = new byte[512];
        EndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);
        int receivedBytes = socket.ReceiveFrom(buffer, ref remoteEP);
        string endJson = Encoding.UTF8.GetString(buffer, 0, receivedBytes);
        Message? endMsg = JsonSerializer.Deserialize<Message>(endJson, jsonOptions);
        if (endMsg != null && endMsg.MsgType == MessageType.End)
        {
            string endText = endMsg.Content != null ? ((JsonElement)endMsg.Content).GetString() ?? "" : "";
            Console.WriteLine($"Client: Received End (MsgId={endMsg.MsgId}) with content: \"{endText}\"");
            Console.WriteLine("Client: Session ended. Closing socket.");
        }
        else
        {
            Console.WriteLine("Client: Did not receive expected End message. Closing socket.");
        }
        // Socket will be closed in finally block of Start().
    }

    /// <summary>
    /// Helper to produce a readable description of a DNSLookup content for logging.
    /// </summary>
    private string DescribeLookupContent(object content)
    {
        if (content is DNSRecord record)
        {
            return $"{record.Type} record for \"{record.Name}\"";
        }
        if (content is string name)
        {
            return $"\"{name}\" (no type specified)";
        }
        // For anonymous object or other types (malformed query content)
        try
        {
            string json = JsonSerializer.Serialize(content);
            return $"(malformed) {json}";
        }
        catch
        {
            return content.ToString() ?? "(unknown content)";
        }
    }

    // Internal class to map settings JSON structure
    private class Settings
    {
        public string ServerIP { get; set; } = "127.0.0.1";
        public int ServerPort { get; set; } = 9050;
        public string ClientIP { get; set; } = "127.0.0.1";
        public int ClientPort { get; set; } = 0;
    }
}
