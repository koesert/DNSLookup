using System.Collections.Immutable;
using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using LibData;

// SendTo();
class Program
{
    static void Main(string[] args)
    {
        ClientUDP.start();
    }
}

public class Setting
{
    public int ServerPortNumber { get; set; }
    public string? ServerIPAddress { get; set; }
    public int ClientPortNumber { get; set; }
    public string? ClientIPAddress { get; set; }
}

class ClientUDP
{

    //TODO: [Deserialize Setting.json]
    static string configFile = @"../Setting.json";
    static string configContent = File.ReadAllText(configFile);
    static Setting? setting = JsonSerializer.Deserialize<Setting>(configContent);
    private static Socket clientSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    private static EndPoint serverEndPoint;
    private static int msgCounter = 1;


    public static void start()
    {

        try
        {
            // Setup endpoints
            serverEndPoint = (EndPoint)new IPEndPoint(
                IPAddress.Parse(setting.ServerIPAddress),
                setting.ServerPortNumber
            );

            // 1. Send Hello
            var helloMsg = new Message
            {
                MsgId = GenerateMsgId(),
                MsgType = MessageType.Hello,
                Content = "Hello from client"
            };
            SendMessage(helloMsg);

            // Receive Welcome
            var welcome = ReceiveMessage();
            if (welcome.MsgType != MessageType.Welcome)
            {
                Console.WriteLine($"Protocol violation: Expected Welcome but received {welcome.MsgType}");
                Environment.Exit(1);
            }

            // DNS Lookups
            var lookups = new[] {
                new { Type = "A", Name = "www.example.com", Valid = true },
                new { Type = "MX", Name = "example.com", Valid = true },
                new { Type = "A", Name = "invalid.domain", Valid = false },
                new { Type = "CNAME", Name = "missing.record", Valid = false }
            };

            foreach (var lookup in lookups)
            {
                var lookupMsg = new Message
                {
                    MsgId = GenerateMsgId(),
                    MsgType = MessageType.DNSLookup,
                    Content = new DNSRecord
                    {
                        Type = lookup.Type,
                        Name = lookup.Name
                    }
                };

                SendMessage(lookupMsg);

                // Handle response
                var response = ReceiveMessage();
                if (response.MsgType == MessageType.DNSLookupReply)
                {
                    Console.WriteLine($"DNS Record found: {JsonSerializer.Serialize(response.Content)}");
                }
                else if (response.MsgType == MessageType.Error)
                {
                    Console.WriteLine($"Error: {response.Content}");
                }

                // Send Ack
                var ackMsg = new Message
                {
                    MsgId = response.MsgId,
                    MsgType = MessageType.Ack,
                    Content = response.MsgId.ToString()
                };
                SendMessage(ackMsg);
            }

            // Receive End
            var endMsg = ReceiveMessage();
            if (endMsg.MsgType == MessageType.End)
            {
                Console.WriteLine("Received End message. Closing client.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Client error: {ex.Message}");
        }
    }

    private static int GenerateMsgId() => msgCounter++;

    private static void SendMessage(Message message)
    {
        string json = JsonSerializer.Serialize(message);
        byte[] data = Encoding.ASCII.GetBytes(json);
        clientSocket.SendTo(data, serverEndPoint);
        Console.WriteLine($"Sent: {message.MsgType} ({message.MsgId})");
    }

    private static Message ReceiveMessage()
    {
        byte[] buffer = new byte[1024];
        int received = clientSocket.ReceiveFrom(buffer, ref serverEndPoint);
        string json = Encoding.ASCII.GetString(buffer, 0, received);
        var message = JsonSerializer.Deserialize<Message>(json);
        Console.WriteLine($"Received: {message.MsgType} ({message.MsgId})");
        return message;
    }

}
