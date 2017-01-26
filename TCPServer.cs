using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Net.Sockets;
using System.Net;
using System.Threading;
using System.Text.RegularExpressions;
using System.IO;
using System.Web;
using System.Xml;
using System.Xml.Serialization;

namespace OruxPals
{
    public class OruxPalsServer
    {
        public static string serviceName { get { return "OruxPals"; } }
        public static string softver { get { return "OruxPalsServer v0.2a"; } }

        private Hashtable clientList = new Hashtable();
        private Thread listenThread = null;
        private TcpListener mainListener = null;        
        private bool isRunning = false;        
        private ulong clientCounter = 0;
        private ulong mmtactCounter = 0;
        private Buddies BUDS = null;
        private DateTime started;

        private IPAddress ListenIP = IPAddress.Any;
        private int ListenPort = 12015;
        private ushort MaxClientAlive = 60;
        private byte maxHours = 48;
        private ushort greenMinutes = 60;
        private string urlPath = "/oruxpals/";
        private string adminName = "admin";

        public OruxPalsServer() 
        {
            OruxPalsServerConfig config = OruxPalsServerConfig.LoadFile("OruxPalsServer.xml");
            ListenPort = config.ListenPort;
            MaxClientAlive = config.maxClientAlive;
            maxHours = config.maxHours;
            greenMinutes = config.greenMinutes;
            if (config.urlPath.Length != 8) throw new Exception("urlPath must be 8 symbols length");
            adminName = config.adminName;
            urlPath = "/"+config.urlPath.ToLower()+"/";
        }        

        public bool Running { get { return isRunning; } }
        public IPAddress ServerIP { get { return ListenIP; } }
        public int ServerPort { get { return ListenPort; } set { ListenPort = value; } }

        public void Dispose() { Stop(); }
        ~OruxPalsServer() { Dispose(); }

        public void Start()
        {
            if (isRunning) return;
            started = DateTime.UtcNow;
            Console.Write("Starting {0} at {1}:{2}... ", softver, ListenIP, ListenPort);
            BUDS = new Buddies(maxHours, greenMinutes);
            BUDS.onBroadcast = new Buddies.BroadcastMethod(Broadcast);
            isRunning = true;
            listenThread = new Thread(MainThread);
            listenThread.Start();            
        }

        private void MainThread()
        {
            mainListener = new TcpListener(this.ListenIP, this.ListenPort);
            mainListener.Start();
            Console.WriteLine("OK");
            Console.WriteLine("Info at: http://127.0.0.1:{0}{1}i/",ListenPort, urlPath);
            while (isRunning)
            {
                try
                {
                    GetClient(mainListener.AcceptTcpClient());
                }
                catch { };
                Thread.Sleep(10);
            };
            Console.WriteLine("OK");
        }

        public void Stop()
        {
            if (!isRunning) return;

            Console.Write("Stopping... ");
            isRunning = false;
            BUDS.Dispose();
            BUDS = null;

            if (mainListener != null) mainListener.Stop();
            mainListener = null;

            listenThread.Join();
            listenThread = null;            
        }

        private void GetClient(TcpClient Client)
        {
            ClientData cd = new ClientData(new Thread(ClientThread), Client, ++clientCounter);
            lock(clientList) clientList.Add(cd.id, cd);
            cd.thread.Start(cd);            
        }

        private void ClientThread(object param)
        {            
            ClientData cd = (ClientData)param;            

            string rxText = "";
            byte[] rxBuffer = new byte[4096];
            int rxCount = 0;
            int rxAvailable = 0;
            int waitCounter = 30; // 3 sec
            
            while (Running && cd.thread.IsAlive && IsConnected(cd.client) && (DateTime.UtcNow.Subtract(cd.connected).TotalMinutes < MaxClientAlive))
            {
                try { rxAvailable = cd.client.Client.Available; }
                catch { break; };                

                // AIS Client
                if (cd.state == 1)
                {
                    Thread.Sleep(1000);
                    continue;                    
                };

                if (waitCounter-- == 0)
                {
                    cd.state = 1;
                    OnAISClient(cd);
                };

                while (rxAvailable > 0)
                {
                    try { rxAvailable -= (rxCount = cd.stream.Read(rxBuffer, 0, rxBuffer.Length > rxAvailable ? rxAvailable : rxBuffer.Length)); }
                    catch { break; };
                    if (rxCount > 0) rxText += Encoding.ASCII.GetString(rxBuffer, 0, rxCount);                    
                };

                // GPSGate or MapMyTracks Client
                if (rxText.Length >= 4)
                {
                    if (rxText.IndexOf("GET") == 0)
                        OnGet(cd, rxText);
                    else if (rxText.IndexOf("POST") == 0)
                        OnPost(cd, rxText);
                    break;
                };
                
                Thread.Sleep(100);
            };

            lock (clientList) clientList.Remove(cd.id);
            cd.client.Close();            
            cd.thread.Abort();
        }

        private void OnAISClient(ClientData cd)
        {            
            if (BUDS == null) return;

            Buddie[] bup = BUDS.Current;
            List<byte[]> blist = new List<byte[]>();
            foreach (Buddie b in bup) blist.Add(b.AISNMEA);
            foreach (byte[] ba in blist)
                try { cd.stream.Write(ba, 0, ba.Length); } catch { };
        }

        private void OnGet(ClientData cd, string rxText)
        {
            cd.state = 2;
            int hi = rxText.IndexOf("HTTP");
            if (hi <= 0) return;
            string query = rxText.Substring(4, hi - 4).Trim();
            if (!IsValidQuery(query))
            {
                HttpClientSendError(cd.client, 404);
                return;
            };
            switch(query[10])
            {
                case '$':
                    OnAdmin(cd, query.Substring(11));
                    return;
                case 'i':
                    OnBrowser(cd, query.Substring(11));
                    return;
                case '@':
                    OnCmd(cd, query.Substring(11));
                    return;
                //case 'm':
                //    OnMMT(cd, rxText);
                //    return;
                default:
                    HttpClientSendError(cd.client, 403);
                    return;

            };
        }

        private void OnAdmin(ClientData cd, string query)
        {
            string[] ss = query.Split(new char[] { '/' }, 2);
            if ((ss != null) && (ss[0] == adminName))
            {
                string resp = "<form action=\""+urlPath+"$"+ss[0]+"/\"><input type=\"text\" name=\"user\" maxlength=\"9\"/><input type=\"submit\"/></form>";
                if((ss.Length > 1) && (ss[1].Length > 8))
                {
                    string user = ss[1].Substring(6).ToUpper();
                    if(Buddie.BuddieNameRegex.IsMatch(user))
                        resp += user + ":" + Buddie.Hash(user);
                };
                HTTPClientSendResponse(cd.client, resp);
            }
            else
            {
                HttpClientSendError(cd.client, 403);
            };
        }

        private void OnBrowser(ClientData cd, string query)
        {
            query = query.Replace("/", "").ToUpper();
            string user = "user";

            string addit = "";
            if ((query.Length > 0) && (Buddie.BuddieNameRegex.IsMatch(query)))
            {
                Buddie[] all = BUDS.Current;
                foreach (Buddie b in all)
                    if (b.name == query)
                    {
                        user = b.name;
                        addit = String.Format("Information about: <b>{0}</b>\r\n<br/>", b.name);
                        addit += String.Format("Source: {0}\r\n<br/>", b.source == 1 ? "GPSGate" : "MapMyTracks");
                        addit += String.Format("Received: {0} UTC\r\n<br/>", b.last);
                        addit += String.Format("Valid till: {0} UTC\r\n<br/>", b.last.AddHours(MaxClientAlive));
                        addit += String.Format("Position: {0} {1}\r\n<br/>", b.lat, b.lon);
                        addit += String.Format("Speed: {0} kmph\r\n<br/>", b.speed);
                        addit += String.Format("Heading: {0}&deg;\r\n<br/>", b.course);
                        addit += String.Format("<a href=\"https://yandex.ru/maps/?text={1},{0}\" target=\"_blank\"><img src=\"http://static-maps.yandex.ru/1.x/?ll={0},{1}&size=500,300&z=13&l=map&pt={0},{1},vkbkm\"/></a>", b.lon.ToString(System.Globalization.CultureInfo.InvariantCulture), b.lat.ToString(System.Globalization.CultureInfo.InvariantCulture));
                        addit += String.Format("<a href=\"https://yandex.ru/maps/?text={1},{0}\" target=\"_blank\"><img src=\"http://static-maps.yandex.ru/1.x/?ll={0},{1}&size=500,300&z=15&l=map&pt={0},{1},vkbkm\"/></a>", b.lon.ToString(System.Globalization.CultureInfo.InvariantCulture), b.lat.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    };
            };

            int cc = 0;
            lock (clientList)
                foreach (ClientData ci in clientList.Values)
                    if (ci.state == 1) cc++;
            int bc = 0;
            string allbds = "";
            if (BUDS != null)
            {
                Buddie[] all = BUDS.Current;
                bc = all.Length;
                foreach (Buddie b in all) allbds += "<a href=\"" + urlPath + "i/" + b.name + "\">" + b.name + "</a> ";
            };
            HTTPClientSendResponse(cd.client, String.Format(
                "Server: {0}\r\n<br/>" +
                "Port: {4}\r\n<br/>" +
                "Started {1} UTC\r\n<br/>" +
                "AIS Clients: {2}\r\n<br/>" +
                "Buddies: {3} {6} \r\n<br/>" +
                "AIS URL: 127.0.0.1:{4}\r\n<br/>" +
                "GPSGate URL: http://127.0.0.1:{4}{5}@"+user+"/\r\n<br/>" +
                "MapMyTracks URL: http://127.0.0.1:{4}{5}m/\r\n<br/>\r\n<br/>"+
                addit,
                new object[] { 
                softver, 
                started, 
                cc, 
                bc,
                ListenPort,
                urlPath,
                allbds,
                addit}));
        }

        private void OnCmd(ClientData cd, string query)
        {
            int s2f = query.IndexOf("/?cmd=");
            if (s2f < 3) return;
            string user = query.Substring(0, s2f).ToUpper();
            if (!Buddie.BuddieNameRegex.IsMatch(user)) { HttpClientSendError(cd.client, 417); };
            string cmd = query.Substring(s2f + 6);

            string[] pData = cmd.Split(new string[] { "," }, StringSplitOptions.None);
            if (pData.Length < 13) { HttpClientSendError(cd.client, 417); };
            if (pData[2] != "_SendMessage") { HttpClientSendError(cd.client, 417); };
            int pass = 0;
            if (!int.TryParse(pData[1], out pass)) { HttpClientSendError(cd.client, 417); };
            if (Buddie.Hash(user) != pass) { HttpClientSendError(cd.client, 403); };

            // PARSE //
            try
            {
                double lat = double.Parse(pData[4].Substring(2, 7), System.Globalization.CultureInfo.InvariantCulture);
                lat = double.Parse(pData[4].Substring(0, 2), System.Globalization.CultureInfo.InvariantCulture) + lat / 60;
                if (pData[5] == "S") lat *= -1;

                double lon = 0;
                if (pData[6].IndexOf(".") > 4)
                {
                    lon = double.Parse(pData[6].Substring(3, 7), System.Globalization.CultureInfo.InvariantCulture);
                    lon = double.Parse(pData[6].Substring(0, 3), System.Globalization.CultureInfo.InvariantCulture) + lon / 60;
                }
                else
                {
                    lon = double.Parse(pData[6].Substring(2, 7), System.Globalization.CultureInfo.InvariantCulture);
                    lon = double.Parse(pData[6].Substring(0, 2), System.Globalization.CultureInfo.InvariantCulture) + lon / 60;
                };
                if (pData[7] == "W") lon *= -1;

                // double alt = double.Parse(pData[8], System.Globalization.CultureInfo.InvariantCulture);
                double speed = double.Parse(pData[9], System.Globalization.CultureInfo.InvariantCulture) * 1.852;
                double heading = double.Parse(pData[10], System.Globalization.CultureInfo.InvariantCulture);

                HTTPClientSendResponse(cd.client, "accepted");
                cd.client.Close();

                Buddie b = new Buddie(1, user, lat, lon, (short)speed, (short)heading);
                OnNewData(b);
            }
            catch { HttpClientSendError(cd.client, 417); };
        }

        private void OnPost(ClientData cd, string rxText)
        {
            cd.state = 3;
            int hi = rxText.IndexOf("HTTP");
            if (hi <= 0) return;
            string query = rxText.Substring(5, hi - 5).Trim();
            if (!IsValidQuery(query))
            {
                HttpClientSendError(cd.client, 404);
                return;
            };
            if (query[10] == 'm')
            {
                OnMMT(cd, rxText);
                return;
            };
            HttpClientSendError(cd.client, 417);
        }

        private void OnMMT(ClientData cd, string rxText)
        {
            // Authorization Required //
            string user = "";
            int pass = 0;
            
            try
            {
                int aut = rxText.IndexOf("Authorization: Basic");
                if (aut < 0)
                {
                    SendAuthReq(cd.client);
                    return;
                }
                else
                {
                    aut += 21;
                    string cup = rxText.Substring(aut, rxText.IndexOf("\r\n", aut) - aut).Trim();
                    string dup = Encoding.UTF8.GetString(Convert.FromBase64String(cup));
                    string[] up = dup.Split(new char[] { ':' }, 2);
                    if ((up == null) || (up.Length != 2)) { SendAuthReq(cd.client); return; };
                    user = up[0].ToUpper();
                    if (!Buddie.BuddieNameRegex.IsMatch(user)) { SendAuthReq(cd.client); return; };                    
                    if (!int.TryParse(up[1], out pass)) { SendAuthReq(cd.client); return; };                    
                };
            }
            catch { SendAuthReq(cd.client); return; };

            if (Buddie.Hash(user) != pass)
            {
                HTTPClientSendResponse(cd.client, "<?xml version=\"1.0\" encoding=\"UTF-8\"?><message><type>error</type><reason>unauthorised</reason></message>");
                return;
            };

            System.Collections.Specialized.NameValueCollection ask = null;
            int db = rxText.IndexOf("\r\n\r\n");
            if (db > 0)
                ask = HttpUtility.ParseQueryString(rxText.Substring(db + 4));
            else
            {
                db = rxText.IndexOf("\n\n");
                if (db > 0)
                    ask = HttpUtility.ParseQueryString(rxText.Substring(db + 2));
                else
                { HttpClientSendError(cd.client, 415); return; };
            };

            if ((ask["request"] == "start_activity") || (ask["request"] == "update_activity"))
            {
                HTTPClientSendResponse(cd.client, "<?xml version=\"1.0\" encoding=\"UTF-8\"?><message><type>activity_started</type><activity_id>" + (++mmtactCounter).ToString() + "</activity_id></message>");

                string points = ask["points"];
                if ((points != null) && (points != String.Empty))
                {
                    string[] pp = points.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if ((pp.Length > 3) && ((pp.Length % 4) == 0))
                    {
                        double lat = 0;
                        double lon = 0;
                        //double alt = 0;
                        //DateTime DT = DateTime.MinValue;
                        for (int i = 0; i < pp.Length; i += 4)
                        {
                            lat = double.Parse(pp[i + 0], System.Globalization.CultureInfo.InvariantCulture);
                            lon = double.Parse(pp[i + 1], System.Globalization.CultureInfo.InvariantCulture);
                            //double alt = double.Parse(pp[i + 2], System.Globalization.CultureInfo.InvariantCulture);
                            //DT = UnixTimeStampToDateTime(double.Parse(pp[i + 3], System.Globalization.CultureInfo.InvariantCulture));                            
                        };
                        Buddie b = new Buddie(2, user, lat, lon, 0, 0);
                        OnNewData(b);
                    };
                };
                return;
            };
            if (ask["request"] == "stop_activity")
            {
                HTTPClientSendResponse(cd.client, "<?xml version=\"1.0\" encoding=\"UTF-8\"?><message><type>activity_stopped</type></message>");
                return;
            };
            if (ask["request"] == "get_time")
            {
                HTTPClientSendResponse(cd.client, "<?xml version=\"1.0\" encoding=\"UTF-8\"?><message><type>time</type><server_time>" + ((long)DateTimeToUnixTimestamp(DateTime.UtcNow)).ToString() + "</server_time></message>");
                return;
            };

            HTTPClientSendResponse(cd.client, "<?xml version=\"1.0\" encoding=\"UTF-8\"?><message><type>error</type><reason>request not supported </reason></message>");
            return;
        }

        private void OnNewData(Buddie buddie)
        {
            if (BUDS != null)
                BUDS.Update(buddie);
        }

        public void Broadcast(byte[] data)
        {
            List<ClientData> cdlist = new List<ClientData>();
            lock(clientList)
                foreach (object obj in clientList.Values)
                {
                    if (obj == null) continue;
                    ClientData cd = (ClientData)obj;
                    if (cd.state == 1)
                        cdlist.Add(cd);
                };

            foreach(ClientData cd in cdlist)
                try{ cd.client.GetStream().Write(data,0,data.Length); } catch {};
        }

        private bool IsValidQuery(string query)
        {
            if (query.Length < 11) return false;
            string subQuery = query.Substring(0, 10).ToLower();
            if (subQuery != urlPath) return false;
            return true;
        }

        private static bool IsConnected(TcpClient Client)
        {
            if (!Client.Connected) return false;
            if (Client.Client.Poll(0, SelectMode.SelectRead))
            {
                byte[] buff = new byte[1];
                try
                {
                    if (Client.Client.Receive(buff, SocketFlags.Peek) == 0)
                        return false;
                }
                catch
                {
                    return false;
                };
            };
            return true;
        }

        private static void SendAuthReq(TcpClient Client)
        {
            string Str =
                        "HTTP/1.1 401 Unauthorized\r\n" +
                        "Date: " + DateTime.UtcNow.ToString("dd.MM.yyyy HH:mm:ss") + " GMT\r\n" +
                        "WWW-Authenticate: Basic realm=\"Map My Tracks API\"\r\n" +
                        "Vary: Accept-Encoding\r\nContent-Length: 12\r\n" +
                        "Server: " + softver + "\r\n" +
                        "Connection: close\r\n" +
                        "Content-Type: text/html\r\n\r\n" +
                        "Unauthorized";
            byte[] Buffer = Encoding.ASCII.GetBytes(Str);
            try { Client.GetStream().Write(Buffer, 0, Buffer.Length); } catch { }
            Client.Close();
        }

        private static void HTTPClientSendResponse(TcpClient Client, string text)
        {
            string Headers = 
                "HTTP/1.1 200 OK\r\n"+
                "Server: " + softver + "\r\n" +
                "Connection: close\r\n" +
                "Content-Type: text/html\r\n"+
                "Content-Length: " + text.Length + "\r\n\r\n";
            byte[] Buffer = Encoding.ASCII.GetBytes(Headers + text);
            try { Client.GetStream().Write(Buffer, 0, Buffer.Length); } catch { }
            Client.Close();
        }

        private static void HttpClientSendError(TcpClient Client, int Code)
        {
            string CodeStr = Code.ToString() + " " + ((HttpStatusCode)Code).ToString();
            string Html = "<html><body><h1>" + CodeStr + "</h1></body></html>";
            string Str = 
                "HTTP/1.1 " + CodeStr + "\r\n"+
                "Server: "+softver+"\r\n"+
                "Connection: close\r\n" +
                "Content-type: text/html\r\n"+
                "Content-Length:" + Html.Length.ToString() + "\r\n\r\n" + 
                Html;
            byte[] Buffer = Encoding.ASCII.GetBytes(Str);
            try { Client.GetStream().Write(Buffer, 0, Buffer.Length); } catch { }
            Client.Close();
        }

        private static double DateTimeToUnixTimestamp(DateTime dateTime)
        {
            return (dateTime - new DateTime(1970, 1, 1, 0, 0, 0, 0, System.DateTimeKind.Utc)).TotalSeconds;
        }

        private static DateTime UnixTimeStampToDateTime(double unixTimeStamp)
        {
            System.DateTime dtDateTime = new DateTime(1970, 1, 1, 0, 0, 0, 0, System.DateTimeKind.Utc);
            dtDateTime = dtDateTime.AddSeconds(unixTimeStamp).ToLocalTime();
            return dtDateTime;
        }

        private class ClientData
        {
            public byte state; // 0 - undefined; 1 - listen; 2 - gpsgate; 3 - mapmytracks
            public Thread thread;
            public TcpClient client;
            public DateTime connected;
            public ulong id;
            public Stream stream;            

            public ClientData(Thread thread, TcpClient client, ulong clientID)
            {
                this.id = clientID;
                this.connected = DateTime.UtcNow;
                this.state = 0;
                this.thread = thread;
                this.client = client;
                this.stream = client.GetStream();
            }
        }
    }

    [Serializable]
    public class OruxPalsServerConfig
    {
        public int ListenPort = 12015;
        public ushort maxClientAlive = 60;
        public byte maxHours = 48;
        public ushort greenMinutes = 60;
        public string urlPath = "oruxpals";
        public string adminName = "ADMIN";

        public static OruxPalsServerConfig LoadFile(string file)
        {
            System.Xml.Serialization.XmlSerializer xs = new System.Xml.Serialization.XmlSerializer(typeof(OruxPalsServerConfig));
            System.IO.StreamReader reader = System.IO.File.OpenText(GetCurrentDir() + @"\" + file);
            OruxPalsServerConfig c = (OruxPalsServerConfig)xs.Deserialize(reader);
            reader.Close();
            return c;
        }

        private static string GetCurrentDir()
        {
            string fname = System.Reflection.Assembly.GetExecutingAssembly().GetName().CodeBase.ToString();
            fname = fname.Replace("file:///", "");
            fname = fname.Replace("/", @"\");
            fname = fname.Substring(0, fname.LastIndexOf(@"\") + 1);
            return fname;
        }
    }
}
