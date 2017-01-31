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
        public static string softver { get { return "OruxPalsServer v0.3a"; } }

        private OruxPalsServerConfig.FRNUser[] frnusers;
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
            if (config.users != null) frnusers = config.users.users;
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
            BUDS.onBroadcastAIS = new Buddies.BroadcastMethod(BroadcastAIS);
            BUDS.onBroadcastAPRS = new Buddies.BroadcastMethod(BroadcastAPRS);
            isRunning = true;
            listenThread = new Thread(MainThread);
            listenThread.Start();            
        }

        private void MainThread()
        {
            mainListener = new TcpListener(this.ListenIP, this.ListenPort);
            mainListener.Start();
            Console.WriteLine("OK");
            Console.WriteLine("Info at: http://127.0.0.1:{0}{1}info",ListenPort, urlPath);
            Console.WriteLine("Admin at: http://127.0.0.1:{0}{1}${2}", ListenPort, urlPath, adminName);
            (new Thread(PingThread)).Start(); // ping clients thread
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

        private void PingThread()
        {
            ushort pingInterval = 0;
            while (isRunning)
            {
                if (pingInterval++ == 300) // 30 sec
                {
                    pingInterval = 0;
                    try { PingAlive(); }
                    catch { };
                };
                Thread.Sleep(100);
            };
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

        private void PingAlive()
        {
            string pingmsg = "# " + softver + "\r\n";
            byte[] pingdata = Encoding.ASCII.GetBytes(pingmsg);
            Broadcast(pingdata, true, true);
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

                if ((cd.state == 0) && (waitCounter-- == 0))
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

                // READ INCOMING DATA
                try 
                {
                    // GPSGate, MapMyTracks or APRS Client //
                    if ((cd.state == 0) && (rxText.Length >= 4))
                    {
                        if (rxText.IndexOf("GET") == 0)
                            OnGet(cd, rxText);
                        else if (rxText.IndexOf("POST") == 0)
                            OnPost(cd, rxText);
                        else if (rxText.IndexOf("user") == 0)
                        {
                            if (OnAPRSClient(cd, rxText.Replace("\r", "").Replace("\n", "")))
                                rxText = "";
                            else
                                break;
                        }
                        else if (rxText.IndexOf("$FRPAIR") == 0)
                        {
                            if (OnFRNClient(cd, rxText.Replace("\r", "").Replace("\n", "")))
                                rxText = "";
                            else
                                break;
                        }
                        else
                            break;
                    };

                    // APRS Client //
                    if ((cd.state == 4) && (rxText.Length > 0))
                    {
                        string[] lines = rxText.Split(new string[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                        rxText = "";
                        foreach (string line in lines)
                            OnAPRSData(cd, line);
                    };

                    // FRS Client //
                    if ((cd.state == 5) && (rxText.Length > 0))
                    {
                        string[] lines = rxText.Split(new string[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                        rxText = "";
                        foreach (string line in lines)
                            OnFRNData(cd, line);
                    };
                }
                catch { };
                
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

        private bool OnAPRSClient(ClientData cd, string loginstring)
        {
            string res = "# logresp user unverified";

            Match rm = Regex.Match(loginstring, @"^user\s([\w\-]{3,})\spass\s([\d\-]+)\svers\s([\w\d\-.]+)\s([\w\d\-.\+]+)");
            if (rm.Success)
            {
                string callsign = rm.Groups[1].Value.ToUpper();
                string password = rm.Groups[2].Value;
                //string software = rm.Groups[3].Value;
                //string version = rm.Groups[4].Value;
                string doptext = loginstring.Substring(rm.Groups[0].Value.Length).Trim();

                int psw = -1;
                int.TryParse(password, out psw);
                if (psw == APRSData.CallsignChecksum(callsign))
                {
                    cd.state = 4; //APRS
                    res = "# logresp " + callsign + " verified, filter is not supported";
                    byte[] ret = Encoding.ASCII.GetBytes(res + "\r\n");
                    try { cd.stream.Write(ret, 0, ret.Length); }
                    catch { };

                    if (BUDS != null)
                    {
                        Buddie[] bup = BUDS.Current;
                        List<byte[]> blist = new List<byte[]>();
                        foreach (Buddie b in bup) blist.Add(b.APRSData);
                        foreach (byte[] ba in blist)
                            try { cd.stream.Write(ba, 0, ba.Length); }
                            catch { };
                    }

                    return true;
                };
            };

            // invalid user
            {
                byte[] ret = Encoding.ASCII.GetBytes(res + "\r\n");
                try { cd.stream.Write(ret, 0, ret.Length); }
                catch { };
                cd.client.Close();
                return false;
            };            
        }

        private void OnAPRSData(ClientData cd, string line)
        {
            if (line.IndexOf("#") == 0) return;
            if (line.IndexOf(">") < 0) return;

            try { if (line.IndexOf("::") > 0) onAPRStypeMessage(cd, line); } catch { };
            
            Buddie b = APRSData.ParseAPRSPacket(line);
            if ((b != null) && (b.name != null) && (b.name != String.Empty) && (b.lat != 0) && (b.lon != 0))
            {
                // remove ssid
                if (b.name.Contains("-")) b.name = b.name.Substring(0, b.name.IndexOf("-"));
                OnNewData(b);
            };
        }

        private bool OnFRNClient(ClientData cd, string pairstring)
        {
            byte[] ba = Encoding.ASCII.GetBytes("# " + softver);
            try { cd.stream.Write(ba, 0, ba.Length); } catch { };

            Match rx;
            if ((rx = Regex.Match(pairstring, @"^(\$FRPAIR),([\w\+]+),(\w+)\*(\w+)$")).Success)
            {
                string phone = rx.Groups[2].Value;
                //string imei = rx.Groups[3].Value;                
                if(frnusers != null) 
                    foreach(OruxPalsServerConfig.FRNUser u in frnusers)
                        if (u.phone == phone)
                        {
                            cd.state = 5;
                            cd.AdditData = u.name;
                            return true;
                        };
            };
            return false;
        }

        private void OnFRNData(ClientData cd, string line)
        {
            Match rx;

            if ((rx = Regex.Match(line, @"^(\$FRCMD),(\w*),(\w+),(\w*),?([\w\s.,=]*)\*(\w{2})$")).Success)
            {
                string resp = "";

                // _ping
                if (rx.Groups[3].Value.ToLower() == "_ping")
                   resp = ChecksumAdd2Line("$FRRET," + rx.Groups[2].Value + ",_Ping,Inline");
                
                // _sendmessage
                if (rx.Groups[3].Value.ToLower() == "_sendmessage")
                {
                   string val = rx.Groups[4].Value;
                   string val2 = rx.Groups[5].Value;
                   resp = ChecksumAdd2Line("$FRRET," + rx.Groups[2].Value + ",_SendMessage,Inline");
                   
                   // 0000.00000,N,00000.00000,E,0.0,0.000,0.0,190117,122708.837,0,BatteryLevel=78
                   // DDMM.mmmm,N,DDMM.mmmm,E,AA.a,SSS.ss,HHH.h,DDMMYY,hhmmss.dd,fixOk,NOTE*xx

                   Match rxa = Regex.Match(val2, @"^(\d{4}.\d+),(N|S),(\d{5}.\d+),(E|W),([0-9.]*),([0-9.]*),([0-9.]*),(\d{6}),([0-9.]{6,}),([\w.\s=]),([\w.\s=,]*)$");
                   if (rxa.Success)
                   {
                       string sFix = sFix = rxa.Groups[10].Value;
                       if (sFix == "1")
                       {
                           string sLat = rxa.Groups[1].Value;
                           string lLat = rxa.Groups[2].Value;
                           string sLon = rxa.Groups[3].Value;
                           string lLon = rxa.Groups[4].Value;
                           string sSpeed = rxa.Groups[6].Value;
                           string sHeading = rxa.Groups[7].Value;

                           double rLat = double.Parse(sLat.Substring(2, 7), System.Globalization.CultureInfo.InvariantCulture);
                           rLat = double.Parse(sLat.Substring(0, 2), System.Globalization.CultureInfo.InvariantCulture) + rLat / 60;
                           if (lLat == "S") rLat *= -1;

                           double rLon = double.Parse(sLon.Substring(3, 7), System.Globalization.CultureInfo.InvariantCulture);
                           rLon = double.Parse(sLon.Substring(0, 3), System.Globalization.CultureInfo.InvariantCulture) + rLon / 60;
                           if (lLon == "W") rLon *= -1;


                           double rHeading = double.Parse(sHeading, System.Globalization.CultureInfo.InvariantCulture);
                           double rSpeed = double.Parse(sSpeed, System.Globalization.CultureInfo.InvariantCulture) * 1.852;

                           Buddie b = new Buddie(4, (string)cd.AdditData, rLat, rLon, (short)rSpeed, (short)rHeading);
                           OnNewData(b);
                       };
                   };
               };

               if (resp != "")
               {
                   byte[] ba = Encoding.ASCII.GetBytes(resp + "\r\n");
                   try { cd.stream.Write(ba, 0, ba.Length); }
                   catch { };
               };
            };
        }

        private static string ChecksumHex(string str)
        {
            int checksum = 0;
            for (int i = 1; i < str.Length; i++)
                checksum ^= Convert.ToByte(str[i]);
            return checksum.ToString("X2");
        }

        private static string ChecksumAdd2Line(string line)
        {
            return line + "*" + ChecksumHex(line);
        }

        // Receive incoming messages from APRS client
        private void onAPRStypeMessage(ClientData cd, string line)
        {
            string frm = line.Substring(0, line.IndexOf(">"));
            string msg = line.Substring(line.IndexOf("::") + 12).Trim();

            bool sendack = false;
            byte[] tosendack = new byte[0];

            if (msg.Contains("{"))
            {
                string cmd2s = "ORXPLS-GW>APRS,TCPIP*::" + frm + ": ack" + msg.Substring(msg.IndexOf("{") + 1) + "\r\n";
                msg = msg.Substring(0, msg.IndexOf("{")).Trim();
                tosendack = Encoding.ASCII.GetBytes(cmd2s);                
            };

            if (line.IndexOf("::ORXPLS-GW:") > 0) // ping 
                sendack = true;                

            if (sendack && (tosendack.Length > 0))
                try { cd.stream.Write(tosendack, 0, tosendack.Length); } catch { };
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
                case 'v':
                    OnView(cd, query.Substring(11));
                    return;
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
                string resp = "<small><a href=\"" + urlPath + "$" + ss[0] + "\">Main admin page</a> | <a href=\"" + urlPath + "$" + ss[0] + "/?clear\">Clear Buddies List</a></small><br/><br/>";
                resp += "<form action=\"" + urlPath + "$" + ss[0] + "/\"><input type=\"text\" name=\"user\" maxlength=\"9\"/><input type=\"submit\"/></form>";
                if((ss.Length > 1) && (ss[1].Length > 8))
                {
                    string user = ss[1].Substring(6).ToUpper();
                    if(Buddie.BuddieNameRegex.IsMatch(user))
                        resp += user + ":" + Buddie.Hash(user);
                }
                else if ((ss.Length > 1) && (ss[1] == "?clear"))
                    if (BUDS != null)
                    {
                        BUDS.Clear();
                        resp += "Buddies List is Empty";
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
                        string src = "Unknown";
                        if (b.source == 1) src = "OruxMaps GPSGate";
                        if (b.source == 2) src = "OruxMaps MapMyTracks";
                        if (b.source == 3) src = "APRS Client";
                        if (b.source == 4) src = "FRS (GPSGate Tracker)";
                        user = b.name;
                        addit = String.Format("Information about: <b>{0}</b>\r\n<br/>", b.name);
                        addit += String.Format("Source: {0}\r\n<br/>", src);
                        addit += String.Format("Received: {0} UTC\r\n<br/>", b.last);
                        addit += String.Format("Valid till: {0} UTC\r\n<br/>", b.last.AddHours(MaxClientAlive));
                        addit += String.Format("Position: {0} {1}\r\n<br/>", b.lat, b.lon);
                        addit += String.Format("Speed: {0} kmph\r\n<br/>", b.speed);
                        addit += String.Format("Heading: {0}&deg;\r\n<br/>", b.course);
                        addit += String.Format("<a href=\"https://yandex.ru/maps/?text={1},{0}\" target=\"_blank\"><img src=\"http://static-maps.yandex.ru/1.x/?ll={0},{1}&size=500,300&z=13&l=map&pt={0},{1},vkbkm\"/></a>", b.lon.ToString(System.Globalization.CultureInfo.InvariantCulture), b.lat.ToString(System.Globalization.CultureInfo.InvariantCulture));
                        addit += String.Format("<a href=\"https://yandex.ru/maps/?text={1},{0}\" target=\"_blank\"><img src=\"http://static-maps.yandex.ru/1.x/?ll={0},{1}&size=500,300&z=15&l=map&pt={0},{1},vkbkm\"/></a>", b.lon.ToString(System.Globalization.CultureInfo.InvariantCulture), b.lat.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    };
            };

            int cAIS = 0;
            int cAPRS = 0;
            int cFRS = 0;
            lock (clientList)
                foreach (ClientData ci in clientList.Values)
                {
                    if (ci.state == 1) cAIS++;
                    if (ci.state == 4) cAPRS++;
                    if (ci.state == 5) cFRS++;
                };
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
                "APRS Clients: {7}\r\n<br/>" +
                "FRS Clients: {8}\r\n<br/>" +
                "Buddies: {3} {6} \r\n<br/>" +                
                "APRS URL: 127.0.0.1:{4}\r\n<br/>" +
                "FRS URL (GPSGate Tracker): 127.0.0.1:{4}\r\n<br/>" +
                "OruxMaps AIS URL: 127.0.0.1:{4}\r\n<br/>" +
                "OruxMaps GPSGate URL: http://127.0.0.1:{4}{5}@"+user+"/\r\n<br/>" +
                "OruxMaps MapMyTracks URL: http://127.0.0.1:{4}{5}m/\r\n<br/>" +
                "<a href=\"{5}view\">View Online Map</a>\r\n<br/>\r\n<br/>" +
                addit,
                new object[] { 
                softver, 
                started, 
                cAIS, 
                bc,
                ListenPort,
                urlPath,
                allbds,
                cAPRS,
                cFRS}));
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

        private void OnView(ClientData cd, string query)
        {
            string[] ss = query.Split(new char[] { '/' }, 2);
            string prf = ss[0];
            string ptf = "";
            if (ss.Length > 1) ptf = ss[1];

            if (prf == "list")
            {
                string cdata = "";
                if (BUDS != null)
                {
                    Buddie[] bs = BUDS.Current;
                    foreach (Buddie b in bs)
                    {
                        string src = "Unknown";
                        if (b.source == 1) src = "OruxMaps GPSGate";
                        if (b.source == 2) src = "OruxMaps MapMyTracks";
                        if (b.source == 3) src = "APRS Client";
                        if (b.source == 4) src = "FRS (GPSGate Tracker)";
                        cdata += (cdata.Length > 0 ? "," : "") + "{" + String.Format("user:'{0}',received:'{1}',lat:{2},lon:{3},speed:{4},hdg:{5},source:'{6}'",
                            new object[] { b.name, b.last, b.lat.ToString(System.Globalization.CultureInfo.InvariantCulture), b.lon.ToString(System.Globalization.CultureInfo.InvariantCulture), b.speed, b.course, src }) + "}";
                    };
                };
                cdata = "["+cdata+"]";
                HTTPClientSendResponse(cd.client, cdata);
            }
            else
            {
                if (ptf == "")
                    HTTPClientSendFile(cd.client, "map.html");
                else
                    HTTPClientSendFile(cd.client, ptf);
            };
        }

        private void OnNewData(Buddie buddie)
        {
            if (BUDS != null)
                BUDS.Update(buddie);
        }

        public void BroadcastAIS(byte[] data)
        {
            Broadcast(data, true, false);
        }

        public void BroadcastAPRS(byte[] data)
        {
            Broadcast(data, false, true);
        }

        public void Broadcast(byte[] data, bool bAIS, bool bAPRS)
        {
            List<ClientData> cdlist = new List<ClientData>();
            lock (clientList)
                foreach (object obj in clientList.Values)
                {
                    if (obj == null) continue;
                    ClientData cd = (ClientData)obj;
                    if (((cd.state == 1) && bAIS) || ((cd.state == 4) && bAPRS))
                        cdlist.Add(cd);
                };

            foreach (ClientData cd in cdlist)
                try { cd.client.GetStream().Write(data, 0, data.Length); }
                catch { };
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

        private static void HTTPClientSendFile(TcpClient Client, string fileName)
        {
            string ffn = OruxPalsServerConfig.GetCurrentDir() + @"\MAP\" + fileName;
            if (!File.Exists(ffn))
            {
                HttpClientSendError(Client, 404);
                return;
            };

            string ctype = "text/html; charset=utf-8";
            System.IO.FileStream fs = new FileStream(ffn, FileMode.Open, FileAccess.Read);

            string Headers =
                "HTTP/1.1 200 OK\r\n" +
                "Server: " + softver + "\r\n" +
                "Connection: close\r\n" +
                "Content-Type: " + ctype + "\r\n" +
                "Content-Length: " + fs.Length + "\r\n\r\n";
            byte[] Buffer = new byte[8192];
            try {
                Buffer = Encoding.ASCII.GetBytes(Headers);
                Client.GetStream().Write(Buffer, 0, Buffer.Length);
                int btr = (int)(fs.Length - fs.Position);
                while (btr > 0)
                {
                    int rdd = fs.Read(Buffer, 0, Buffer.Length > btr ? btr : Buffer.Length);
                    Client.GetStream().Write(Buffer, 0, rdd);
                    btr -= rdd;
                };                
            }
            catch { }
            fs.Close();
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
            public byte state; // 0 - undefined; 1 - listen (AIS); 2 - gpsgate; 3 - mapmytracks; 4 - APRS; 5 - FRS (GPSGate by TCP)
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

            public object AdditData;
        }
    }

    [Serializable]
    public class OruxPalsServerConfig
    {
        public class FRNUsers
        {            
            [XmlElement("u")]
            public FRNUser[] users;
        }

        public class FRNUser
        {
            [XmlAttribute]
            public string name;
            [XmlAttribute]
            public string phone;
        }

        public int ListenPort = 12015;
        public ushort maxClientAlive = 60;
        public byte maxHours = 48;
        public ushort greenMinutes = 60;
        public string urlPath = "oruxpals";
        public string adminName = "ADMIN";
        [XmlElement("users")]
        public FRNUsers users;

        public static OruxPalsServerConfig LoadFile(string file)
        {
            System.Xml.Serialization.XmlSerializer xs = new System.Xml.Serialization.XmlSerializer(typeof(OruxPalsServerConfig));
            System.IO.StreamReader reader = System.IO.File.OpenText(GetCurrentDir() + @"\" + file);
            OruxPalsServerConfig c = (OruxPalsServerConfig)xs.Deserialize(reader);
            reader.Close();
            return c;
        }

        public static string GetCurrentDir()
        {
            string fname = System.Reflection.Assembly.GetExecutingAssembly().GetName().CodeBase.ToString();
            fname = fname.Replace("file:///", "");
            fname = fname.Replace("/", @"\");
            fname = fname.Substring(0, fname.LastIndexOf(@"\") + 1);
            return fname;
        }
    }
}
