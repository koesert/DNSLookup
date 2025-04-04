using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LibData;

/// <summary>
/// ServerUDP klasse voor het afhandelen van DNS lookup verzoeken via UDP protocol
/// </summary>
public class ServerUDP
{
    // Configuratie eigenschappen
    private string serverIP;
    private int serverPort;
    private List<DNSRecord> dnsRecords = new List<DNSRecord>();
    private Socket socket;
    private Random rand = new Random();
    private JsonSerializerOptions jsonOptions;

    // Constanten voor server configuratie
    private const int BUFFER_SIZE = 1024;
    private const int DEFAULT_EXPECTED_QUERIES = 4;
    private const int SOCKET_TIMEOUT = 30000; // 30 seconden timeout

    /// <summary>
    /// Constructor voor ServerUDP
    /// </summary>
    public ServerUDP()
    {
        // Configureer JSON opties voor serialisatie/deserialisatie
        jsonOptions = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull // negeer null velden
        };
        jsonOptions.Converters.Add(new JsonStringEnumConverter()); // enums als strings
    }

    /// <summary>
    /// Main method - startpunt van de applicatie
    /// </summary>
    public static void Main()
    {
        ServerUDP server = new ServerUDP();
        server.Start();
    }

    /// <summary>
    /// Start de server en verwerk inkomende client verzoeken
    /// </summary>
    public void Start()
    {
        try
        {
            Console.WriteLine("Server: Configuratie wordt geladen...");
            // Laad server configuratie
            LoadConfiguration();

            Console.WriteLine("Server: DNS records worden geladen uit dnsrecords.json...");
            // Laad de DNS records database
            LoadDnsRecords();
            Console.WriteLine($"Server: {dnsRecords.Count} DNS records geladen.");

            // Initialiseer en configureer de UDP socket
            InitializeSocket();

            Console.WriteLine("Server: Wachten op clients...");

            // Start de hoofdlus voor het verwerken van client verzoeken
            ProcessClientRequests();
        }
        catch (SocketException ex)
        {
            Console.WriteLine($"Server: Netwerkfout bij het starten - {ex.Message} (ErrorCode: {ex.ErrorCode})");
        }
        catch (JsonException ex)
        {
            Console.WriteLine($"Server: JSON verwerkingsfout - {ex.Message}");
        }
        catch (FileNotFoundException ex)
        {
            Console.WriteLine($"Server: Bestand niet gevonden - {ex.Message}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Server: Kon niet starten - {ex.Message}");
            if (ex.InnerException != null)
            {
                Console.WriteLine($"Server: Onderliggende fout - {ex.InnerException.Message}");
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
            Console.WriteLine("Server: Socket gesloten.");
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

            // Configureer server-instellingen met validatie
            serverIP = string.IsNullOrEmpty(settings.ServerIP) ? "0.0.0.0" : settings.ServerIP;
            serverPort = settings.ServerPort > 0 ? settings.ServerPort : 
                        throw new ArgumentException("ServerPort moet groter zijn dan 0");

            Console.WriteLine($"Server: Configuratie -> IP={serverIP}, Poort={serverPort}");
        }
        catch (Exception ex)
        {
            throw new Exception("Fout bij het laden van de configuratie", ex);
        }
    }

    /// <summary>
    /// Laad DNS records uit het JSON-bestand
    /// </summary>
    private void LoadDnsRecords()
    {
        try
        {
            string baseDir = AppContext.BaseDirectory;
            string dnsPath = Path.Combine(baseDir, "../", "../", "../", "dnsrecords.json");

            if (!File.Exists(dnsPath))
            {
                throw new FileNotFoundException($"DNS records bestand niet gevonden op pad: {dnsPath}");
            }

            string dnsJson = File.ReadAllText(dnsPath);
            List<DNSRecord>? loadedRecords = JsonSerializer.Deserialize<List<DNSRecord>>(dnsJson, jsonOptions);

            if (loadedRecords == null)
            {
                Console.WriteLine("Waarschuwing: Geen DNS records gevonden of leeg bestand.");
                dnsRecords = new List<DNSRecord>();
            }
            else
            {
                dnsRecords = loadedRecords;

                // Valideer de geladen records
                int invalidRecords = dnsRecords.RemoveAll(r =>
                    string.IsNullOrEmpty(r.Name) ||
                    string.IsNullOrEmpty(r.Type) ||
                    string.IsNullOrEmpty(r.Value));

                if (invalidRecords > 0)
                {
                    Console.WriteLine($"Waarschuwing: {invalidRecords} ongeldige DNS records verwijderd.");
                }
            }
        }
        catch (Exception ex)
        {
            throw new Exception("Fout bij het laden van DNS records", ex);
        }
    }

    /// <summary>
    /// Initialiseer de UDP socket en bind deze aan het server eindpunt
    /// </summary>
    private void InitializeSocket()
    {
        try
        {
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

            // Stel een receive timeout in om vastlopen te voorkomen
            socket.ReceiveTimeout = SOCKET_TIMEOUT;

            // Bepaal het bind adres
            IPAddress ipAddress;
            if (serverIP == "0.0.0.0" || string.IsNullOrEmpty(serverIP))
            {
                ipAddress = IPAddress.Any;
            }
            else
            {
                if (!IPAddress.TryParse(serverIP, out ipAddress))
                {
                    throw new FormatException($"Ongeldig IP adres formaat: {serverIP}");
                }
            }

            IPEndPoint localEP = new IPEndPoint(ipAddress, serverPort);
            socket.Bind(localEP);
            Console.WriteLine($"Server: UDP socket gebonden aan {localEP.Address}:{localEP.Port}");
        }
        catch (Exception ex)
        {
            throw new Exception("Fout bij het initialiseren van de UDP socket", ex);
        }
    }

    /// <summary>
    /// Hoofdlus voor het verwerken van client verzoeken
    /// </summary>
    private void ProcessClientRequests()
    {
        EndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);
        IPEndPoint? currentClientEP = null;
        bool sessionActive = false;
        int queriesHandled = 0;
        int expectedQueries = DEFAULT_EXPECTED_QUERIES; // verwacht minstens 4 queries per client sessie

        byte[] buffer = new byte[BUFFER_SIZE];

        // Hoofdlus voor het afhandelen van clients en berichten
        while (true)
        {
            try
            {
                // Ontvang een UDP bericht (blokkerend)
                int receivedBytes = socket.ReceiveFrom(buffer, ref remoteEP);
                string receivedJson = Encoding.UTF8.GetString(buffer, 0, receivedBytes);
                Console.WriteLine($"Server: Ontvangen van {remoteEP}: {receivedJson}");

                // Deserialiseer het bericht
                Message? msg = null;
                try
                {
                    msg = JsonSerializer.Deserialize<Message>(receivedJson, jsonOptions);
                }
                catch (JsonException ex)
                {
                    Console.WriteLine($"Server: Kon bericht niet ontleden - {ex.Message} (genegeerd).");
                    continue;
                }

                if (msg == null)
                {
                    Console.WriteLine("Server: Kon bericht niet ontleden (genegeerd).");
                    continue;
                }

                // Voor extra veiligheid, valideer MsgId
                if (msg.MsgId <= 0)
                {
                    Console.WriteLine("Server: Ongeldig MsgId in bericht (genegeerd).");
                    continue;
                }

                // Als er geen actieve sessie is, accepteer alleen Hello berichten
                if (!sessionActive)
                {
                    HandleInactiveSessionMessage(msg, remoteEP, ref sessionActive, ref currentClientEP, ref queriesHandled);
                    continue;
                }

                // Sessie is actief: controleer of het bericht van dezelfde client komt
                if (!ValidateMessageSender(remoteEP, currentClientEP))
                {
                    continue;
                }

                // Verwerk berichttypen binnen een actieve sessie
                HandleActiveSessionMessage(msg, currentClientEP, ref sessionActive, ref queriesHandled, ref expectedQueries);
            }
            catch (SocketException ex)
            {
                if (ex.ErrorCode == 10060) // Timeout
                {
                    Console.WriteLine("Server: Timeout bij wachten op berichten. Server blijft actief.");
                    // Reset sessie na lange inactiviteit
                    if (sessionActive)
                    {
                        Console.WriteLine("Server: Sessie gereset vanwege inactiviteit.");
                        sessionActive = false;
                        currentClientEP = null;
                        queriesHandled = 0;
                    }
                }
                else
                {
                    Console.WriteLine($"Server: Netwerk fout in communicatie: {ex.Message} (ErrorCode: {ex.ErrorCode})");
                    // Reset huidige sessie bij fout maar ga door
                    sessionActive = false;
                    currentClientEP = null;
                    queriesHandled = 0;
                    Console.WriteLine("Server: Sessie gereset vanwege netwerkfout. Wachten op nieuwe client...");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Server: Uitzondering in communicatie lus: {ex.Message}");
                // Reset huidige sessie bij fout en ga door
                sessionActive = false;
                currentClientEP = null;
                queriesHandled = 0;
                Console.WriteLine("Server: Sessie gereset vanwege fout. Wachten op nieuwe client...");
            }
        }
    }

    /// <summary>
    /// Controleer of het bericht van de huidige client komt
    /// </summary>
    /// <param name="remoteEP">Het ontvangen eindpunt</param>
    /// <param name="currentClientEP">Het huidige client eindpunt</param>
    /// <returns>True als het bericht van de actieve client komt</returns>
    private bool ValidateMessageSender(EndPoint remoteEP, IPEndPoint? currentClientEP)
    {
        IPEndPoint senderEP = (IPEndPoint)remoteEP;
        if (currentClientEP == null ||
            !senderEP.Address.Equals(currentClientEP.Address) ||
            senderEP.Port != currentClientEP.Port)
        {
            Console.WriteLine($"Server: Bericht van onbekende client {senderEP} genegeerd (sessie actief met {currentClientEP}).");
            return false;
        }
        return true;
    }

    /// <summary>
    /// Verwerk berichten wanneer er geen actieve sessie is
    /// </summary>
    private void HandleInactiveSessionMessage(
        Message msg,
        EndPoint remoteEP,
        ref bool sessionActive,
        ref IPEndPoint? currentClientEP,
        ref int queriesHandled)
    {
        // Verwacht een Hello bericht om een nieuwe sessie te starten
        if (msg.MsgType == MessageType.Hello)
        {
            sessionActive = true;
            currentClientEP = new IPEndPoint(((IPEndPoint)remoteEP).Address, ((IPEndPoint)remoteEP).Port);
            queriesHandled = 0;
            Console.WriteLine("Server: Hello ontvangen. -> Verstuur Welcome.");

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
            Console.WriteLine($"Server: Onverwacht {msg.MsgType} bericht vóór Hello (genegeerd).");
        }
    }

    /// <summary>
    /// Verwerk berichten binnen een actieve sessie
    /// </summary>
    private void HandleActiveSessionMessage(
        Message msg,
        IPEndPoint? currentClientEP,
        ref bool sessionActive,
        ref int queriesHandled,
        ref int expectedQueries)
    {
        if (currentClientEP == null)
        {
            Console.WriteLine("Server: Geen actieve client eindpunt (interne fout).");
            sessionActive = false;
            queriesHandled = 0;
            return;
        }

        switch (msg.MsgType)
        {
            case MessageType.DNSLookup:
                HandleDnsLookupMessage(msg, currentClientEP, ref sessionActive, ref queriesHandled, expectedQueries);
                break;

            case MessageType.Ack:
                HandleAckMessage(msg, currentClientEP, ref sessionActive, ref queriesHandled, expectedQueries);
                break;

            case MessageType.Hello:
                Console.WriteLine("Server: Hello ontvangen tijdens actieve sessie (genegeerd).");
                break;

            case MessageType.Welcome:
                Console.WriteLine("Server: Onverwacht Welcome bericht van client (genegeerd).");
                break;

            case MessageType.DNSLookupReply:
                Console.WriteLine("Server: Onverwacht DNSLookupReply van client (genegeerd).");
                break;

            case MessageType.End:
                Console.WriteLine("Server: 'End' ontvangen van client. Sessie wordt gesloten.");
                sessionActive = false;
                queriesHandled = 0;
                break;

            case MessageType.Error:
                Console.WriteLine("Server: Foutbericht ontvangen van client (genegeerd).");
                break;

            default:
                Console.WriteLine($"Server: Onbekend berichttype: {msg.MsgType} (genegeerd).");
                break;
        }
    }

    /// <summary>
    /// Verwerk een DNSLookup bericht
    /// </summary>
    private void HandleDnsLookupMessage(
        Message msg,
        IPEndPoint currentClientEP,
        ref bool sessionActive,
        ref int queriesHandled,
        int expectedQueries)
    {
        queriesHandled++;
        string? contentInfo = msg.Content != null ? msg.Content.ToString() : "(null)";
        Console.WriteLine($"Server: DNSLookup #{queriesHandled} (MsgId={msg.MsgId}) Content={contentInfo}");

        bool foundRecord = false;

        if (msg.Content is string nameOnly)
        {
            // Ontbrekend type in content
            Console.WriteLine("Server: DNSLookup content mist Type veld.");
            SendError(currentClientEP, "Domain not found");
        }
        else
        {
            try
            {
                // Verwerk content als DNSRecord (verwacht Type en Name)
                string contentJson = msg.Content?.ToString() ?? "{}";
                var queryRec = JsonSerializer.Deserialize<DNSRecord>(contentJson, jsonOptions);

                if (queryRec == null || string.IsNullOrEmpty(queryRec.Name) || string.IsNullOrEmpty(queryRec.Type))
                {
                    Console.WriteLine("Server: Onvolledige DNSLookup content (Type/Name ontbreekt).");
                    SendError(currentClientEP, "Domain not found");
                }
                else
                {
                    // Zoek het DNS record in onze lijst
                    DNSRecord? result = FindDnsRecord(queryRec);

                    if (result != null)
                    {
                        foundRecord = true;
                        Console.WriteLine($"Server: Record gevonden voor {queryRec.Name} ({queryRec.Type}). -> Verstuur DNSLookupReply.");

                        Message reply = new Message
                        {
                            MsgId = msg.MsgId, // zelfde MsgId als verzoek
                            MsgType = MessageType.DNSLookupReply,
                            Content = result
                        };
                        SendMessage(reply, currentClientEP);
                    }
                    else
                    {
                        Console.WriteLine($"Server: Geen record voor {queryRec.Name} ({queryRec.Type}). -> Verstuur Error.");
                        SendError(currentClientEP, "Domain not found");
                    }
                }
            }
            catch (JsonException ex)
            {
                Console.WriteLine($"Server: JSON fout bij verwerken DNSLookup content -> {ex.Message}");
                SendError(currentClientEP, "Invalid request format");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Server: Fout bij verwerken DNSLookup content -> {ex.Message}");
                SendError(currentClientEP, "Domain not found");
            }
        }

        // Als dit de laatste verwachte query was, beslis over het beëindigen van de sessie
        if (queriesHandled >= expectedQueries)
        {
            if (!foundRecord)
            {
                // Laatste query resulteerde in een fout (geen Ack verwacht), beëindig sessie nu
                Console.WriteLine("Server: Laatste query was ongeldig. Verstuur End naar client.");
                SendEndMessage(currentClientEP);

                // Reset sessie voor volgende client
                sessionActive = false;
                queriesHandled = 0;
                Console.WriteLine("Server: Sessie gesloten (na fout). Wachten op nieuwe client...");
            }
            // Als foundRecord true is, wacht op laatste Ack om End te versturen
        }
    }

    /// <summary>
    /// Zoek een DNS record op basis van een zoekopdracht
    /// </summary>
    /// <param name="queryRec">Het zoek record met Type en Name</param>
    /// <returns>Het gevonden DNS record of null</returns>
    private DNSRecord? FindDnsRecord(DNSRecord queryRec)
    {
        return dnsRecords.Find(r =>
            r.Name.Equals(queryRec.Name, StringComparison.OrdinalIgnoreCase) &&
            r.Type.Equals(queryRec.Type, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Verwerk een Ack bericht
    /// </summary>
    private void HandleAckMessage(
        Message msg,
        IPEndPoint currentClientEP,
        ref bool sessionActive,
        ref int queriesHandled,
        int expectedQueries)
    {
        Console.WriteLine($"Server: Ack ontvangen voor MsgId {msg.Content}.");

        if (queriesHandled >= expectedQueries)
        {
            // Alle queries verwerkt en de laatste is bevestigd
            Console.WriteLine("Server: Alle queries bevestigd. Verstuur End naar client.");
            SendEndMessage(currentClientEP);

            // Reset voor volgende client
            sessionActive = false;
            queriesHandled = 0;
            Console.WriteLine("Server: Sessie gesloten. Klaar voor volgende client.");
        }
    }

    /// <summary>
    /// Verstuur een End bericht naar de client
    /// </summary>
    /// <param name="endpoint">Client eindpunt</param>
    private void SendEndMessage(EndPoint endpoint)
    {
        Message endMsg = new Message
        {
            MsgId = rand.Next(10000, 99999),
            MsgType = MessageType.End,
            Content = "End of DNSLookup"
        };
        SendMessage(endMsg, endpoint);
    }

    /// <summary>
    /// Helper functie om een bericht via UDP te versturen
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

            Console.WriteLine($"Server: {message.MsgType} verstuurd naar {endpoint}: {json}");
        }
        catch (SocketException ex)
        {
            Console.WriteLine($"Server: Netwerkfout bij verzenden {message.MsgType}: {ex.Message}");
            // Gooi geen exceptie, log alleen de fout om de server actief te houden
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Server: Fout bij verzenden {message.MsgType}: {ex.Message}");
            // Gooi geen exceptie, log alleen de fout om de server actief te houden
        }
    }

    /// <summary>
    /// Helper functie om een foutbericht te versturen
    /// </summary>
    /// <param name="endpoint">Client eindpunt</param>
    /// <param name="errorContent">De foutmelding</param>
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

    /// <summary>
    /// Klasse voor het deserialiseren van instellingen uit JSON
    /// </summary>
    private class Settings
    {
        public string? ServerIP { get; set; }
        public int ServerPort { get; set; }
    }
}