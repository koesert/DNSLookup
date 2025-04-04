using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LibData;

public class ClientUDP
{
    private string serverIP;
    private int serverPort;
    private string clientIP;
    private int clientPort;
    private Socket socket;
    private Random rand = new Random();
    private JsonSerializerOptions jsonOptions;

    public ClientUDP()
    {
        // Configure JSON options (enums as strings, ignore nulls)
        jsonOptions = new JsonSerializerOptions();
        jsonOptions.Converters.Add(new JsonStringEnumConverter());
        jsonOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    }

    public static void Main()
    {
        ClientUDP client = new ClientUDP();
        client.Start();
    }

    public void Start()
    {
        try
        {
            Console.WriteLine("Client: Loading configuration...");
            string baseDir = AppContext.BaseDirectory;
            string settingsPath = Path.Combine(baseDir,"../", "../", "../", "../", "Setting.json");
            string settingsJson = File.ReadAllText(settingsPath);
            var settings = JsonSerializer.Deserialize<Settings>(settingsJson, jsonOptions);
            if (settings == null)
            {
                throw new Exception("Unable to load settings.");
            }
            serverIP = settings.ServerIP ?? throw new Exception("ServerIP cannot be null in settings.");
            serverPort = settings.ServerPort;
            clientIP = string.IsNullOrEmpty(settings.ClientIP) ? "0.0.0.0" : settings.ClientIP;
            clientPort = settings.ClientPort;
            Console.WriteLine($"Client: Config -> Server={serverIP}:{serverPort}, ClientPort={clientPort}");

            Console.WriteLine("Client: Initializing UDP socket...");
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            if (clientPort > 0)
            {
                // Bind client socket to specified IP and port (if provided)
                IPAddress bindAddress = clientIP == "0.0.0.0" ? IPAddress.Any : IPAddress.Parse(clientIP);
                IPEndPoint clientEndPoint = new IPEndPoint(bindAddress, clientPort);
                socket.Bind(clientEndPoint);
                Console.WriteLine($"Client: Socket bound to {clientEndPoint.Address}:{clientEndPoint.Port}");
            }
            else
            {
                Console.WriteLine("Client: Using ephemeral port (no client port specified).");
            }

            Console.WriteLine("Client: Loading DNS records from dnsrecords.json...");
            string dnsPath = Path.Combine(baseDir, "../", "../", "../", "../", "server/dnsrecords.json");
            string dnsJson = File.ReadAllText(dnsPath);
            List<DNSRecord> dnsRecords = JsonSerializer.Deserialize<List<DNSRecord>>(dnsJson, jsonOptions) ?? new List<DNSRecord>();
            Console.WriteLine($"Client: Loaded {dnsRecords.Count} DNS records.");

            // Prepare server endpoint for communication
            if (string.IsNullOrEmpty(serverIP))
                throw new ArgumentException("Server IP address cannot be null or empty");
            IPEndPoint serverEndPoint = new IPEndPoint(IPAddress.Parse(serverIP), serverPort);

            // 1. Handshake: Hello -> Welcome
            Message helloMsg = new Message
            {
                MsgId = rand.Next(1, 1000),
                MsgType = MessageType.Hello,
                Content = "Hello from client"
            };
            SendMessage(helloMsg, serverEndPoint);
            // Wait for Welcome response
            EndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);
            byte[] buffer = new byte[1024];
            int receivedBytes = socket.ReceiveFrom(buffer, ref remoteEP);
            string recvJson = Encoding.UTF8.GetString(buffer, 0, receivedBytes);
            Console.WriteLine($"Client: Received from server: {recvJson}");
            Message welcomeMsg = JsonSerializer.Deserialize<Message>(recvJson, jsonOptions)!;
            if (welcomeMsg == null || welcomeMsg.MsgType != MessageType.Welcome)
            {
                Console.WriteLine($"Client: Protocol error - expected Welcome, but got {welcomeMsg?.MsgType}. Exiting.");
                return;
            }
            Console.WriteLine($"Client: Welcome received. Content: {welcomeMsg.Content}");

            // 2. Prepare DNS lookup queries (at least 2 valid and 2 invalid)
            DNSRecord? validQuery1 = dnsRecords.Count > 0 ? dnsRecords[0] : null;
            DNSRecord? validQuery2 = null;
            if (validQuery1 != null)
            {
                // find another record with a different type if possible
                validQuery2 = dnsRecords.Find(r => !r.Type.Equals(validQuery1.Type, StringComparison.OrdinalIgnoreCase));
                if (validQuery2 == null && dnsRecords.Count > 1)
                    validQuery2 = dnsRecords[1];
            }
            if (validQuery1 == null) validQuery1 = new DNSRecord { Type = "A", Name = "example.com" };
            if (validQuery2 == null) validQuery2 = new DNSRecord { Type = "A", Name = "example.net" };

            var queries = new List<Message>();
            // Query 1 (Invalid): Unknown domain (name only, no type)
            queries.Add(new Message
            {
                MsgId = rand.Next(1000, 9999),
                MsgType = MessageType.DNSLookup,
                Content = "unknown.domain"
            });
            // Query 2 (Valid): DNS record with type & name (validQuery1)
            queries.Add(new Message
            {
                MsgId = rand.Next(1000, 9999),
                MsgType = MessageType.DNSLookup,
                Content = new DNSRecord { Type = validQuery1.Type, Name = validQuery1.Name }
            });
            // Query 3 (Invalid): Malformed content (Type provided, Name missing)
            queries.Add(new Message
            {
                MsgId = rand.Next(1000, 9999),
                MsgType = MessageType.DNSLookup,
                Content = new DNSRecord { Type = "A", Value = validQuery1.Name }
            });
            // Query 4 (Valid): Another valid DNS record (validQuery2)
            queries.Add(new Message
            {
                MsgId = rand.Next(1000, 9999),
                MsgType = MessageType.DNSLookup,
                Content = new DNSRecord { Type = validQuery2.Type, Name = validQuery2.Name }
            });

            Console.WriteLine("Client: Starting DNS lookup queries...");
            foreach (Message query in queries)
            {
                // Send DNSLookup query
                string? info = query.Content != null ? query.Content.ToString() : "(no content)";
                Console.WriteLine($"Client: Sending DNSLookup (MsgId={query.MsgId}, Content={info})");
                SendMessage(query, serverEndPoint);

                // Receive server response
                receivedBytes = socket.ReceiveFrom(buffer, ref remoteEP);
                recvJson = Encoding.UTF8.GetString(buffer, 0, receivedBytes);
                Console.WriteLine($"Client: Received from server: {recvJson}");
                Message? response = JsonSerializer.Deserialize<Message>(recvJson, jsonOptions);
                if (response == null)
                {
                    Console.WriteLine("Client: Failed to parse server response. Stopping.");
                    break;
                }
                if (response.MsgType == MessageType.DNSLookupReply)
                {
                    // Successful DNS lookup response
                    DNSRecord? record = null;
                    try
                    {
                        if (response.Content?.ToString() is string content)
                        {
                            record = JsonSerializer.Deserialize<DNSRecord>(content, jsonOptions);
                        }
                    }
                    catch { /* ignore parse errors */ }
                    if (record != null)
                    {
                        Console.WriteLine($"Client: DNSLookupReply (MsgId={response.MsgId}) -> Name={record.Name}, Type={record.Type}, Value={record.Value}, TTL={record.TTL}");
                    }
                    else
                    {
                        Console.WriteLine($"Client: DNSLookupReply received (MsgId={response.MsgId}).");
                    }
                    // Send Ack for this DNSLookupReply
                    Message ackMsg = new Message
                    {
                        MsgId = rand.Next(1000, 9999),
                        MsgType = MessageType.Ack,
                        Content = response.MsgId.ToString()
                    };
                    Console.WriteLine($"Client: Sending Ack for MsgId {response.MsgId}");
                    SendMessage(ackMsg, serverEndPoint);
                }
                else if (response.MsgType == MessageType.Error)
                {
                    // Error response (no Ack expected)
                    Console.WriteLine($"Client: Error received from server: {response.Content}");
                    // Do not send Ack for an Error
                }
                else if (response.MsgType == MessageType.End)
                {
                    // End received unexpectedly (server ended early)
                    Console.WriteLine("Client: 'End' message received unexpectedly. Terminating.");
                    return;
                }
                else
                {
                    // Unexpected message type
                    Console.WriteLine($"Client: Unexpected response ({response.MsgType}). Stopping.");
                    return;
                }
            }

            // 3. Wait for End message from server
            Console.WriteLine("Client: All queries done. Waiting for End message...");
            receivedBytes = socket.ReceiveFrom(buffer, ref remoteEP);
            recvJson = Encoding.UTF8.GetString(buffer, 0, receivedBytes);
            Console.WriteLine($"Client: Received from server: {recvJson}");
            Message? endMsg = JsonSerializer.Deserialize<Message>(recvJson, jsonOptions);
            if (endMsg != null && endMsg.MsgType == MessageType.End)
            {
                Console.WriteLine($"Client: End received. Content: {endMsg.Content}");
            }
            else
            {
                Console.WriteLine($"Client: Protocol error - expected End, but got {endMsg?.MsgType}");
            }
            Console.WriteLine("Client: UDP client finished. Exiting.");
        }
        catch (Exception ex)
        {
            Console.WriteLine("Client: Error - " + ex.Message);
        }
        finally
        {
            if (socket != null)
            {
                socket.Close();
            }
        }
    }

    // Helper to send a message to the server
    private void SendMessage(Message message, EndPoint endpoint)
    {
        string json = JsonSerializer.Serialize(message, jsonOptions);
        byte[] data = Encoding.UTF8.GetBytes(json);
        socket.SendTo(data, endpoint);
        Console.WriteLine($"Client: Sent {message.MsgType} to {((IPEndPoint)endpoint).Address}:{((IPEndPoint)endpoint).Port}: {json}");
    }

    // Class for settings JSON deserialization
    private class Settings
    {
        public string? ServerIP { get; set; }
        public int ServerPort { get; set; }
        public string? ClientIP { get; set; }
        public int ClientPort { get; set; }
    }
}
