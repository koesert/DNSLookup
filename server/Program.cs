using System;
using System.Data;
using System.Data.SqlTypes;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using LibData;

// ReceiveFrom();
class Program
{
    static void Main(string[] args)
    {
        ServerUDP.start();
    }
}

public class Setting
{
    public int ServerPortNumber { get; set; }
    public string? ServerIPAddress { get; set; }
    public int ClientPortNumber { get; set; }
    public string? ClientIPAddress { get; set; }
}


class ServerUDP
{
    static string configFile = @"../Setting.json";
    static Setting? setting;

    static ServerUDP()
    {
        try
        {
            string configContent = File.ReadAllText(configFile);
            setting = JsonSerializer.Deserialize<Setting>(configContent);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to load settings: {ex.Message}");
            Environment.Exit(1);
        }
    }
    static List<DNSRecord> dnsRecords;
    static Socket serverSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    private static Dictionary<EndPoint, int> ackCounts = new Dictionary<EndPoint, int>();

    // TODO: [Read the JSON file and return the list of DNSRecords]




    public static void start()
    {
        try
        {
            // Load DNS records
            string dnsFile = @"DNSRecords.json";
            if (!File.Exists(dnsFile))
            {
                throw new FileNotFoundException($"DNS records file not found: {dnsFile}");
            }
            string dnsContent = File.ReadAllText(dnsFile);
            dnsRecords = JsonSerializer.Deserialize<List<DNSRecord>>(dnsContent)
                ?? new List<DNSRecord>();

            // Bind socket
            var localEndPoint = new IPEndPoint(
                IPAddress.Parse(setting!.ServerIPAddress!),
                setting!.ServerPortNumber
            );
            serverSocket.Bind(localEndPoint);
            Console.WriteLine("Server started...");

            while (true)
            {
                EndPoint clientEndPoint = new IPEndPoint(IPAddress.Any, 0);
                byte[] buffer = new byte[1024];

                // Receive message
                int received = serverSocket.ReceiveFrom(buffer, ref clientEndPoint);
                string json = Encoding.ASCII.GetString(buffer, 0, received);

                try
                {
                    var message = JsonSerializer.Deserialize<Message>(json);

                    // Validate required fields
                    if (message.MsgType == null || message.MsgId == 0)
                    {
                        SendError(clientEndPoint, "Invalid message structure", message.MsgId);
                        continue;
                    }

                    Console.WriteLine($"Received: {message.MsgType} from {clientEndPoint}");

                    switch (message.MsgType)
                    {
                        case MessageType.Hello:
                            HandleHello(message, clientEndPoint);
                            break;
                        case MessageType.DNSLookup:
                            HandleDnsLookup(message, clientEndPoint);
                            break;
                        case MessageType.Ack:
                            HandleAck(message, clientEndPoint);
                            break;
                        default:
                            SendError(clientEndPoint, "Invalid message type");
                            break;
                    }
                }
                catch (JsonException)
                {
                    SendError(clientEndPoint, "Invalid message format", 0);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Server error: {ex.Message}");
        }
    }

    private static void HandleAck(Message message, EndPoint clientEndPoint)
    {
        // Tel het aantal ontvangen Acks per client
        if (!ackCounts.ContainsKey(clientEndPoint))
        {
            ackCounts[clientEndPoint] = 0;
        }
        ackCounts[clientEndPoint]++;

        // Stuur End na 4 Acks (voor 4 DNSLookups)
        if (ackCounts[clientEndPoint] == 4)
        {
            var endMsg = new Message
            {
                MsgId = message.MsgId,
                MsgType = MessageType.End,
                Content = "End of DNSLookup"
            };
            SendMessage(endMsg, clientEndPoint);

            // Reset teller
            ackCounts.Remove(clientEndPoint);
        }
    }

    private static void HandleHello(Message message, EndPoint clientEndPoint)
    {
        var welcome = new Message
        {
            MsgId = message.MsgId,
            MsgType = MessageType.Welcome,
            Content = "Welcome from server"
        };
        SendMessage(welcome, clientEndPoint);
    }

    private static void HandleDnsLookup(Message message, EndPoint clientEndPoint)
    {
        try
        {
            var lookup = JsonSerializer.Deserialize<DNSRecord>(message.Content.ToString());
            var record = dnsRecords.FirstOrDefault(r =>
                r.Type == lookup.Type &&
                r.Name == lookup.Name);

            if (record != null)
            {
                var reply = new Message
                {
                    MsgId = message.MsgId,
                    MsgType = MessageType.DNSLookupReply,
                    Content = record
                };
                SendMessage(reply, clientEndPoint);
            }
            else
            {
                SendError(clientEndPoint, "Record not found", message.MsgId);
            }
        }
        catch
        {
            SendError(clientEndPoint, "Invalid DNS lookup format", message.MsgId);
        }
    }


    private static void SendError(EndPoint clientEndPoint, string errorMessage, int? originalMsgId = null)
    {
        if (!originalMsgId.HasValue)
        {
            throw new ArgumentNullException("Error messages require original message ID");
        }

        var error = new Message
        {
            MsgId = originalMsgId.Value, // Strict protocol compliance
            MsgType = MessageType.Error,
            Content = errorMessage
        };
        SendMessage(error, clientEndPoint);
    }

    private static void SendMessage(Message message, EndPoint clientEndPoint)
    {
        string json = JsonSerializer.Serialize(message);
        byte[] data = Encoding.ASCII.GetBytes(json);
        serverSocket.SendTo(data, clientEndPoint);
        Console.WriteLine($"Sent: {message.MsgType} to {clientEndPoint}");
    }

    private static int GenerateMsgId() => new Random().Next(1000, 9999);




}