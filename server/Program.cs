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
    private Socket socket;
    private JsonSerializerOptions jsonOptions;
    private List<DNSRecord> dnsRecords;  // Loaded DNS records from JSON

    public ServerUDP()
    {
        // Set up JSON options (for enum serialization)
        jsonOptions = new JsonSerializerOptions { WriteIndented = false };
        jsonOptions.Converters.Add(new JsonStringEnumConverter());
        dnsRecords = new List<DNSRecord>();
    }

    public static void Main()
    {
        ServerUDP server = new ServerUDP();
        server.Start();
    }

    /// <summary>
    /// Start the server: load config and DNS data, bind socket, and listen for incoming messages.
    /// </summary>
    public void Start()
    {
        try
        {
            LoadConfiguration();
            LoadDNSRecords();
            InitializeSocket();
            Console.WriteLine($"Server: Listening on {serverIP}:{serverPort}");

            // Main server loop to handle clients sequentially
            while (true)
            {
                // Wait for a Hello message from a client (start of handshake)
                EndPoint clientEP = new IPEndPoint(IPAddress.Any, 0);
                byte[] buffer = new byte[2048];
                int receivedBytes = socket.ReceiveFrom(buffer, ref clientEP);
                string requestJson = Encoding.UTF8.GetString(buffer, 0, receivedBytes);

                Message? requestMsg = null;
                try
                {
                    requestMsg = JsonSerializer.Deserialize<Message>(requestJson, jsonOptions);
                }
                catch (JsonException)
                {
                    Console.WriteLine($"Server: Received invalid JSON from {clientEP}, ignoring.");
                    continue;  // ignore and wait for a proper Hello
                }

                if (requestMsg == null)
                {
                    Console.WriteLine($"Server: Received unrecognized message from {clientEP}, ignoring.");
                    continue;
                }

                // Expecting a Hello to start session
                if (requestMsg.MsgType != MessageType.Hello)
                {
                    // If not a Hello, send an error back (protocol violation) and continue
                    Console.WriteLine($"Server: Protocol error - expected Hello, but got {requestMsg.MsgType} from {clientEP}");
                    SendError(clientEP, "Expected Hello message");
                    continue;
                }

                // Log and respond to Hello
                string helloContent = requestMsg.Content is JsonElement element ? element.GetString() ?? "" : "";
                Console.WriteLine($"Server: Received Hello (MsgId={requestMsg.MsgId}) from {clientEP}: \"{helloContent}\"");
                // Send Welcome
                Message welcomeMsg = new Message
                {
                    MsgId = new Random().Next(1, 10000),
                    MsgType = MessageType.Welcome,
                    Content = "Welcome from server"
                };
                byte[] welcomeData = JsonSerializer.SerializeToUtf8Bytes(welcomeMsg, jsonOptions);
                socket.SendTo(welcomeData, clientEP);
                Console.WriteLine($"Server: Sent Welcome (MsgId={welcomeMsg.MsgId}) to {clientEP}");

                // Now handle this client's DNSLookup requests in a session loop
                HandleClientSession(clientEP);
                // After session ends (End message sent), loop back to wait for next client Hello.
            }
        }
        catch (SocketException se)
        {
            Console.Error.WriteLine($"Server socket error: {se.Message}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Server error: {ex.Message}");
        }
        finally
        {
            socket?.Close();
        }
    }

    /// <summary>
    /// Load server IP and port from Settings.json.
    /// </summary>
    private void LoadConfiguration()
    {
        // Go up directories from bin/Debug/net8.0 to the project root
        string settingsPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Setting.json");
        settingsPath = Path.GetFullPath(settingsPath); // Convert to absolute path

        if (!File.Exists(settingsPath))
        {
            throw new Exception($"Settings.json not found at {settingsPath}");
        }

        string json = File.ReadAllText(settingsPath);
        var settings = JsonSerializer.Deserialize<Settings>(json, jsonOptions);

        if (settings == null)
            throw new Exception("Failed to parse Settings.json for configuration.");

        serverIP = settings.ServerIP;
        serverPort = settings.ServerPort;
    }


    /// <summary>
    /// Load DNS records from dnsrecords.json into the dnsRecords list.
    /// </summary>
    private void LoadDNSRecords()
    {
        string baseDir = AppContext.BaseDirectory;
        string dnsPath = System.IO.Path.Combine(baseDir, "..", "..", "..", "dnsrecords.json");
        if (!System.IO.File.Exists(dnsPath))
        {
            throw new Exception($"DNS records file not found at {dnsPath}");
        }
        string json = System.IO.File.ReadAllText(dnsPath);
        // Deserialize JSON array of DNSRecord objects
        List<DNSRecord>? records = JsonSerializer.Deserialize<List<DNSRecord>>(json, jsonOptions);
        if (records == null)
            throw new Exception("Failed to parse dnsrecords.json");
        dnsRecords = records;
    }

    /// <summary>
    /// Initialize and bind the UDP socket for the server.
    /// </summary>
    private void InitializeSocket()
    {
        socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        IPEndPoint serverEndPoint = new IPEndPoint(IPAddress.Parse(serverIP), serverPort);
        socket.Bind(serverEndPoint);
    }

    /// <summary>
    /// Handle an active client session: process DNSLookup, DNSLookupReply/Error, Ack, and send End.
    /// </summary>
    /// <param name="clientEP">The endpoint of the connected client.</param>
    private void HandleClientSession(EndPoint clientEP)
    {
        int queriesHandled = 0;      // count of DNSLookup messages processed in this session
        bool awaitingAck = false;    // whether we're waiting for an Ack for a reply we sent
        int lastQueryId = 0;         // the MsgId of the last DNSLookup request (for which a reply was sent)

        byte[] buffer = new byte[2048];
        EndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);

        // Loop to receive DNSLookup and Ack messages from this client
        while (true)
        {
            int receivedBytes;
            string requestJson;
            Message? requestMsg;
            try
            {
                // Receive next message from client (will block until message arrives)
                remoteEP = new IPEndPoint(IPAddress.Any, 0);
                receivedBytes = socket.ReceiveFrom(buffer, ref remoteEP);
                // Only proceed if the message is from the same client (same IP and port)
                if (!remoteEP.Equals(clientEP))
                {
                    // If a different client sends a Hello during an ongoing session, we ignore it (or could queue it).
                    Console.WriteLine($"Server: Received message from new client {remoteEP} during active session - ignoring.");
                    continue;
                }
                requestJson = Encoding.UTF8.GetString(buffer, 0, receivedBytes);
                requestMsg = JsonSerializer.Deserialize<Message>(requestJson, jsonOptions);
            }
            catch (JsonException)
            {
                Console.WriteLine($"Server: Received malformed JSON from {clientEP}, sending error.");
                SendError(clientEP, "Invalid message format");
                // Continue to next iteration, do not break session yet
                awaitingAck = false;
                continue;
            }
            catch (SocketException se)
            {
                Console.WriteLine($"Server: Socket error during receive: {se.Message}");
                break;  // break out of session loop on socket error (will terminate server or session)
            }

            if (requestMsg == null)
            {
                Console.WriteLine($"Server: Could not parse message from {clientEP}, ignoring.");
                continue;
            }

            // Process message based on its type
            if (requestMsg.MsgType == MessageType.DNSLookup)
            {
                // If a DNSLookup arrives while waiting for an Ack to a previous reply, it's out of order
                if (awaitingAck)
                {
                    Console.WriteLine($"Server: Protocol error - received DNSLookup (MsgId={requestMsg.MsgId}) while awaiting Ack from {clientEP}");
                    SendError(clientEP, "Ack expected, not DNSLookup");
                    // We consider this a fatal protocol error for the session
                    break;
                }

                // Parse DNSLookup content (should contain Type and Name)
                string queryType;
                string queryName;
                if (requestMsg.Content is JsonElement contentElem)
                {
                    if (contentElem.ValueKind == JsonValueKind.String)
                    {
                        // If content is a simple string, treat it as the domain name (type assumed "A" by default)
                        queryName = contentElem.GetString() ?? "";
                        queryType = "A";
                    }
                    else if (contentElem.ValueKind == JsonValueKind.Object)
                    {
                        // Content is an object, try to get "Type" and "Name"
                        bool hasType = contentElem.TryGetProperty("Type", out JsonElement typeElem);
                        bool hasName = contentElem.TryGetProperty("Name", out JsonElement nameElem);
                        if (hasType && hasName)
                        {
                            queryType = typeElem.GetString() ?? "";
                            queryName = nameElem.GetString() ?? "";
                        }
                        else
                        {
                            // Missing Type or Name field
                            Console.WriteLine($"Server: Received DNSLookup with incomplete content from {clientEP}");
                            SendError(clientEP, "Invalid DNSLookup format");
                            continue;  // wait for next message (could be another query)
                        }
                    }
                    else
                    {
                        // Content is not string or object (unexpected type, e.g., number)
                        Console.WriteLine($"Server: Received DNSLookup with unsupported content type from {clientEP}");
                        SendError(clientEP, "Invalid DNSLookup format");
                        continue;
                    }
                }
                else
                {
                    // Content is null or not JsonElement (which shouldn't happen if JSON was parsed correctly)
                    Console.WriteLine($"Server: DNSLookup content missing or unrecognized from {clientEP}");
                    SendError(clientEP, "Invalid DNSLookup format");
                    continue;
                }

                queriesHandled++;
                lastQueryId = requestMsg.MsgId;
                Console.WriteLine($"Server: Received DNSLookup (MsgId={lastQueryId}) from {clientEP} requesting {queryType} record for \"{queryName}\"");

                // Look up the DNS record in our data
                DNSRecord? resultRecord = dnsRecords.Find(r =>
                    r.Type.Equals(queryType, StringComparison.OrdinalIgnoreCase) &&
                    r.Name.Equals(queryName, StringComparison.OrdinalIgnoreCase));

                if (resultRecord != null)
                {
                    // Record found – send DNSLookupReply with the same MsgId and full DNSRecord as content
                    Message replyMsg = new Message
                    {
                        MsgId = lastQueryId,  // reuse client's query ID for reply
                        MsgType = MessageType.DNSLookupReply,
                        Content = resultRecord
                    };
                    byte[] replyData = JsonSerializer.SerializeToUtf8Bytes(replyMsg, jsonOptions);
                    socket.SendTo(replyData, clientEP);
                    Console.WriteLine($"Server: Sent DNSLookupReply (MsgId={replyMsg.MsgId}) to {clientEP} -> {resultRecord.Type} {resultRecord.Name} = {resultRecord.Value} (TTL={resultRecord.TTL})");
                    awaitingAck = true;  // now expect an Ack from client for this reply
                }
                else
                {
                    // No record found – send an Error message to client
                    SendError(clientEP, "Domain not found");
                    Console.WriteLine($"Server: DNS record not found for \"{queryName}\" (Type={queryType}), sent Error to {clientEP}");
                    awaitingAck = false;
                }

                // If we've handled the required number of queries (e.g., 4), we may end the session
                if (queriesHandled >= 4)
                {
                    if (!awaitingAck)
                    {
                        // If the last message was an Error (no Ack expected), we can end session now
                        SendEnd(clientEP);
                        Console.WriteLine($"Server: Sent End to {clientEP} (session complete with {queriesHandled} queries)");
                        break;
                    }
                    // If awaitingAck is true, we'll send End after receiving the Ack (handled below).
                }
            }
            else if (requestMsg.MsgType == MessageType.Ack)
            {
                // Ack received from client
                int ackContentId = 0;
                if (requestMsg.Content is JsonElement elem && elem.ValueKind == JsonValueKind.Number)
                {
                    ackContentId = elem.GetInt32();
                }
                else if (requestMsg.Content is JsonElement elemStr && elemStr.ValueKind == JsonValueKind.String)
                {
                    // If the Ack content was serialized as string
                    int.TryParse(elemStr.GetString(), out ackContentId);
                }

                Console.WriteLine($"Server: Received Ack (MsgId={requestMsg.MsgId}) from {clientEP} for MsgId={ackContentId}");
                if (!awaitingAck)
                {
                    // Unexpected Ack (no reply pending)
                    Console.WriteLine($"Server: Protocol error - unexpected Ack from {clientEP} (no reply was pending)");
                    // We can ignore it or break. Here, break the session due to protocol error.
                    break;
                }
                if (ackContentId != lastQueryId)
                {
                    // The Ack content doesn't match the last query ID
                    Console.WriteLine($"Server: Warning - Ack content {ackContentId} does not match last query ID {lastQueryId}");
                    // We still proceed but note the discrepancy
                }
                awaitingAck = false;  // Ack received, no longer waiting for it

                // If this Ack was for the final query of the session, send End
                if (queriesHandled >= 4)
                {
                    SendEnd(clientEP);
                    Console.WriteLine($"Server: Sent End to {clientEP} after final Ack (session handled {queriesHandled} queries)");
                    break;
                }
                // Otherwise, continue to wait for the next DNSLookup from client
            }
            else if (requestMsg.MsgType == MessageType.Hello)
            {
                // If a Hello is received in the middle of a session, that's out of order
                Console.WriteLine($"Server: Received unexpected Hello (MsgId={requestMsg.MsgId}) from {clientEP} during ongoing session.");
                SendError(clientEP, "Already in session");
                // We could continue the session or break. Here we break the session on protocol violation.
                break;
            }
            else
            {
                // Any other message types (Welcome, DNSRecord, End from client, etc.) are not expected from client
                Console.WriteLine($"Server: Received unexpected {requestMsg.MsgType} message from {clientEP}, sending error and terminating session.");
                SendError(clientEP, "Unexpected message type");
                break;
            }
        } // end inner while (client session loop)

        // Session loop ended; any necessary cleanup or resetting state can be done here.
        // (In this design, state is reset by reinitializing variables when a new session starts.)
    }

    /// <summary>
    /// Send an Error message to the specified client endpoint with given error text.
    /// </summary>
    private void SendError(EndPoint clientEP, string errorReason)
    {
        Message errorMsg = new Message
        {
            MsgId = new Random().Next(1000, 999999),
            MsgType = MessageType.Error,
            Content = errorReason
        };
        byte[] errorData = JsonSerializer.SerializeToUtf8Bytes(errorMsg, jsonOptions);
        socket.SendTo(errorData, clientEP);
        // (Logging for errors is done by the caller of this method)
    }

    /// <summary>
    /// Send an End message to the specified client endpoint.
    /// </summary>
    private void SendEnd(EndPoint clientEP)
    {
        Message endMsg = new Message
        {
            MsgId = new Random().Next(1000, 999999),
            MsgType = MessageType.End,
            Content = "End of DNSLookup"
        };
        byte[] endData = JsonSerializer.SerializeToUtf8Bytes(endMsg, jsonOptions);
        socket.SendTo(endData, clientEP);
    }

    // Internal class for reading server settings from JSON
    private class Settings
    {
        public string ServerIP { get; set; } = "127.0.0.1";
        public int ServerPort { get; set; } = 9050;
    }
}