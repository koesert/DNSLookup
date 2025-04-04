using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LibData;

public class ServerUDP
{
    private string serverIP;
    private int serverPort;
    private List<DNSRecord> dnsRecords = new List<DNSRecord>();
    private Socket socket;
    private Random rand = new Random();
    private JsonSerializerOptions jsonOptions;

    public ServerUDP()
    {
        // Configure JSON options for serialization/deserialization
        jsonOptions = new JsonSerializerOptions();
        jsonOptions.Converters.Add(new JsonStringEnumConverter()); // enums as strings
        jsonOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull; // omit null fields
    }

    public static void Main()
    {
        ServerUDP server = new ServerUDP();
        server.Start();
    }

    public void Start()
    {
        try
        {
            Console.WriteLine("Server: Loading configuration...");
            string baseDir = AppContext.BaseDirectory;
            string settingsPath = Path.Combine(baseDir, "../", "../", "../", "../", "Setting.json");
            string settingsJson = File.ReadAllText(settingsPath);
            var settings = JsonSerializer.Deserialize<Settings>(settingsJson, jsonOptions);
            if (settings == null)
            {
                throw new Exception("Unable to load settings.");
            }
            serverIP = string.IsNullOrEmpty(settings.ServerIP) ? "0.0.0.0" : settings.ServerIP;
            serverPort = settings.ServerPort;
            Console.WriteLine($"Server: Config -> IP={serverIP}, Port={serverPort}");

            Console.WriteLine("Server: Loading DNS records from dnsrecords.json...");
            string dnsPath = Path.Combine(baseDir, "../", "../", "../", "dnsrecords.json");
            string dnsJson = File.ReadAllText(dnsPath);
            dnsRecords = JsonSerializer.Deserialize<List<DNSRecord>>(dnsJson, jsonOptions) ?? new List<DNSRecord>();
            Console.WriteLine($"Server: Loaded {dnsRecords.Count} DNS records.");

            // Initialize UDP socket and bind to server endpoint
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            IPAddress ipAddress = serverIP == "0.0.0.0" ? IPAddress.Any : IPAddress.Parse(serverIP);
            IPEndPoint localEP = new IPEndPoint(ipAddress, serverPort);
            socket.Bind(localEP);
            Console.WriteLine($"Server: UDP socket bound to {localEP.Address}:{localEP.Port}");
            Console.WriteLine("Server: Waiting for clients...");

            EndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);
            IPEndPoint? currentClientEP = null;
            bool sessionActive = false;
            int queriesHandled = 0;
            int expectedQueries = 4; // expecting at least 4 queries per client session

            byte[] buffer = new byte[1024];
            // Main loop to handle clients and messages
            while (true)
            {
                try
                {
                    // Receive a UDP message (blocking)
                    int receivedBytes = socket.ReceiveFrom(buffer, ref remoteEP);
                    string receivedJson = Encoding.UTF8.GetString(buffer, 0, receivedBytes);
                    Console.WriteLine($"Server: Received from {remoteEP}: {receivedJson}");
                    Message? msg = JsonSerializer.Deserialize<Message>(receivedJson, jsonOptions);
                    if (msg == null)
                    {
                        Console.WriteLine("Server: Failed to parse message (ignored).");
                        continue;
                    }

                    if (!sessionActive)
                    {
                        // Expect a Hello message to start a new session
                        if (msg.MsgType == MessageType.Hello)
                        {
                            sessionActive = true;
                            currentClientEP = new IPEndPoint(((IPEndPoint)remoteEP).Address, ((IPEndPoint)remoteEP).Port);
                            queriesHandled = 0;
                            Console.WriteLine("Server: Hello received. -> Sending Welcome.");
                            Message welcomeMsg = new Message
                            {
                                MsgId = rand.Next(1000, 9999),
                                MsgType = MessageType.Welcome,
                                Content = "Welcome from server"
                            };
                            SendMessage(welcomeMsg, currentClientEP);
                        }
                        else
                        {
                            Console.WriteLine($"Server: Unexpected {msg.MsgType} before Hello (ignored).");
                        }
                        continue;
                    }

                    // Session is active: ensure message comes from the same client
                    IPEndPoint senderEP = (IPEndPoint)remoteEP;
                    if (currentClientEP == null ||
                        !senderEP.Address.Equals(currentClientEP.Address) ||
                        senderEP.Port != currentClientEP.Port)
                    {
                        Console.WriteLine($"Server: Message from unknown client {senderEP} ignored (session active with {currentClientEP}).");
                        continue;
                    }

                    // Handle message types within an active session
                    switch (msg.MsgType)
                    {
                        case MessageType.DNSLookup:
                            queriesHandled++;
                            string? contentInfo = msg.Content != null ? msg.Content.ToString() : "(null)";
                            Console.WriteLine($"Server: DNSLookup #{queriesHandled} (MsgId={msg.MsgId}) Content={contentInfo}");
                            bool foundRecord = false;
                            if (msg.Content is string nameOnly)
                            {
                                // Missing type in content
                                Console.WriteLine("Server: DNSLookup content missing Type field.");
                                SendError(currentClientEP, "Domain not found");
                            }
                            else
                            {
                                try
                                {
                                    // Parse content as DNSRecord (expects Type and Name)
                                    var queryRec = JsonSerializer.Deserialize<DNSRecord>(msg.Content.ToString(), jsonOptions);
                                    if (queryRec == null || string.IsNullOrEmpty(queryRec.Name) || string.IsNullOrEmpty(queryRec.Type))
                                    {
                                        Console.WriteLine("Server: Incomplete DNSLookup content (Type/Name missing).");
                                        SendError(currentClientEP, "Domain not found");
                                    }
                                    else
                                    {
                                        // Look up the DNS record in our list
                                        DNSRecord? result = dnsRecords.Find(r =>
                                            r.Name.Equals(queryRec.Name, StringComparison.OrdinalIgnoreCase) &&
                                            r.Type.Equals(queryRec.Type, StringComparison.OrdinalIgnoreCase));
                                        if (result != null)
                                        {
                                            foundRecord = true;
                                            Console.WriteLine($"Server: Record found for {queryRec.Name} ({queryRec.Type}). -> Sending DNSLookupReply.");
                                            Message reply = new Message
                                            {
                                                MsgId = msg.MsgId, // same MsgId as request
                                                MsgType = MessageType.DNSLookupReply,
                                                Content = result
                                            };
                                            SendMessage(reply, currentClientEP);
                                        }
                                        else
                                        {
                                            Console.WriteLine($"Server: No record for {queryRec.Name} ({queryRec.Type}). -> Sending Error.");
                                            SendError(currentClientEP, "Domain not found");
                                        }
                                    }
                                }
                                catch (Exception e)
                                {
                                    Console.WriteLine("Server: Error processing DNSLookup content -> " + e.Message);
                                    SendError(currentClientEP, "Domain not found");
                                }
                            }
                            // If this was the last expected query, decide on ending the session
                            if (queriesHandled >= expectedQueries)
                            {
                                if (!foundRecord)
                                {
                                    // Last query resulted in an error (no Ack expected), end session now
                                    Console.WriteLine("Server: Final query was invalid. Sending End to client.");
                                    Message endMsg = new Message
                                    {
                                        MsgId = rand.Next(10000, 99999),
                                        MsgType = MessageType.End,
                                        Content = "End of DNSLookup"
                                    };
                                    SendMessage(endMsg, currentClientEP);
                                    // Reset session for next client
                                    sessionActive = false;
                                    currentClientEP = null;
                                    queriesHandled = 0;
                                    Console.WriteLine("Server: Session closed (after error). Waiting for new client...");
                                }
                                // If foundRecord is true, wait for final Ack to send End
                            }
                            break;

                        case MessageType.Ack:
                            Console.WriteLine($"Server: Ack received for MsgId {msg.Content}.");
                            if (queriesHandled >= expectedQueries)
                            {
                                // All queries processed and last one was acknowledged
                                Console.WriteLine("Server: All queries acknowledged. Sending End to client.");
                                Message endMsg = new Message
                                {
                                    MsgId = rand.Next(10000, 99999),
                                    MsgType = MessageType.End,
                                    Content = "End of DNSLookup"
                                };
                                SendMessage(endMsg, currentClientEP);
                                // Reset for next client
                                sessionActive = false;
                                currentClientEP = null;
                                queriesHandled = 0;
                                Console.WriteLine("Server: Session closed. Ready for next client.");
                            }
                            break;

                        case MessageType.Hello:
                            Console.WriteLine("Server: Hello received during active session (ignored).");
                            break;
                        case MessageType.Welcome:
                            Console.WriteLine("Server: Unexpected Welcome from client (ignored).");
                            break;
                        case MessageType.DNSLookupReply:
                            Console.WriteLine("Server: Unexpected DNSLookupReply from client (ignored).");
                            break;
                        case MessageType.End:
                            Console.WriteLine("Server: 'End' received from client. Closing session.");
                            sessionActive = false;
                            currentClientEP = null;
                            queriesHandled = 0;
                            break;
                        case MessageType.Error:
                            Console.WriteLine("Server: Error message received from client (ignored).");
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Server: Exception in communication loop: " + ex.Message);
                    // Reset current session on error and continue
                    sessionActive = false;
                    currentClientEP = null;
                    queriesHandled = 0;
                    Console.WriteLine("Server: Session reset due to error. Awaiting new client...");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("Server: Failed to start - " + ex.Message);
        }
        finally
        {
            if (socket != null)
            {
                socket.Close();
            }
        }
    }

    // Helper to send a Message via UDP
    private void SendMessage(Message message, EndPoint endpoint)
    {
        string json = JsonSerializer.Serialize(message, jsonOptions);
        byte[] data = Encoding.UTF8.GetBytes(json);
        socket.SendTo(data, endpoint);
        Console.WriteLine($"Server: Sent {message.MsgType} to {endpoint}: {json}");
    }

    // Helper to send an Error message
    private void SendError(EndPoint endpoint, string errorContent)
    {
        Message errorMsg = new Message
        {
            MsgId = rand.Next(1000, 999999),
            MsgType = MessageType.Error,
            Content = errorContent
        };
        SendMessage(errorMsg, endpoint);
    }

    // Class for settings JSON deserialization
    private class Settings
    {
        public required string ServerIP { get; set; }
        public int ServerPort { get; set; }
    }
}
