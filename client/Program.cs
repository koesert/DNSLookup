using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LibData;

/// <summary>
/// ClientUDP klasse voor DNS lookup verzoeken via UDP protocol
/// </summary>
public class ClientUDP
{
    // Configuratie eigenschappen
    private string serverIP;
    private int serverPort;
    private string clientIP;
    private int clientPort;
    private Socket socket;
    private Random rand = new Random();
    private JsonSerializerOptions jsonOptions;

    /// <summary>
    /// Constructor voor ClientUDP
    /// </summary>
    public ClientUDP()
    {
        // Configureer JSON opties (enums als strings, negeer null waarden)
        jsonOptions = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        jsonOptions.Converters.Add(new JsonStringEnumConverter());
    }

    /// <summary>
    /// Main method - startpunt van de applicatie
    /// </summary>
    public static void Main()
    {
        ClientUDP client = new ClientUDP();
        client.Start();
    }

    /// <summary>
    /// Start de client en voer het DNS lookup protocol uit
    /// </summary>
    public void Start()
    {
        try
        {
            Console.WriteLine("Client: Configuratie wordt geladen...");
            // Inladen van configuratie instellingen
            LoadConfiguration();

            Console.WriteLine("Client: UDP socket wordt geïnitialiseerd...");
            // Initialiseer en configureer de UDP socket
            InitializeSocket();

            Console.WriteLine("Client: DNS records worden geladen uit dnsrecords.json...");
            // Laad DNS records die gebruikt worden voor tests
            List<DNSRecord> dnsRecords = LoadDnsRecords();
            Console.WriteLine($"Client: {dnsRecords.Count} DNS records geladen.");

            // Bereid server endpoint voor communicatie
            if (string.IsNullOrEmpty(serverIP))
                throw new ArgumentException("Server IP adres mag niet leeg zijn");

            IPEndPoint serverEndPoint = new IPEndPoint(IPAddress.Parse(serverIP), serverPort);

            // 1. Handshake: Hello -> Welcome bericht uitwisseling
            PerformHandshake(serverEndPoint);

            // 2. Bereid DNS lookup verzoeken voor (2 geldige en 2 ongeldige)
            List<Message> queries = PrepareDnsQueries(dnsRecords);

            // Verstuur alle queries en verwerk de antwoorden
            ProcessQueries(queries, serverEndPoint);

            // 3. Wacht op End bericht van server
            WaitForEndMessage();

            Console.WriteLine("Client: UDP client afgerond. Programma wordt afgesloten.");
        }
        catch (SocketException ex)
        {
            Console.WriteLine($"Client: Netwerkfout - {ex.Message} (ErrorCode: {ex.ErrorCode})");
        }
        catch (JsonException ex)
        {
            Console.WriteLine($"Client: JSON verwerkingsfout - {ex.Message}");
        }
        catch (FileNotFoundException ex)
        {
            Console.WriteLine($"Client: Bestand niet gevonden - {ex.Message}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Client: Fout - {ex.Message}");
            if (ex.InnerException != null)
            {
                Console.WriteLine($"Client: Onderliggende fout - {ex.InnerException.Message}");
            }
        }
        finally
        {
            // Zorg dat de socket altijd wordt gesloten, zelfs bij fouten
            if (socket != null && socket.Connected)
            {
                socket.Shutdown(SocketShutdown.Both);
                socket.Close();
            }
            else if (socket != null)
            {
                socket.Close();
            }
        }
    }

    /// <summary>
    /// Laad de configuratie uit het Settings.json bestand
    /// </summary>
    private void LoadConfiguration()
    {
        try
        {
            string baseDir = AppContext.BaseDirectory;
            string settingsPath = Path.Combine(baseDir, "../", "../", "../", "../", "Setting.json");

            if (!File.Exists(settingsPath))
            {
                throw new FileNotFoundException($"Configuratiebestand niet gevonden op pad: {settingsPath}");
            }

            string settingsJson = File.ReadAllText(settingsPath);
            var settings = JsonSerializer.Deserialize<Settings>(settingsJson, jsonOptions);

            if (settings == null)
            {
                throw new Exception("Instellingen konden niet worden geladen.");
            }

            // Configureer client-instellingen met validatie
            serverIP = settings.ServerIP ?? throw new Exception("ServerIP mag niet null zijn in de instellingen.");
            serverPort = settings.ServerPort > 0 ? settings.ServerPort : throw new Exception("ServerPort moet groter zijn dan 0.");
            clientIP = string.IsNullOrEmpty(settings.ClientIP) ? "0.0.0.0" : settings.ClientIP;
            clientPort = settings.ClientPort;

            Console.WriteLine($"Client: Configuratie -> Server={serverIP}:{serverPort}, ClientPort={clientPort}");
        }
        catch (Exception ex)
        {
            throw new Exception("Fout bij het laden van de configuratie", ex);
        }
    }

    /// <summary>
    /// Initialiseer de UDP socket en bind deze indien nodig
    /// </summary>
    private void InitializeSocket()
    {
        try
        {
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

            // Stel een timeout in voor ontvangen om hangende operaties te voorkomen
            socket.ReceiveTimeout = 10000; // 10 seconden timeout

            if (clientPort > 0)
            {
                // Bind client socket aan opgegeven IP en poort (indien opgegeven)
                IPAddress bindAddress;
                if (clientIP == "0.0.0.0" || string.IsNullOrEmpty(clientIP))
                {
                    bindAddress = IPAddress.Any;
                }
                else
                {
                    if (!IPAddress.TryParse(clientIP, out bindAddress))
                    {
                        throw new FormatException($"Ongeldig IP adres formaat: {clientIP}");
                    }
                }

                IPEndPoint clientEndPoint = new IPEndPoint(bindAddress, clientPort);
                socket.Bind(clientEndPoint);
                Console.WriteLine($"Client: Socket gebonden aan {clientEndPoint.Address}:{clientEndPoint.Port}");
            }
            else
            {
                Console.WriteLine("Client: Dynamische poort wordt gebruikt (geen client poort opgegeven).");
            }
        }
        catch (Exception ex)
        {
            throw new Exception("Fout bij het initialiseren van de UDP socket", ex);
        }
    }

    /// <summary>
    /// Laad DNS records uit het JSON-bestand
    /// </summary>
    /// <returns>Lijst van DNS records</returns>
    private List<DNSRecord> LoadDnsRecords()
    {
        try
        {
            string baseDir = AppContext.BaseDirectory;
            string dnsPath = Path.Combine(baseDir, "../", "../", "../", "../", "server/dnsrecords.json");

            if (!File.Exists(dnsPath))
            {
                Console.WriteLine($"Waarschuwing: DNS records bestand niet gevonden op pad: {dnsPath}");
                return new List<DNSRecord>();
            }

            string dnsJson = File.ReadAllText(dnsPath);
            List<DNSRecord> dnsRecords = JsonSerializer.Deserialize<List<DNSRecord>>(dnsJson, jsonOptions) ?? new List<DNSRecord>();
            return dnsRecords;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Waarschuwing: Kon DNS records niet laden: {ex.Message}");
            // Retourneer een lege lijst in plaats van een exception te gooien om door te kunnen gaan
            return new List<DNSRecord>();
        }
    }

    /// <summary>
    /// Voer de handshake uit met de server (Hello -> Welcome)
    /// </summary>
    /// <param name="serverEndPoint">Server endpoint voor communicatie</param>
    private void PerformHandshake(IPEndPoint serverEndPoint)
    {
        // Genereer een Hello bericht
        Message helloMsg = new Message
        {
            MsgId = rand.Next(1, 1000),
            MsgType = MessageType.Hello,
            Content = "Hello from client"
        };

        // Stuur het Hello bericht
        SendMessage(helloMsg, serverEndPoint);

        try
        {
            // Wacht op Welcome antwoord
            EndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);
            byte[] buffer = new byte[1024];
            int receivedBytes = socket.ReceiveFrom(buffer, ref remoteEP);

            string recvJson = Encoding.UTF8.GetString(buffer, 0, receivedBytes);
            Console.WriteLine($"Client: Ontvangen van server: {recvJson}");

            Message? welcomeMsg = JsonSerializer.Deserialize<Message>(recvJson, jsonOptions);
            if (welcomeMsg == null)
            {
                throw new Exception("Ongeldig antwoord ontvangen van server tijdens handshake.");
            }

            if (welcomeMsg.MsgType != MessageType.Welcome)
            {
                throw new Exception($"Protocol fout - Welcome verwacht, maar {welcomeMsg.MsgType} ontvangen.");
            }

            Console.WriteLine($"Client: Welcome ontvangen. Inhoud: {welcomeMsg.Content}");
        }
        catch (SocketException ex)
        {
            throw new Exception("Socket fout tijdens handshake met server", ex);
        }
        catch (JsonException ex)
        {
            throw new Exception("Kon het server antwoord niet verwerken tijdens handshake", ex);
        }
    }

    /// <summary>
    /// Bereid DNS lookup queries voor (2 geldige en 2 ongeldige)
    /// </summary>
    /// <param name="dnsRecords">Beschikbare DNS records</param>
    /// <returns>Lijst van query berichten</returns>
    private List<Message> PrepareDnsQueries(List<DNSRecord> dnsRecords)
    {
        // Kies geldige records voor queries indien beschikbaar
        DNSRecord? validQuery1 = dnsRecords.Count > 0 ? dnsRecords[0] : null;
        DNSRecord? validQuery2 = null;

        if (validQuery1 != null)
        {
            // Zoek een record met een ander type indien mogelijk
            validQuery2 = dnsRecords.Find(r => !r.Type.Equals(validQuery1.Type, StringComparison.OrdinalIgnoreCase));
            if (validQuery2 == null && dnsRecords.Count > 1)
                validQuery2 = dnsRecords[1];
        }

        // Gebruik standaard waarden als er geen records beschikbaar zijn
        if (validQuery1 == null) validQuery1 = new DNSRecord { Type = "A", Name = "example.com" };
        if (validQuery2 == null) validQuery2 = new DNSRecord { Type = "A", Name = "example.net" };

        // Maak een lijst met test queries (2 geldig, 2 ongeldig)
        var queries = new List<Message>();

        // Query 1 (Ongeldig): Onbekend domein (alleen naam, geen type)
        queries.Add(new Message
        {
            MsgId = rand.Next(1000, 9999),
            MsgType = MessageType.DNSLookup,
            Content = "unknown.domain"
        });

        // Query 2 (Geldig): DNS record met type & naam (validQuery1)
        queries.Add(new Message
        {
            MsgId = rand.Next(1000, 9999),
            MsgType = MessageType.DNSLookup,
            Content = new DNSRecord { Type = validQuery1.Type, Name = validQuery1.Name }
        });

        // Query 3 (Ongeldig): Onjuist formaat (Type opgegeven, Naam ontbreekt)
        queries.Add(new Message
        {
            MsgId = rand.Next(1000, 9999),
            MsgType = MessageType.DNSLookup,
            Content = new DNSRecord { Type = "A", Value = validQuery1.Name }
        });

        // Query 4 (Geldig): Een andere geldige DNS record (validQuery2)
        queries.Add(new Message
        {
            MsgId = rand.Next(1000, 9999),
            MsgType = MessageType.DNSLookup,
            Content = new DNSRecord { Type = validQuery2.Type, Name = validQuery2.Name }
        });

        return queries;
    }

    /// <summary>
    /// Verwerk alle queries en hun antwoorden
    /// </summary>
    /// <param name="queries">Te versturen DNS queries</param>
    /// <param name="serverEndPoint">Server endpoint</param>
    private void ProcessQueries(List<Message> queries, IPEndPoint serverEndPoint)
    {
        Console.WriteLine("Client: DNS lookup verzoeken worden gestart...");

        byte[] buffer = new byte[1024];
        EndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);

        foreach (Message query in queries)
        {
            try
            {
                // Stuur DNSLookup query
                string? info = query.Content != null ? query.Content.ToString() : "(geen inhoud)";
                Console.WriteLine($"Client: DNSLookup wordt verstuurd (MsgId={query.MsgId}, Content={info})");
                SendMessage(query, serverEndPoint);

                // Ontvang server antwoord
                int receivedBytes = socket.ReceiveFrom(buffer, ref remoteEP);
                string recvJson = Encoding.UTF8.GetString(buffer, 0, receivedBytes);
                Console.WriteLine($"Client: Ontvangen van server: {recvJson}");

                Message? response = JsonSerializer.Deserialize<Message>(recvJson, jsonOptions);
                if (response == null)
                {
                    Console.WriteLine("Client: Kan server antwoord niet verwerken. Doorgaan naar volgende query.");
                    continue;
                }

                // Verwerk het antwoord op basis van het berichttype
                ProcessResponse(response, serverEndPoint);
            }
            catch (SocketException ex)
            {
                Console.WriteLine($"Client: Socket fout bij query {query.MsgId}: {ex.Message}");
                // Ga door met volgende query in plaats van volledig te stoppen
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Client: Fout bij query {query.MsgId}: {ex.Message}");
                // Ga door met volgende query in plaats van volledig te stoppen
            }
        }
    }

    /// <summary>
    /// Verwerk een antwoord van de server
    /// </summary>
    /// <param name="response">Het ontvangen antwoord</param>
    /// <param name="serverEndPoint">Server endpoint</param>
    private void ProcessResponse(Message response, IPEndPoint serverEndPoint)
    {
        switch (response.MsgType)
        {
            case MessageType.DNSLookupReply:
                // Succesvol DNS lookup antwoord
                ProcessDnsLookupReply(response, serverEndPoint);
                break;

            case MessageType.Error:
                // Fout antwoord (geen Ack verwacht)
                Console.WriteLine($"Client: Fout ontvangen van server: {response.Content}");
                // Geen Ack sturen voor een Error
                break;

            case MessageType.End:
                // End onverwacht ontvangen (server eindigde vroegtijdig)
                Console.WriteLine("Client: 'End' bericht onverwacht ontvangen. Wordt beëindigd.");
                break;

            default:
                // Onverwacht berichttype
                Console.WriteLine($"Client: Onverwacht antwoord ({response.MsgType}). Doorgaan met volgende query.");
                break;
        }
    }

    /// <summary>
    /// Verwerk een DNS lookup antwoord
    /// </summary>
    /// <param name="response">Het DNSLookupReply antwoord</param>
    /// <param name="serverEndPoint">Server endpoint</param>
    private void ProcessDnsLookupReply(Message response, IPEndPoint serverEndPoint)
    {
        DNSRecord? record = null;
        try
        {
            if (response.Content?.ToString() is string content)
            {
                record = JsonSerializer.Deserialize<DNSRecord>(content, jsonOptions);
            }
        }
        catch (JsonException ex)
        {
            Console.WriteLine($"Client: Kon DNS record niet parsen: {ex.Message}");
        }

        if (record != null)
        {
            Console.WriteLine($"Client: DNSLookupReply (MsgId={response.MsgId}) -> " +
                              $"Name={record.Name}, Type={record.Type}, Value={record.Value}, TTL={record.TTL}");
        }
        else
        {
            Console.WriteLine($"Client: DNSLookupReply ontvangen (MsgId={response.MsgId}).");
        }

        // Stuur Ack voor deze DNSLookupReply
        SendAcknowledgement(response.MsgId, serverEndPoint);
    }

    /// <summary>
    /// Stuur een bevestiging (Ack) voor een ontvangen bericht
    /// </summary>
    /// <param name="originalMsgId">ID van het te bevestigen bericht</param>
    /// <param name="serverEndPoint">Server endpoint</param>
    private void SendAcknowledgement(int originalMsgId, IPEndPoint serverEndPoint)
    {
        Message ackMsg = new Message
        {
            MsgId = rand.Next(1000, 9999),
            MsgType = MessageType.Ack,
            Content = originalMsgId.ToString()
        };
        Console.WriteLine($"Client: Ack wordt verstuurd voor MsgId {originalMsgId}");
        SendMessage(ackMsg, serverEndPoint);
    }

    /// <summary>
    /// Wacht op het End bericht van de server
    /// </summary>
    private void WaitForEndMessage()
    {
        try
        {
            Console.WriteLine("Client: Alle verzoeken afgerond. Wachten op End bericht...");

            EndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);
            byte[] buffer = new byte[1024];

            int receivedBytes = socket.ReceiveFrom(buffer, ref remoteEP);
            string recvJson = Encoding.UTF8.GetString(buffer, 0, receivedBytes);
            Console.WriteLine($"Client: Ontvangen van server: {recvJson}");

            Message? endMsg = JsonSerializer.Deserialize<Message>(recvJson, jsonOptions);
            if (endMsg != null && endMsg.MsgType == MessageType.End)
            {
                Console.WriteLine($"Client: End ontvangen. Inhoud: {endMsg.Content}");
            }
            else
            {
                Console.WriteLine($"Client: Protocol fout - End verwacht, maar {endMsg?.MsgType} ontvangen");
            }
        }
        catch (SocketException ex)
        {
            throw new Exception("Socket fout bij wachten op End bericht", ex);
        }
    }

    /// <summary>
    /// Hulpfunctie om een bericht naar de server te sturen
    /// </summary>
    /// <param name="message">Het te versturen bericht</param>
    /// <param name="endpoint">Het eindpunt waar het bericht naar toe moet</param>
    private void SendMessage(Message message, EndPoint endpoint)
    {
        try
        {
            string json = JsonSerializer.Serialize(message, jsonOptions);
            byte[] data = Encoding.UTF8.GetBytes(json);

            int bytesSent = socket.SendTo(data, endpoint);
            if (bytesSent != data.Length)
            {
                Console.WriteLine($"Waarschuwing: Niet alle bytes verstuurd ({bytesSent}/{data.Length})");
            }

            Console.WriteLine($"Client: {message.MsgType} verstuurd naar {((IPEndPoint)endpoint).Address}:{((IPEndPoint)endpoint).Port}: {json}");
        }
        catch (SocketException ex)
        {
            throw new Exception($"Fout bij verzenden van {message.MsgType} bericht", ex);
        }
        catch (Exception ex)
        {
            throw new Exception($"Algemene fout bij verzenden van {message.MsgType} bericht", ex);
        }
    }

    /// <summary>
    /// Klasse voor het deserialiseren van instellingen uit JSON
    /// </summary>
    private class Settings
    {
        public string? ServerIP { get; set; }
        public int ServerPort { get; set; }
        public string? ClientIP { get; set; }
        public int ClientPort { get; set; }
    }
}