#define _DEBUGORUX

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
        public static string serviceName { get { return "OruxPalsServer"; } }
        public string ServerName = "OruxPalsServer";
        public static string softver { get { return "OruxPalsServer v0.14a"; } }

        private static bool _NoSendToFRS = true; // GPSGate Tracker didn't support $FRPOS

        private OruxPalsServerConfig.RegUser[] regUsers;
        private Hashtable clientList = new Hashtable();
        private Thread listenThread = null;
        private TcpListener mainListener = null;        
        private bool isRunning = false;        
        private ulong clientCounter = 0;
        private ulong mmtactCounter = 0;
        private Buddies BUDS = null;
        private DateTime started;

        private APRSISConfig aprscfg;
        private APRSISGateWay aprsgw;

        private OruxPalsServerConfig.FwdSvc[] forwardServices;

        private IPAddress ListenIP = IPAddress.Any;
        private int ListenPort = 12015;
        private ushort MaxClientAlive = 60;
        private byte maxHours = 48;
        private ushort greenMinutes = 60;
        private int KMLObjectsRadius = 5;
        private int KMLObjectsLimit = 50;
        private string urlPath = "/oruxpals/";
        private string adminName = "admin";
        private bool sendBack = false;
        private bool callsignToUser = true;
        private string infoIP = "127.0.0.1";
        private List<string> banlist = new List<string>();

        public static void InitCPU()
        {
            string path = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            if (IntPtr.Size == 8) // or: if(Environment.Is64BitProcess) // .NET 4.0
            {
                File.Copy(Path.Combine(path, "x64") + @"\System.Data.SQLite.dll", path + @"\System.Data.SQLite.dll", true);
                File.Copy(Path.Combine(path, "x64") + @"\SQLite.Interop.dll", path + @"\SQLite.Interop.dll", true);
            }
            else
            {
                File.Copy(Path.Combine(path, "x86") + @"\System.Data.SQLite.dll", path + @"\System.Data.SQLite.dll", true);
                File.Copy(Path.Combine(path, "x86") + @"\SQLite.Interop.dll", path + @"\SQLite.Interop.dll", true);
            };
        }

        public OruxPalsServer() 
        {
            //InitCPU();
                       
            OruxPalsServerConfig config = OruxPalsServerConfig.LoadFile("OruxPalsServer.xml");
            ListenPort = config.ListenPort;
            MaxClientAlive = config.maxClientAlive;
            maxHours = config.maxHours;
            greenMinutes = config.greenMinutes;
            KMLObjectsRadius = config.KMLObjectsRadius;
            KMLObjectsLimit = config.KMLObjectsLimit;
            if (config.urlPath.Length != 8) throw new Exception("urlPath must be 8 symbols length");
            adminName = config.adminName;
            sendBack = config.sendBack == "yes";
            callsignToUser = config.callsignToUser == "yes";
            infoIP = config.infoIP;
            urlPath = "/"+config.urlPath.ToLower()+"/";
            if (config.users != null) regUsers = config.users.users;
            aprscfg = config.aprsis;
            forwardServices = config.forwardServices.services;
            ServerName = config.ServerName;
            banlist.AddRange(config.banlist.ToUpper().Split(new char[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries));
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
            Console.Write("Starting {0} at {1}:{2}... ", softver, infoIP, ListenPort);
            BUDS = new Buddies(maxHours, greenMinutes, KMLObjectsRadius, KMLObjectsLimit);
            BUDS.onBroadcastAIS = new Buddies.BroadcastMethod(BroadcastAIS);
            BUDS.onBroadcastAPRS = new Buddies.BroadcastMethod(BroadcastAPRS);
            BUDS.onBroadcastFRS = new Buddies.BroadcastMethod(BroadcastFRS);
            isRunning = true;
            listenThread = new Thread(MainThread);
            listenThread.Start();

            if ((aprscfg != null) && (aprscfg.user != null) && (aprscfg.user != String.Empty) && (aprscfg.url != null) && (aprscfg.url != String.Empty) &&
                ((aprscfg.global2ais == "yes") || (aprscfg.global2aprs == "yes") || (aprscfg.global2frs == "yes") || (aprscfg.aprs2global == "yes") || (aprscfg.any2global == "yes")))
            {
                aprsgw = new APRSISGateWay(aprscfg);
                aprsgw.onPacket = new APRSISGateWay.onAPRSGWPacket(OnGlobalAPRSData);
                aprsgw.Start();
            };
        }

        private void MainThread()
        {
            mainListener = new TcpListener(this.ListenIP, this.ListenPort);
            mainListener.Start();
            Console.WriteLine("OK");
            Console.WriteLine("Info at: http://{2}:{0}{1}info",ListenPort, urlPath, infoIP);
            Console.WriteLine("Admin at: http://{3}:{0}{1}${2}", ListenPort, urlPath, adminName, infoIP);
            (new Thread(PingNearestThread)).Start(); // ping clients thread
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

        private void PingNearestThread()
        {
            ushort pingInterval = 0;
            ushort nrstInterval = 0;
            while (isRunning)
            {
                if (pingInterval++ == 300) // 30 sec
                {
                    pingInterval = 0;
                    try { PingAlive(); }
                    catch { };
                };
                if (nrstInterval++ == 450) // 45 sec
                {
                    nrstInterval = 0;
                    try { SendNearest(); }
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

            if (aprsgw != null) aprsgw.Stop();
            aprsgw = null;

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
            BroadcastAPRS(pingdata);
            Send2Air(null, pingdata);

            SafetyRelatedBroadcastMessage sbm = new SafetyRelatedBroadcastMessage("PING, " + OruxPalsServer.softver.ToUpper());
            string txt = sbm.ToPacketFrame() + "\r\n";
            byte[] ret = Encoding.ASCII.GetBytes(sbm.ToPacketFrame() + "\r\n");
            BroadcastAIS(ret);

            PingFRS();
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
            int waitCounter = 45; // 4.5 sec, after 2 seconds - welcome

            while (Running && cd.thread.IsAlive && IsConnected(cd.client))
            {
                if (((cd.state == 1) || (cd.state == 6)) && (DateTime.UtcNow.Subtract(cd.connected).TotalMinutes >= MaxClientAlive)) break;

                try { rxAvailable = cd.client.Client.Available; }
                catch { break; };

                // AIS Client or APRS Read Only
                if ((cd.state == 1) || (cd.state == 6))
                {
                    Thread.Sleep(1000);
                    continue;
                };

                // After 2 seconds write AIS Welcome message
                // OruxMaps from version OruxMaps7.0.0rc7.apk supports APRS, but identity packet sends 
                //   only when any data received from server // client save 2.5 sec to identify
                if ((cd.state == 0) && (waitCounter == 25))
                {
                    SafetyRelatedBroadcastMessage sbm = new SafetyRelatedBroadcastMessage("WELCOME, AIS/APRS, " + OruxPalsServer.softver.ToUpper());
                    byte[] ret = Encoding.ASCII.GetBytes(sbm.ToPacketFrame() + "\r\n");
                    cd.stream.Write(ret, 0, ret.Length);
                };

                // After 4.5 seconds if no data
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

                // READ INCOMING DATA //
                try
                {
                    // Identificate Browser, GPSGate, MapMyTracks, APRS or FRS Client //
                    if ((cd.state == 0) && (rxText.Length >= 4) && (rxText.IndexOf("\n") > 0))
                    {
                        if (rxText.IndexOf("GET") == 0) // GPSGate, Browser
                            OnGet(cd, rxText);
                        else if (rxText.IndexOf("POST") == 0) // MapMyTracks
                            OnPost(cd, rxText);
                        else if (rxText.IndexOf("user") == 0) // APRS
                        {
                            if (OnAPRSClient(cd, rxText.Replace("\r", "").Replace("\n", "")))
                                rxText = "";
                            else
                                break;
                        }
                        else if (rxText.IndexOf("$FRPAIR") == 0) // FRS
                        {
                            if (OnFRSClient(cd, rxText.Replace("\r", "").Replace("\n", "")))
                                rxText = "";
                            else
                                break;
                        }
                        else
                            break;
                    };

                    // Receive incoming data from identificated clients only
                    if ((cd.state > 0) && (rxText.Length > 0) && (rxText.IndexOf("\n") > 0))
                    {
                        // Verified APRS Client //
                        if (cd.state == 4)
                        {
                            string[] lines = rxText.Split(new string[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                            rxText = "";
                            foreach (string line in lines)
                                OnAPRSData(cd, line);
                        };

                        // FRS Client //
                        if (cd.state == 5)
                        {
                            string[] lines = rxText.Split(new string[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                            rxText = "";
                            foreach (string line in lines)
                                OnFRSData(cd, line);
                        };

                        // AFSKMODEM
                        if (cd.state == 7)
                        {
                            string[] lines = rxText.Split(new string[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                            rxText = "";
                            foreach (string line in lines)
                                OnAir(cd, line);
                        };

                        rxText = "";
                    };
                }
                catch
                { };

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
            foreach (Buddie b in bup) 
                if((b.source != 5) && (b.source != 6)) // no tx everytime & static objects
                    blist.Add(b.AISNMEA);
            foreach (byte[] ba in blist)
                try { cd.stream.Write(ba, 0, ba.Length); } catch { };
        }

        private bool OnAPRSClient(ClientData cd, string loginstring)
        {
            #if DEBUGORUX
            Console.WriteLine("> " + loginstring);
            #endif

            string res = "# logresp user unverified, server " + serviceName.ToUpper();

            Match rm = Regex.Match(loginstring, @"^user\s([\w\-]{3,})\spass\s([\d\-]+)\svers\s([\w\d\-.]+)\s([\w\d\-.\+]+)");
            if (rm.Success)
            {
                string callsign = rm.Groups[1].Value.ToUpper();

                if (banlist.Contains(callsign))
                {
                    cd.client.Close();
                    return false;
                };

                string password = rm.Groups[2].Value;
                //string software = rm.Groups[3].Value;
                //string version = rm.Groups[4].Value;
                string doptext = loginstring.Substring(rm.Groups[0].Value.Length).Trim();
                if (doptext.IndexOf("filter") >= 0)
                    cd.SetFilter(doptext.Substring(doptext.IndexOf("filter") + 7));
                
                int psw = -1;
                int.TryParse(password, out psw);
                // check for valid HAM user or for valid OruxPalsServer user
                // these users can upload data to server
                if ((psw == APRSData.CallsignChecksum(callsign)) || (psw == Buddie.Hash(callsign)))
                {
                    if (callsign == "AFSKMODEM")
                    {
                        cd.state = 7; // AFSKMODEM
                        cd.user = callsign;

                        res = "# logresp " + callsign + " verified, server " + serviceName.ToUpper();
                        byte[] aret = Encoding.ASCII.GetBytes(res + "\r\n");
                        try { cd.stream.Write(aret, 0, aret.Length); }
                        catch { };

                        return true;
                    };

                    cd.state = 4; //APRS
                    cd.user = callsign; // .user - valid username for callsign

                    /* SEARCH REGISTERED as Global APRS user*/
                    if ((regUsers != null) && (regUsers.Length > 0))
                        foreach (OruxPalsServerConfig.RegUser u in regUsers)
                            if ((u.services != null) && (u.services.Length > 0))
                                foreach (OruxPalsServerConfig.RegUserSvc svc in u.services)
                                    if (svc.names.Contains("A"))
                                        if (callsign == svc.id)
                                            cd.user = u.name;

                    // remove ssid, `-` not valid symbol in name
                    if (cd.user.Contains("-")) cd.user = cd.user.Substring(0, cd.user.IndexOf("-"));

                    res = "# logresp " + callsign + " verified, server " + serviceName.ToUpper();
                    byte[] ret = Encoding.ASCII.GetBytes(res + "\r\n");
                    try { cd.stream.Write(ret, 0, ret.Length); }
                    catch { };

                    SendBuddies(cd);
                    return true;
                };
            };

            // Invalid user
            // these users cannot upload data to server (receive data only allowed)
            {
                cd.state = 6; // APRS Read-Only
                byte[] ret = Encoding.ASCII.GetBytes(res + "\r\n");
                try { cd.stream.Write(ret, 0, ret.Length); }
                catch { };

                SendBuddies(cd);
                return true;
            };
        }

        // on verified users // they can upload data to server
        private void OnAPRSData(ClientData cd, string line)
        {
            #if DEBUGORUX
            Console.WriteLine("> " + line);
            #endif

            // COMMENT STRING
            if (line.IndexOf("#") == 0)
            {
                string filter = "";
                if (line.IndexOf("filter") > 0) filter = line.Substring(line.IndexOf("filter"));
                // filter ... active
                if (filter != "")
                {
                    string fres = cd.SetFilter(filter.Substring(7));
                    string resp = "# filter '" + fres + "' is active\r\n";
                    byte[] bts = Encoding.ASCII.GetBytes(resp);
                    try { cd.stream.Write(bts, 0, bts.Length); }
                    catch { }
                };
                return;
            };
            if (line.IndexOf(">") < 0) return;            
            
            // PARSE NORMAL PACKET
            Buddie b = APRSData.ParseAPRSPacket(line);
            if ((b != null) && (b.name != null) && (b.name != String.Empty))
            {
                bool forward2aprs = false;

                if((regUsers != null) && (regUsers.Length > 0))
                    foreach (OruxPalsServerConfig.RegUser u in regUsers)
                    {                        
                        if ((!callsignToUser) && (u.name == b.name)) b.regUser = u; // packet callsign as userName
                        if (callsignToUser && (u.name == cd.user)) b.regUser = u; // client logon with userName

                        if ((u.services != null) && (u.services.Length > 0))
                            foreach (OruxPalsServerConfig.RegUserSvc svc in u.services)
                                if (svc.names.Contains("A") && (b.name == svc.id)) // client logon with Callsign
                                {
                                    b.regUser = u;
                                    // can forward to global only if user logon with callsign, and forward set to A
                                    forward2aprs = (u.forward != null) && (u.forward.Contains("A")); 
                                };                        
                    };

                // 2 SERVER MESSAGE
                // messages for server no need to save, broadcast & forward
                try { if (line.IndexOf("::") > 0) if (OnAPRSinternalMessage(cd, b, line)) return; }
                catch { };
                
                // Direct Forward if user logon with global Callsign (see xml comment) //
                if (forward2aprs && (aprsgw != null) && (aprscfg.aprs2global == "yes"))
                {
                    string broadcastline = b.APRS;
                    // if no comment passed set comment from registered user info
                    if ((b.regUser != null) && (b.PositionIsValid) && ((b.parsedComment == null) || (b.parsedComment == String.Empty)) && ((b.regUser.comment != null) && ((b.regUser.comment != String.Empty))))
                        broadcastline = line + " " + b.regUser.comment + "\r\n";
                    aprsgw.SendCommand(broadcastline);
                };
                
                // if status
                if ((line.IndexOf(":>") > 0) && (BUDS != null))
                    BUDS.UpdateStatus(b, line.Substring(line.IndexOf(":>")+2));

               // If callsignToUser is `yes` and
               // if packet sender is not user name (for registered users) or not as logged in name
               // for all packets will set sender as user name (for registered users) or as logged in name
               // If callsignToUser is `no` - verified user can send packets from any sender as is (no replacement)                               
               if ((callsignToUser) && (b.name != cd.user))
               {
                   b.APRS = b.APRS.Replace(b.name + ">", cd.user + ">");
                   b.APRSData = Encoding.ASCII.GetBytes(b.APRS);
                   b.name = cd.user;
               };

               if ((b.PositionIsValid)) // if Position Packet send to All (AIS + APRS + Web)
               {
                   OnNewData(b);
                   cd.lastFixYX = new double[3] { 1, b.lat, b.lon };                   
               }
               else // if not Position Packet send as is only to all APRS clients
                   Broadcast(b.APRSData, b.name, false, true, false);

               // broadcast to air
               Send2Air(cd, b.APRSData);
            };
        }

        private void OnAir(ClientData cd, string line)
        {
            //Console.WriteLine("AIR> " + line);

            if (line.IndexOf("#") == 0) return;
            
            int id = line.IndexOf(":");
            int ip = line.IndexOf(">");
            if (id > 0)
            {
                string IGate = "ORXPLS-GW";
                if ((aprscfg != null) && (aprscfg.user != null) && (aprscfg.user != String.Empty)) IGate = aprscfg.user;
                line = line.Insert(line.IndexOf(":"), (id == (ip + 1) ? "qAR" : ",qAR") + "," + IGate);
            };
            
            // PARSE NORMAL PACKET
            Buddie b = APRSData.ParseAPRSPacket(line);

            // 2 SERVER MESSAGE
            // messages for server no need to save, broadcast & forward
            try { if (line.IndexOf("::") > 0) if (OnAPRSinternalMessage(cd, b, line)) return; }
            catch { };

            // Direct Forward to Global
            if ((aprsgw != null) && (aprscfg.aprs2global == "yes"))
                aprsgw.SendCommand(b.APRS);

            // if status
            if ((line.IndexOf(":>") > 0) && (BUDS != null))
                BUDS.UpdateStatus(b, line.Substring(line.IndexOf(":>") + 2));

            if ((b.PositionIsValid)) // if Position Packet send to All (AIS + APRS + Web)
                OnNewData(b);
            else // if not Position Packet send as is only to all APRS clients
                Broadcast(b.APRSData, b.name, false, true, false);

            // broadcast to air
            Send2Air(cd, b.APRSData);
        }

        private void Send2Air(ClientData sender, byte[] aprs)
        {
            List<ClientData> cdlist = new List<ClientData>();
            lock (clientList)
                foreach (object obj in clientList.Values)
                {
                    if (obj == null) continue;
                    ClientData cd = (ClientData)obj;
                    if ((cd.state == 7) && ((sender == null) || (cd.id != sender.id)))
                        cdlist.Add(cd);
                };

            foreach (ClientData cd in cdlist)
                try { cd.client.GetStream().Write(aprs, 0, aprs.Length); }
                catch { };
        }

        private void SendBuddies(ClientData cd)
        {
            if (BUDS != null)
            {
                Buddie[] bup = BUDS.Current;
                List<byte[]> blist = new List<byte[]>();
                foreach (Buddie b in bup)
                {
                    if((b.source != 5) && (b.name != cd.user))
                        if((cd.filter != null) && (!cd.filter.PassName(b.name))) 
                            continue;
                    if (sendBack || (cd.state == 6) || (b.source == 5))
                        blist.Add(b.APRSData);
                    else if (b.name != cd.user)
                        blist.Add(b.APRSData);
                };
                foreach (byte[] ba in blist)
                    try { cd.stream.Write(ba, 0, ba.Length); }
                    catch { };
            };
        }

        private void SendNearest()
        {
            List<ClientData> cdl = new List<ClientData>();

            lock (clientList)
                foreach (ClientData ci in clientList.Values)
                    if (ci.state == 4)
                        cdl.Add(ci);

            if (cdl.Count > 0)
                if (BUDS != null)
                    foreach (ClientData ci in cdl)
                        if ((ci.lastFixYX != null) && (ci.lastFixYX.Length == 3) && ((int)ci.lastFixYX[0] == 1))
                        {
                            if ((ci.filter == null) || (ci.filter.inMyRadiusKM != 0))
                            {
                                PreloadedObject[] nearest = BUDS.GetNearest(ci.lastFixYX[1], ci.lastFixYX[2], ci.filter);
                                if ((nearest != null) && (nearest.Length > 0))
                                    foreach (PreloadedObject near in nearest)
                                    {
                                        byte[] bts = Encoding.ASCII.GetBytes(near.ToString());
                                        try { ci.stream.Write(bts, 0, bts.Length); }
                                        catch { };
                                    };
                            };
                            ci.lastFixYX = new double[] { 0, 0, 0 };
                        };
        }

        private bool OnAPRSinternalMessage(ClientData cd, Buddie buddie, string line)
        {
            string frm = line.Substring(0, line.IndexOf(">"));
            string msg = line.Substring(line.IndexOf("::") + 12).Trim();

            bool sendack = false;
            byte[] tosendack = new byte[0];

            // FIND USER, callsign of the packet must be within registered users //
            OruxPalsServerConfig.RegUser cu = null;
            if ((frm != "") && (regUsers != null))
                foreach (OruxPalsServerConfig.RegUser u in regUsers)
                {
                    if (frm == u.name)
                    {
                        cu = u;
                        break;
                    };
                    if (u.services != null)
                        foreach (OruxPalsServerConfig.RegUserSvc svc in u.services)
                            if (svc.names.Contains("A") && (svc.id == frm))
                            {
                                cu = u;
                                break;
                            };
                };

            if (line.IndexOf("::ORXPLS-GW:") > 0) // ping & forward
            {
                sendack = true;
                if (msg.Contains("{"))
                {
                    string cmd2s = "ORXPLS-GW>APRS,TCPIP*::" + frm + ": ack" + msg.Substring(msg.IndexOf("{") + 1) + "\r\n";
                    msg = msg.Substring(0, msg.IndexOf("{")).Trim();
                    tosendack = Encoding.ASCII.GetBytes(cmd2s);
                };

                if ((msg != null) && (msg != String.Empty))
                {
                    string[] ms = msg.ToUpper().Split(new string[] { " " }, StringSplitOptions.None);
                    if (ms == null) return true;
                    if (ms.Length == 0) return true;

                    if (ms[0] == "FORWARD") // forward only for registered users
                    {                        
                        if (cu == null)
                        {
                            string cmd2s = "ORXPLS-GW>APRS,TCPIP*::" + frm + ": no forward privileges\r\n";
                            byte[] bts = Encoding.ASCII.GetBytes(cmd2s);
                            try { cd.stream.Write(bts, 0, bts.Length); }
                            catch { };
                        }
                        else
                        {
                            if (ms.Length > 1)
                                cu.forward = ms[1].ToUpper();
                            string cmd2s = "ORXPLS-GW>APRS,TCPIP*::" + frm + ": forward set to `" + (cu.forward == null ? "" : cu.forward) + "`\r\n";
                            byte[] bts = Encoding.ASCII.GetBytes(cmd2s);
                            try { cd.stream.Write(bts, 0, bts.Length); }
                            catch { };
                        };
                    }
                    else if ((ms[0] == "KILL") && (ms.Length > 1)) // kill only for registered users
                    {
                        string u2k = ms[1].ToUpper();
                        if (cu == null)
                        {
                            string cmd2s = "ORXPLS-GW>APRS,TCPIP*::" + frm + ": no kill privileges\r\n";
                            byte[] bts = Encoding.ASCII.GetBytes(cmd2s);
                            try { cd.stream.Write(bts, 0, bts.Length); }
                            catch { };
                        }
                        else
                        {
                            bool ok = false;
                            if (BUDS != null) ok = BUDS.Kill(u2k);
                            string cmd2s = "ORXPLS-GW>APRS,TCPIP*::" + frm + ": kill `" + u2k + "` "+(ok ? "OK" : "FAILED")+"\r\n";
                            byte[] bts = Encoding.ASCII.GetBytes(cmd2s);
                            try { cd.stream.Write(bts, 0, bts.Length); }
                            catch { };
                        };
                    }
                    else if ((ms[0] == adminName.ToUpper()) && (ms.Length > 1)) // hashsum for any user that know admin
                    {
                        string res = "";
                        for (int i = 1; i < ms.Length; i++) res += "u " + ms[i] + " a " + APRSData.CallsignChecksum(ms[i]) + " p " + Buddie.Hash(ms[i]) + " ";
                        string cmd2s = "ORXPLS-GW>APRS,TCPIP*::" + frm + ": " + res + "\r\n";
                        byte[] bts = Encoding.ASCII.GetBytes(cmd2s);
                        try { cd.stream.Write(bts, 0, bts.Length); }
                        catch { };
                    };
                };
            };

            if (line.IndexOf("::ORXPLS-ST:") > 0) // global status only for APRS global users
            {
                sendack = true;
                if (msg.Contains("{"))
                {
                    string cmd2s = "ORXPLS-ST>APRS,TCPIP*::" + frm + ": ack" + msg.Substring(msg.IndexOf("{") + 1) + "\r\n";
                    msg = msg.Substring(0, msg.IndexOf("{")).Trim();
                    tosendack = Encoding.ASCII.GetBytes(cmd2s);
                };

                cu = null;
                string id = "";
                if ((regUsers != null) && (aprsgw != null) && (aprscfg.aprs2global == "yes"))
                    foreach (OruxPalsServerConfig.RegUser u in regUsers)                    
                        if ((u.forward != null) && (u.forward.Contains("A")) && (u.services != null))
                            foreach (OruxPalsServerConfig.RegUserSvc svc in u.services)
                                if (svc.names.Contains("A") && (svc.id == frm))
                                {
                                    cu = u;
                                    id = svc.id;
                                    break;
                                };

                if ((msg != null) && (msg != String.Empty))
                {
                    BUDS.UpdateStatus(buddie, msg);
                    if (id != "")
                    {
                        aprsgw.SendCommand(id + ">APRS,TCPIP*:>" + msg + "\r\n");

                        string cmd2s = "ORXPLS-ST>APRS,TCPIP*::" + frm + ": " + msg + "\r\n";
                        byte[] bts = Encoding.ASCII.GetBytes(cmd2s);
                        try { cd.stream.Write(bts, 0, bts.Length); }
                        catch { };
                    }
                    else if((buddie != null) && (buddie.regUser != null))
                    {                        
                        string cmd2s = buddie.regUser.name + ">APRS,TCPIP*:>" + msg + "\r\n";
                        byte[] bts = Encoding.ASCII.GetBytes(cmd2s);
                        BroadcastAPRS(bts);
                    };
                }
                else
                {
                    string cmd2s = "ORXPLS-ST>APRS,TCPIP*::" + frm + ": FAILED\r\n";
                    byte[] bts = Encoding.ASCII.GetBytes(cmd2s);
                    try { cd.stream.Write(bts, 0, bts.Length); }
                    catch { };
                };
            };

            if (line.IndexOf("::ORXPLS-CM:") > 0) // comment
            {
                sendack = true;
                if (msg.Contains("{"))
                {
                    string cmd2s = "ORXPLS-CM>APRS,TCPIP*::" + frm + ": ack" + msg.Substring(msg.IndexOf("{") + 1) + "\r\n";
                    msg = msg.Substring(0, msg.IndexOf("{")).Trim();
                    tosendack = Encoding.ASCII.GetBytes(cmd2s);
                };

                msg = msg.Trim();
                bool updated = false;
                
                if ((msg != null) && (msg != String.Empty) && (msg != "?") && (buddie != null))
                {
                    if (BUDS != null)
                        updated = BUDS.UpdateComment(buddie, msg);
                    if ((!updated) && (buddie.regUser != null))
                    {
                        buddie.regUser.comment = msg;
                        updated = true;
                    };
                };
                if (updated)
                {
                    string cmd2s = "ORXPLS-CM>APRS,TCPIP*::" + frm + ": " + msg + "\r\n";
                    byte[] bts = Encoding.ASCII.GetBytes(cmd2s);
                    try { cd.stream.Write(bts, 0, bts.Length); }
                    catch { };
                }
                else
                {
                    string cmd2s = "";
                    if ((msg == "?") && (buddie != null) && (buddie.regUser != null))
                        cmd2s = "ORXPLS-CM>APRS,TCPIP*::" + frm + ": " + buddie.regUser.comment + "\r\n";
                    else
                        cmd2s = "ORXPLS-CM>APRS,TCPIP*::" + frm + ": User not found\r\n";
                    byte[] bts = Encoding.ASCII.GetBytes(cmd2s);
                    try { cd.stream.Write(bts, 0, bts.Length); }
                    catch { };
                };
            };

            if (sendack && (tosendack.Length > 0))
                try { cd.stream.Write(tosendack, 0, tosendack.Length); }
                catch { };

            return sendack;
        }

        private void OnGlobalAPRSData(string line)
        {
            if (aprscfg.global2aprs == "yes")
                BroadcastAPRS(Encoding.ASCII.GetBytes(line+"\r\n"));

            if ((aprscfg.global2ais == "yes") || (aprscfg.global2frs == "yes"))
            {
                Buddie b = APRSData.ParseAPRSPacket(line);
                if ((b != null) && (b.name != null) && (b.name != String.Empty) && (b.PositionIsValid))                    
                {
                    if (aprscfg.global2ais == "yes")
                    {
                        b.SetAIS();
                        b.green = true;
                        BroadcastAIS(b.AISNMEA);
                    };
                    if (aprscfg.global2frs == "yes")
                    {
                        b.SetFRS();
                        BroadcastFRS(b.FRPOSData);
                    };
                };
            };
        }

        private bool OnFRSClient(ClientData cd, string pairstring)
        {
            byte[] ba = Encoding.ASCII.GetBytes("# " + softver + "\r\n");
            try { cd.stream.Write(ba, 0, ba.Length); } catch { };

            Match rx;
            if ((rx = Regex.Match(pairstring, @"^(\$FRPAIR),([\w\+]+),(\w+)\*(\w+)$")).Success)
            {
                string phone = rx.Groups[2].Value;
                // string imei = rx.Groups[3].Value;
                if(regUsers != null) 
                    foreach(OruxPalsServerConfig.RegUser u in regUsers)
                        if (u.phone == phone)
                        {
                            cd.state = 5;
                            cd.user = u.name;

                            // Welcome message
                            string pmsg = ChecksumAdd2Line("$FRCMD," + u.name + ",_Ping,Inline");
                            byte[] data = Encoding.ASCII.GetBytes(pmsg + "\r\n");
                            try { cd.client.GetStream().Write(data, 0, data.Length); }
                            catch { };

                            if (!_NoSendToFRS)
                            {
                                if (BUDS != null)
                                {
                                    Buddie[] bup = BUDS.Current;
                                    List<byte[]> blist = new List<byte[]>();
                                    foreach (Buddie b in bup)
                                    {
                                        if ((b.source != 5) && (b.source != 6)) // no tx everytime & static objects
                                            continue;
                                        if (sendBack)
                                            blist.Add(b.FRPOSData);
                                        else if (cd.user != b.name)
                                            blist.Add(b.FRPOSData);
                                    };
                                    foreach (byte[] ba2 in blist)
                                        try { cd.stream.Write(ba2, 0, ba2.Length); }
                                        catch { };
                                };
                            };

                            return true;
                        };
            };
            return false;
        }

        private void OnFRSData(ClientData cd, string line)
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
                   // 0000.00000,N,00000.00000,E,0.0,0.000,0.0,190117,122708.837,0,Temperature=-2
                   // DDMM.mmmm,N,DDMM.mmmm,E,AA.a,SSS.ss,HHH.h,DDMMYY,hhmmss.dd,fixOk,NOTE*xx

                   Match rxa = Regex.Match(val2, @"^(\d{4}.\d+),(N|S),(\d{5}.\d+),(E|W),([0-9.]*),([0-9.]*),([0-9.]*),(\d{6}),([0-9.]{6,}),([\w.\s=]),([\w.\s=\-,]*)$");
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

                           Buddie b = new Buddie(4, cd.user, rLat, rLon, (short)Math.Round(rSpeed), (short)Math.Round(rHeading));
                           b.lastPacket = line;
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
                
        private void OnGet(ClientData cd, string rxText)
        {
            cd.state = 2;
            int hi = rxText.IndexOf("HTTP");
            if (hi <= 0) { HTTPClientSendError(cd.client, 400); return; };
            string query = rxText.Substring(4, hi - 4).Trim();
            if (!IsValidQuery(query))
            {
                HTTPClientSendError(cd.client, 404);
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
                    HTTPClientSendError(cd.client, 403);
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
                HTTPClientSendError(cd.client, 403);
                return;
            };
        }

        private void OnBrowser(ClientData cd, string query)
        {
            query = query.Replace("/", "").ToUpper();
            string user = "user";

            string addit = "";
            if ((query.Length > 0) && (Buddie.BuddieCallSignRegex.IsMatch(query)))
            {
                addit = "";
                Buddie[] all = BUDS.Current;
                foreach (Buddie b in all)
                    if (b.name == query)
                    {
                        string src = "Unknown";
                        if (b.source == 1) src = "OruxMaps GPSGate";
                        if (b.source == 2) src = "OruxMaps MapMyTracks";
                        if (b.source == 3) src = "APRS Client";
                        if (b.source == 4) src = "FRS (GPSGate Tracker)";
                        if (b.source == 5) src = "Everytime Object";
                        if (b.source == 6) src = "Static Object";
                        
                        string symb = b.IconSymbol;
                        string prose = "primary";
			            if(symb.Length == 2)
			            {
				            if(symb[0] == '\\') prose = "secondary";
				            symb = symb.Substring(1);
			            };
			            string symbtable = "!\"#$%&\'()*+,-./0123456789:;<=>?@ABCDEFGHIJKLMNOPQRSTUVWXYZ[\\]^_`abcdefghijklmnopqrstuvwxyz{|}~";						
			            int idd = symbtable.IndexOf(symb);
			            if(idd < 0) idd = 14;
			            int itop =  (int)Math.Truncate(idd / 16.0) * 24;
			            int ileft = (idd % 16) * 24;
			            symb = "background:url(../v/images/"+prose+".png) -"+ileft.ToString()+"px -"+itop.ToString()+"px no-repeat;";
                        symb = "<span style=\"display:inline-block;height:24px;width:24px;" + symb + "\">&nbsp;</span>";

                        user = b.name;
                        addit += "<table cellpadding=\"1\" cellspacing=\"1\" border=\"0\">";
                        addit += String.Format("<tr><td><small>User:</small></td><td> <b><a href=\"../v/mapf.html#{0}\">{0}</a> {1}</td></tr>", b.name, b.regUser == null ? "" : "&reg; " + (b.regUser.forward == null ? "" : b.regUser.forward));
                        addit += String.Format("<tr><td><small>Source:</small></td><td> {0}</td></tr>", src);
                        addit += String.Format("<tr><td><small>Received:</small></td><td> {0} UTC</td></tr>", b.last);
                        addit += String.Format("<tr><td><small>Valid till:</small></td><td> {0} UTC</td></tr>", b.last.AddHours(maxHours));
                        addit += String.Format(System.Globalization.CultureInfo.InvariantCulture, "<tr><td><small>Position:</small></td><td> {0:00.00000000} {1:00.00000000}</td></tr>", b.lat, b.lon);
                        addit += String.Format(System.Globalization.CultureInfo.InvariantCulture, "<tr><td><small>Speed:</small></td><td> {0} km/h; {1:0.0} mph; {2:0.0} knots</td></tr>", b.speed, b.speed * 0.62137119, b.speed / 1.852);
                        addit += String.Format("<tr><td><small>Heading:</small></td><td> {0}&deg; {1}</td></tr>", b.course, HeadingToText(b.course));
                        addit += String.Format("<tr><td><small>Symbol:</small></td><td> {0} {1}</td></tr>", System.Security.SecurityElement.Escape(b.IconSymbol), symb);
                        addit += String.Format("<tr><td><small>Comment:</small></td><td style=\"color:navy;\"> {0}</td></tr>", b.Comment == null ? "" : System.Security.SecurityElement.Escape(b.Comment));
                        addit += String.Format("<tr><td><small>Status:</small></td><td style=\"color:maroon;\"> {0}</td></tr>", b.Status == null ? "" : System.Security.SecurityElement.Escape(b.Status));
                        addit += String.Format("<tr><td><small>Origin:</small></td><td style=\"color:gray;\"><small> {0}</small></td></tr>", b.lastPacket);
                        addit += "</table>";

                        addit += "<div id=\"ZM13\" style=\"display:inline-block;\"></div><div id=\"ZM15\" style=\"display:inline-block;\"></div>";

                        addit += String.Format("<div id=\"YA13\" style=\"border:solid 1px gray;display:inline-block;width:500px;height:300px;background:url('http://static-maps.yandex.ru/1.x/?ll={0},{1}&size=500,300&z=13&l=map&pt={0},{1},round') 0px 0px no-repeat;\">" +
                            "<div style=\"display:block;width:24;height:24;overflow:hidden;position:relative;left:238;top:138;background:url(../v/images/" + prose + ".png) -" + ileft.ToString() + "px -" + itop.ToString() + "px no-repeat;\"></div>" +
                            "<div style=\"display:block;width:32;height:32;overflow:visible;position:relative;left:238;top:110;\"><a target=\"_blank\" href=\"" + (urlPath + "view#" + b.name) + "\"><img src=\"../v/images/arrow.png\" style=\"transform:rotate(" + b.course.ToString() + "deg);transform-origin:50% 50% 0px;\" title=\""+b.name+"\"/></a></div>" + 
                            "</div>", b.lon.ToString(System.Globalization.CultureInfo.InvariantCulture), b.lat.ToString(System.Globalization.CultureInfo.InvariantCulture), b.name);
                        addit += String.Format("<div id=\"YA15\" style=\"border:solid 1px gray;display:inline-block;width:500px;height:300px;background:url('http://static-maps.yandex.ru/1.x/?ll={0},{1}&size=500,300&z=15&l=map&pt={0},{1},round') 0px 0px no-repeat;\">" +
                            "<div style=\"display:block;width:24;height:24;overflow:hidden;position:relative;left:238;top:138;background:url(../v/images/" + prose + ".png) -" + ileft.ToString() + "px -" + itop.ToString() + "px no-repeat;\"></div>" +
                            "<div style=\"display:block;width:32;height:32;overflow:visible;position:relative;left:238;top:110;\"><a target=\"_blank\" href=\"" + (urlPath + "view#" + b.name) + "\"><img src=\"../v/images/arrow.png\" style=\"transform:rotate(" + b.course.ToString() + "deg);transform-origin:50% 50% 0px;\" title=\""+b.name+"\"/></a></div>" + 
                            "</div>", b.lon.ToString(System.Globalization.CultureInfo.InvariantCulture), b.lat.ToString(System.Globalization.CultureInfo.InvariantCulture), b.name);
                        addit += String.Format("<br/><small><a href=\"https://yandex.ru/maps/?text={1},{0}\" target=\"_blank\">view on yandex</a> | <a href=\"http://maps.google.com/?q={1}+{0}\" target=\"_blank\">view on google</a><small>", b.lon.ToString(System.Globalization.CultureInfo.InvariantCulture), b.lat.ToString(System.Globalization.CultureInfo.InvariantCulture), b.name);

                        addit += "<script>\r\n";
                        addit += " var mm = new MapMerger(500,300);\r\n ";
                        addit += " mm.InitIcon('" + b.IconSymbol.Replace(@"\", @"\\").Replace(@"'", @"\'") + "', " + b.course.ToString() + ", '" + b.name + "', '" + (urlPath + "view#" + b.name) + "');\r\n ";
                        addit += String.Format(System.Globalization.CultureInfo.InvariantCulture, " document.getElementById('ZM13').innerHTML = mm.GetMap({0}, {1}, 13);\r\n", b.lat, b.lon);
                        addit += String.Format(System.Globalization.CultureInfo.InvariantCulture, " document.getElementById('ZM15').innerHTML = mm.GetMap({0}, {1}, 15);\r\n", b.lat, b.lon);
                        addit += " </script>\r\n";
                    };
            };

            int cAIS = 0;
            int cAPRS = 0;
            int cAPRSr = 0;
            int cFRS = 0;
            int cAIR = 0;
            lock (clientList)
                foreach (ClientData ci in clientList.Values)
                {
                    if (ci.state == 1) cAIS++;
                    if (ci.state == 4) { cAPRS++; cAPRSr++; };
                    if (ci.state == 5) cFRS++;
                    if (ci.state == 6) cAPRS++;
                    if (ci.state == 7) cAIR++;
                };
            int bc = 0, rbc = 0;
            string rbds = "";
            string ubds = "";
            if (BUDS != null)
            {
                Buddie[] all = BUDS.Current;
                foreach (Buddie b in all)
                {
                    if (b.source == 5) continue; // Everytime object
                    if (b.source == 6) continue; // Static object
                    bc++;
                    bool isreg = false;
                    if(regUsers != null)
                        foreach (OruxPalsServerConfig.RegUser u in regUsers)
                        {
                            if (u.name == b.name)
                            {
                                isreg = true;
                                rbc++;
                                break;
                            };
                            if((u.services != null) && (u.services.Length > 0))
                                foreach(OruxPalsServerConfig.RegUserSvc svc in u.services)
                                    if(svc.names.Contains("A") &&(svc.id == b.name))
                                    {
                                        isreg = true;
                                        rbc++;
                                        break;
                                    };
                        };
                    if(isreg)
                      rbds += "<a href=\"" + urlPath + "i/" + b.name + "\">" + b.name + "</a> ";
                    else
                      ubds += "<a href=\"" + urlPath + "i/" + b.name + "\">" + b.name + "</a> ";
                };
                if (rbds.Length > 0) rbds = "Registered: " + rbds + "\r\n<br/>";
                if (ubds.Length > 0) ubds = "Unregistered: " + ubds + "\r\n<br/>";
            };
            string fsvc = "";
            if (aprsgw != null)
            {
                int uc = 0;
                if(regUsers != null)
                    foreach(OruxPalsServerConfig.RegUser u in regUsers)
                        if((u.forward != null) && (u.forward.Contains("A"))) uc++;
                fsvc += String.Format("&nbsp; &nbsp; A as a with {0} clients: \r\n<br/>", uc);
                fsvc += "<span style=\"color:silver;font-size:11px;\">";
                fsvc += String.Format("&nbsp; &nbsp; &nbsp; &nbsp; <b>status:</b> {0}\r\n<br/>", aprsgw.State);
                fsvc += String.Format("&nbsp; &nbsp; &nbsp; &nbsp; <b>last rx:</b> {0}\r\n<br/>", aprsgw.lastRX);
                fsvc += String.Format("&nbsp; &nbsp; &nbsp; &nbsp; <b>last tx:</b> {0}\r\n<br/>", aprsgw.lastTX);
                fsvc += "</span>";
            };
            if (forwardServices != null)
                foreach (OruxPalsServerConfig.FwdSvc svc in forwardServices)
                    if (svc.forward == "yes")
                    {
                        int uc = 0;
                        if (regUsers != null)
                            foreach (OruxPalsServerConfig.RegUser u in regUsers)
                                if ((u.forward != null) && (u.forward.Contains(svc.name))) uc++;
                        fsvc += "&nbsp; &nbsp; " + String.Format("{0} as {1} with {2} clients\r\n<br/>", svc.name, svc.type, uc);
                    };
            if ((BUDS != null) && (addit == "")) addit = "<span style=\"color:green;\">Static Objects Data:</span><br/>" + BUDS.GetStaticObjectsInfo();
            HTTPClientSendResponse(cd.client, String.Format(                
                "<html><head><meta charset=\"windows-1251\"/><meta name=\"robots\" content=\"noindex, follow\"/><title>{0}</title><script src=\"../v/mapmerger.js\"></script></head><body>" + 
                "<span style=\"color:red;\">Server: {0}</span>\r\n<br/>" +
                "<span style=\"color:maroon;\">Name: " + ServerName + "</span>\r\n<br/>" +
                "Port: {4}\r\n<br/>" +
                "Started {1} UTC\r\n<br/>" +
                "<a href=\"{5}info\">Main View</a> | <a href=\"{5}view\">Map View</a> | <a href=\"{5}v/resources\">Resources</a> | <a href=\"{5}v/objects\">Objects</a>\r\n<hr/>" +
                "<div style=\"color:blue;\">Clients AIS/APRS(R)/FRS/AIR: {2} / {7} ({10}) / {8} / {11}\r\n</div><hr/>" +
                "<div style=\"color:green;\">Buddies Online/Registered/Unregistered: {3}\r\n<br/>" +
                "{6}\r\n</div><hr/>" +
                "<div style=\"color:maroon;\">Forward Services:\r\n<br/>" +
                "{9}\r\n</div><hr/>"+
                "<div style=\"color:navy;\">Client connect information:\r\n<br/>" +
                "&nbsp; &nbsp; OruxMaps\r\n<br/>" +
                "&nbsp; &nbsp; &nbsp; &nbsp; - AIS <span style=\"color:black;\">URL:</span> <span style=\"color:blue;\">" + infoIP + ":{4}</span> <i style=\"color:gray;\">receive data only</i>\r\n<br/>" +
                "&nbsp; &nbsp; &nbsp; &nbsp; - APRS <span style=\"color:black;\">URL:</span> <span style=\"color:blue;\">" + infoIP + ":{4}</span> <i style=\"color:gray;\">user and password required</i>\r\n<br/>" +
                "&nbsp; &nbsp; &nbsp; &nbsp; - GPSGate <span style=\"color:black;\">URL:</span> <span style=\"color:blue;\">http://" + infoIP + ":{4}{5}@" + user + "/</span> <i style=\"color:gray;\">set user password as IMEI</i>\r\n<br/>" +
                "&nbsp; &nbsp; &nbsp; &nbsp; - MapMyTracks <span style=\"color:black;\">URL:</span> <span style=\"color:blue;\">http://" + infoIP + ":{4}{5}m/</span> <i style=\"color:gray;\">user and password required</i>\r\n<br/>" +
                "&nbsp; &nbsp; APRS (APRSDroid) <span style=\"color:black;\">URL:</span> <span style=\"color:blue;\">" + infoIP + ":{4}</span> <i style=\"color:gray;\">user and password required</i>\r\n<br/>" +
                "&nbsp; &nbsp; FRS (GPSGate Tracker) <span style=\"color:black;\">URL:</span> <span style=\"color:blue;\">" + infoIP + ":{4}</span> <i style=\"color:gray;\">user's phone number must be registered</i>\r\n<br/>" +
                "</div><hr/>" +
                addit +
                "</body></html>",
                new object[] { 
                softver, 
                started, 
                cAIS, 
                bc.ToString()+" / " + rbc.ToString()+" / " + (bc - rbc).ToString(),
                ListenPort,
                urlPath,
                rbds + ubds,
                cAPRS,
                cFRS,
                fsvc,
                cAPRSr,
                cAIR
                }));
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
                        if (b.source == 5) src = "Everytime Object";
                        if (b.source == 6) src = "Static Object";
                        cdata += (cdata.Length > 0 ? "," : "") + "{" + String.Format(
                            "id:{7},user:'{0}',received:'{1}',lat:{2},lon:{3},speed:{4},hdg:{5},source:'{6}',age:{8},symbol:'{9}',r:{10},comment:'{11}',status:'{12}'",
                            new object[] { 
                                b.name, b.last, 
                                b.lat.ToString("0.00000000", System.Globalization.CultureInfo.InvariantCulture), 
                                b.lon.ToString("0.00000000", System.Globalization.CultureInfo.InvariantCulture), 
                                b.speed, b.course, 
                                src, b.ID, 
                                (int)DateTime.UtcNow.Subtract(b.last).TotalSeconds, 
                                b.IconSymbol.Replace(@"\", @"\\").Replace(@"'", @"\'"), 
                                b.regUser == null ? 0 : 1, 
                                (b.Comment == null ? "" : System.Security.SecurityElement.Escape(b.Comment)), 
                                (b.Status == null ? "" : System.Security.SecurityElement.Escape(b.Status)) }) + "}";
                    };
                };
                cdata = "[" + cdata + "]";
                HTTPClientSendResponse(cd.client, cdata);
            }
            else if (prf == "near")
            {
                Match m = Regex.Match(ptf, @"^([\d.]+)/([\d.]+)");
                if (m.Success)
                {
                    double lat = 0;
                    double lon = 0;
                    try
                    {
                        lat = double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                        lon = double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
                    }
                    catch
                    {
                        HTTPClientSendError(cd.client, 417);
                    };
                    string cdata = "";
                    if (BUDS != null)
                    {
                        PreloadedObject[] no = BUDS.GetNearest(lat, lon);
                        if((no != null) && (no.Length > 0))
                        foreach(PreloadedObject po in no)
                        {
                            string src = "Unknown";
                            if (po.radius < 0) 
                                src = "Everytime Object";
                            else
                                src = "Static Object";
                            cdata += (cdata.Length > 0 ? "," : "") + "{" + String.Format(
                                "id:{7},user:'{0}',received:'{1}',lat:{2},lon:{3},speed:{4},hdg:{5},source:'{6}',age:{8},symbol:'{9}',r:{10},comment:'{11}',status:'{12}'",
                                new object[] { 
                                po.name, DateTime.UtcNow.ToString(), 
                                po.lat.ToString("0.00000000", System.Globalization.CultureInfo.InvariantCulture), 
                                po.lon.ToString("0.00000000", System.Globalization.CultureInfo.InvariantCulture), 
                                0, 0, 
                                src, -1, 
                                0, 
                                po.symbol.Replace(@"\", @"\\").Replace(@"'", @"\'"), 
                                0, 
                                System.Security.SecurityElement.Escape(po.comment), 
                                "" }) + "}";
                        };
                    };
                    cdata = "[" + cdata + "]";
                    HTTPClientSendResponse(cd.client, cdata);
                }
                else
                    HTTPClientSendError(cd.client, 417);
            }
            else
            {
                if (ptf == "")
                    HTTPClientSendFile(cd.client, "map.html", "MAP");
                else if (ptf == "resources") // send 
                {
                    string ffn = OruxPalsServerConfig.GetCurrentDir() + @"\MAP\resources\";
                    string[] flist = Directory.GetFiles(ffn, "*.*", SearchOption.TopDirectoryOnly);
                    string txtout = "<span style=\"color:red;\">Server: " + OruxPalsServer.softver + "</span><br/><span style=\"color:maroon;\">Name: " + ServerName + "</span>\r\n<br/>Resources:\r\n<br/>";
                    txtout += "- <a href=\"../info\">&nbsp;..&nbsp;<a/><br/>";
                    if (flist != null)
                        foreach (string f in flist)
                        {
                            FileInfo fi = new FileInfo(f);
                            txtout += "- <a href=\"resources/" + System.IO.Path.GetFileName(f) + "\">" + System.IO.Path.GetFileName(f) + "<a/> " + String.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0.00} MB", fi.Length / 1024.0 / 1024.0) + "\r\n<br/>";
                        };
                    HTTPClientSendResponse(cd.client, txtout);
                }
                else if (ptf == "objects") // send 
                {
                    string ffn = OruxPalsServerConfig.GetCurrentDir() + @"\OBJECTS\";
                    string[] flist = Directory.GetFiles(ffn, "*.*", SearchOption.TopDirectoryOnly);
                    string txtout = "<span style=\"color:red;\">Server: " + OruxPalsServer.softver + "</span><br/><span style=\"color:maroon;\">Name: " + ServerName + "</span>\r\n<br/>Objects:\r\n<br/>";
                    txtout += "- <a href=\"../info\">&nbsp;..&nbsp;<a/><br/>";
                    if (flist != null)
                        foreach (string f in flist)
                        {
                            FileInfo fi = new FileInfo(f);
                            txtout += "- <a href=\"objects/" + System.IO.Path.GetFileName(f) + "\">" + System.IO.Path.GetFileName(f) + "<a/> " + String.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0.00} MB", fi.Length / 1024.0 / 1024.0) + "\r\n<br/>";
                        };
                    HTTPClientSendResponse(cd.client, txtout);
                }
                else if (ptf.Contains("objects/"))
                    HTTPClientSendFile(cd.client, ptf.Substring(8), "OBJECTS");
                else
                    HTTPClientSendFile(cd.client, ptf, "MAP");
            };
        }

        private void OnCmd(ClientData cd, string query)
        {
            int s2f = query.IndexOf("/?cmd=");
            if (s2f < 3) { HTTPClientSendError(cd.client, 403); return; };
            string user = query.Substring(0, s2f).ToUpper();
            if (!Buddie.BuddieNameRegex.IsMatch(user)) { HTTPClientSendError(cd.client, 403); return; };
            string cmd = query.Substring(s2f + 6);

            string[] pData = cmd.Split(new string[] { "," }, StringSplitOptions.None);
            if (pData.Length < 13) { HTTPClientSendError(cd.client, 417); return; };
            if (pData[2] != "_SendMessage") { HTTPClientSendError(cd.client, 417); return; };
            int pass = 0;
            if (!int.TryParse(pData[1], out pass)) { HTTPClientSendError(cd.client, 417); return; };
            if (Buddie.Hash(user) != pass) { HTTPClientSendError(cd.client, 403); return; };

            cd.user = user;

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
                b.lastPacket = cmd;
                OnNewData(b);
            }
            catch { HTTPClientSendError(cd.client, 417); return; };
        }

        private void OnPost(ClientData cd, string rxText)
        {
            cd.state = 3;
            int hi = rxText.IndexOf("HTTP");
            if (hi <= 0) { HTTPClientSendError(cd.client, 400); return; };
            string query = rxText.Substring(5, hi - 5).Trim();
            if (!IsValidQuery(query))
            {
                HTTPClientSendError(cd.client, 404);
                return;
            };
            if (query[10] == 'm')
            {
                OnMMT(cd, rxText);
                return;
            };
            HTTPClientSendError(cd.client, 403);
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

            cd.user = user;

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
                { HTTPClientSendError(cd.client, 415); return; };
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
                        b.lastPacket = ask.ToString();
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

        private bool CheckRegisteredUser(Buddie buddie)
        {
            if (regUsers != null)
                foreach (OruxPalsServerConfig.RegUser u in regUsers)
                {
                    bool found = false;
                    if (u.name == buddie.name) found = true;
                    if(!found)
                        if ((u.services != null) && (u.services.Length > 0))
                            foreach (OruxPalsServerConfig.RegUserSvc svc in u.services)
                                if ((svc.names != null) && (svc.names.Contains("A")) && (svc.id != null) && (svc.id == buddie.name))
                                    found = true;
                    if (found)
                    {
                        buddie.regUser = u;
                        if ((Buddie.IsNullIcon(buddie.IconSymbol)) && (u.aprssymbol != null))
                        {
                            buddie.IconSymbol = u.aprssymbol;
                            while (buddie.IconSymbol.Length < 1)
                                buddie.IconSymbol = "/" + buddie.IconSymbol;
                        };
                        return true;
                    };
                };
            return false;
        }
       
        private void OnNewData(Buddie buddie)
        {
            if(buddie.regUser == null)
                CheckRegisteredUser(buddie);

            if (BUDS != null)
                BUDS.Update(buddie);

            //forward data
            if ((buddie.regUser != null) && (buddie.regUser.forward != null) && (buddie.regUser.forward.Length > 0) && (buddie.regUser.services != null) && (buddie.regUser.services.Length > 0))
            {
                // forward to APRS Global
                if ((aprsgw != null) && (aprscfg.any2global == "yes") && (buddie.source != 3) && (buddie.regUser.forward.Contains("A")))
                {                    
                    foreach (OruxPalsServerConfig.RegUserSvc svc in buddie.regUser.services)
                        if (svc.names.Contains("A"))
                        {
                            string comment = "#ORXPLS" + buddie.source.ToString() + " ";
                            if (buddie.source == 1) comment = "#ORXPLSg ";
                            if (buddie.source == 2) comment = "#ORXPLSm ";
                            if (buddie.source == 3) comment = "#ORXPLSa ";
                            if (buddie.source == 4) comment = "#ORXPLSf ";
                            if (buddie.Comment != null) comment += buddie.Comment;
                            string aprs = buddie.APRS.Replace(buddie.name + ">", svc.id + ">").Replace("\r\n", " " + comment + "\r\n");
                            aprsgw.SendCommandWithDelay(svc.id, aprs);
                        };
                };

                // forward to Web services
                if((forwardServices != null) && (forwardServices.Length > 0))
                {
                    string toFwd = buddie.regUser.forward;
                    for (int i = 0; i < toFwd.Length; i++)
                    {
                        string l = toFwd[i].ToString();
                        string id = null;
                        foreach (OruxPalsServerConfig.RegUserSvc svc in buddie.regUser.services)
                            if (svc.names.Contains(l))
                                id = svc.id;
                        if (id != null)
                            foreach (OruxPalsServerConfig.FwdSvc fs in forwardServices)
                                if ((fs.name == l) && (fs.forward == "yes"))
                                    ForwardData2WebServices(fs, buddie, id);
                    };
                };
            };                      
        }

        public void BroadcastAIS(BroadCastInfo bdata)
        {
            Broadcast(bdata.data, bdata.user, true, false, false);
        }

        public void BroadcastAIS(byte[] data)
        {
            Broadcast(data, "", true, false, false);
        }

        public void BroadcastAPRS(BroadCastInfo bdata)
        {
            Broadcast(bdata.data, bdata.user, false, true, false);
        }

        public void BroadcastAPRS(byte[] data)
        {
            Broadcast(data, "", false, true, false);
        }

        public void BroadcastFRS(BroadCastInfo bdata)
        {
            Broadcast(bdata.data, bdata.user, false, false, true);
        }

        public void BroadcastFRS(byte[] data)
        {
            Broadcast(data, "", false, false, true);
        }

        public void PingFRS()
        {
            List<ClientData> cdlist = new List<ClientData>();
            lock (clientList)
                foreach (object obj in clientList.Values)
                {
                    if (obj == null) continue;
                    ClientData cd = (ClientData)obj;
                    if (cd.state == 5) // FRS
                        cdlist.Add(cd);
                };
            foreach (ClientData cd in cdlist)
            {
                string pmsg = ChecksumAdd2Line("$FRCMD," + cd.user + ",_Ping,Inline");
                byte[] data = Encoding.ASCII.GetBytes(pmsg + "\r\n");
                try { cd.client.GetStream().Write(data, 0, data.Length); }
                catch { };
            };
        }

        public void Broadcast(byte[] data, string fromUser, bool bAIS, bool bAPRS, bool bFRS)
        {
            List<ClientData> cdlist = new List<ClientData>();
            lock (clientList)
                foreach (object obj in clientList.Values)
                {
                    if (obj == null) continue;
                    ClientData cd = (ClientData)obj;
                    if ((cd.state == 1) && bAIS) // AIS readonly
                        cdlist.Add(cd);
                    if ((cd.state == 4)  && bAPRS) // APRS rx/tx
                    {
                        if (fromUser != cd.user)
                            if ((cd.filter != null) && (!cd.filter.PassName(fromUser)))
                                continue;

                        if (sendBack || (cd.state == 6))
                            cdlist.Add(cd);
                        else if (fromUser != cd.user)
                            cdlist.Add(cd);

                        //if (sendBack)
                        //    cdlist.Add(cd);
                        //else if (fromUser != cd.user)
                        //    cdlist.Add(cd);
                    };
                    if (!_NoSendToFRS)
                    {
                        if ((cd.state == 5) && bFRS) // FRS
                        {
                            if (sendBack)
                                cdlist.Add(cd);
                            else if (fromUser != cd.user)
                                cdlist.Add(cd);
                        };
                    };
                    if ((cd.state == 6) && bAPRS) // APRS readonly
                        cdlist.Add(cd);
                };

            foreach (ClientData cd in cdlist)
                try { cd.client.GetStream().Write(data, 0, data.Length); }
                catch { };
        }

        private void ForwardData2WebServices(OruxPalsServerConfig.FwdSvc svc, Buddie buddie, string id)
        {
            try
            {
                switch (svc.type)
                {
                    case "m":
                        {
                            // Meitrack GT60 packet
                            string[] x;// [IP,PORT]
                            x = svc.ipp.Split(new string[] { ":" }, StringSplitOptions.RemoveEmptyEntries);
                            SendTCP(x[0], Convert.ToInt32(x[1]), GetPacketText_Meitrack_GT60_Protocol(buddie, id));
                        };
                        break;
                    case "x":
                        {
                            // Xenun TK-102B packet
                            string[] x;// [IP,PORT]
                            x = svc.ipp.Split(new string[] { ":" }, StringSplitOptions.RemoveEmptyEntries);
                            SendTCP(x[0], Convert.ToInt32(x[1]), GetPacketText_TK102B_Normal(buddie, id));
                        };
                        break;
                    case "o":
                        {
                            // OpenGPS
                            SendHTTP(svc.ipp + GetPacketText_OpenGPSNET_HTTPReq(buddie, id));
                        };
                        break;
                };
            }
            catch { };
        }

        private bool IsValidQuery(string query)
        {
            if (query.Length < 11) return false;
            string subQuery = query.Substring(0, 10).ToLower();
            if (subQuery != urlPath) return false;
            return true;
        }

        private static string ChecksumHex(string str)
        {
            int checksum = 0;
            for (int i = 1; i < str.Length; i++)
                checksum ^= Convert.ToByte(str[i]);
            return checksum.ToString("X2");
        }

        public static string ChecksumAdd2Line(string line)
        {
            return line + "*" + ChecksumHex(line);
        }

        private static string GetPacketText_Meitrack_GT60_Protocol(Buddie tr, string id)
        {
            // Meitrack GT60 Protocol
            //   $$<packageflag><L>,<IMEI>,<command>,<event_code>,<(-)yy.dddddd>,<(-)xxx.dddddd>,<yymmddHHMMSS>,
            //      <Z(A-ok/V-bad)>,<N(sat count)>,<G(GSM signal)>,<Speed>,<Heading>,<HDOP>,<Altitude>,<Journey>,<Runtime>,<Base ID>,<State>,<AD>,<*checksum>\r\n 
            //   $$A,IMEI,AAA,35,55.450000,037,390000,140214040000,
            //      A,5,60,359,5,118,0,0,MCC|MNC|LAC|CI(460|0|E166|A08B),0000,0,<*checksum>\r\n 
            //
            // Example packet length & checksum: 
            //   $$E28,353358017784062,A15,OK*F4\r\n 

            string packet_prefix = "$$A";
            string packet_data = "," + id + ",AAA,35," + tr.lat.ToString("00.000000").Replace(",", ".") + "," + tr.lon.ToString("000.000000").Replace(",", ".") + "," + DateTime.UtcNow.ToString("yyMMddHHmmss") + "," +
                "A,5," +/*GSM SIGNAL*/DateTime.UtcNow.ToString("HHmmss") + "," + ((int)tr.speed).ToString() + "," + ((int)tr.course).ToString() + ",5,0,0,0," +
                // base
                ",0000,0," + "*";
            string checksum_packet = packet_prefix + (packet_data.Length + 4).ToString() + packet_data;
            byte cs = 0;
            for (int i = 0; i < checksum_packet.Length; i++) cs += (byte)checksum_packet[i];
            string full_data = checksum_packet + cs.ToString("X") + "\r\n";

            return full_data;
        }

        private static string GetPacketText_TK102B_Normal(Buddie tr, string id)
        {
            return GetPacketText_TK102B_Normal(DateTime.UtcNow, id, tr.lat, tr.lon, tr.speed, tr.course, 0);
        }

        private static string GetPacketText_OpenGPSNET_HTTPReq(Buddie tr, string id)
        {
            return GetPacketText_OpenGPSNET_HTTPReq(id, tr.lat, tr.lon, tr.speed, tr.course, 0);
        }

        private static string GetPacketText_OpenGPSNET_HTTPReq(string imei, double lat, double lon, double speed, double heading, double altitude)
        {
            Random rnd = new Random();

            if (lat == 0) lat = 55.54404 + (0.5 - rnd.NextDouble()) / 10;
            if (lon == 0) lon = 37.55860 + (0.5 - rnd.NextDouble()) / 10;
            if (speed < 0) speed = 10; // kmph
            if (heading < 0) heading = rnd.Next(0, 359);
            if (altitude < 0) altitude = 251;

            //http://www.opengps.net/configure.php
            return
                "&imei=" + imei + "&data=" +
                DateTime.UtcNow.ToString("HHmmss") + ".000," +
                Math.Truncate(lat).ToString("00") + ((lat - Math.Truncate(lat)) * 60).ToString("00.0000").Replace(",", ".") + "N," + // Lat
                Math.Truncate(lon).ToString("000") + ((lon - Math.Truncate(lon)) * 60).ToString("00.0000").Replace(",", ".") + "E," + // Lon
                "2.6," + // HDOP
                altitude.ToString("0.0").Replace(",", ".") + "," + // altitude
                "3," + // 0 - noFix, 2-2D,3-3D
                heading.ToString("000.00").Replace(",", ".") + "," + //heading
                speed.ToString("0.0").Replace(",", ".") + "," + // kmph
                (speed / 1.852).ToString("0.0").Replace(",", ".") + "," + // knots
                DateTime.UtcNow.ToString("ddMMyy") + "," + // date
                "12" // sat count
                ;
            ;
        }

        private static string GetPacketText_TK102B_Normal(DateTime dt, string imei, double lat, double lon, double speed, double heading, double altitude)
        {
            Random rnd = new Random();

            if (lat == 0) lat = 55.54404 + (0.5 - rnd.NextDouble()) / 10;
            if (lon == 0) lon = 37.55860 + (0.5 - rnd.NextDouble()) / 10;
            if (speed < 0) speed = 10; // kmph
            if (heading < 0) heading = rnd.Next(0, 359);
            if (altitude < 0) altitude = 251;
            return
                dt.ToString("yyMMddHHmmss") + "," + //Serial no.(year, month, date, hour, minute, second )
                "0," + // Authorized phone no.
                "GPRMC," + // begin GPRMC sentence
                dt.ToString("HHmmss") + ".000,A," + // Time
                Math.Truncate(lat).ToString("00") + ((lat - Math.Truncate(lat)) * 60).ToString("00.0000").Replace(",", ".") + ",N," + // Lat
                Math.Truncate(lon).ToString("000") + ((lon - Math.Truncate(lon)) * 60).ToString("00.0000").Replace(",", ".") + ",E," + // Lon
                (speed / 1.852).ToString("0.00").Replace(",", ".") + "," +//Speed in knots
                heading.ToString("0").Replace(",", ".") + "," +//heading
                dt.ToString("ddMMyy") + ",,,A*62," +// Date
                "F," +//F=GPS signal is full, if it indicate " L ", means GPS signal is low
                "imei:" + imei + "," + //imei
                // CRC
                "05," +// GPS fix (03..10)
                altitude.ToString("0.0").Replace(",", ".") //altitude
                //",F:3.79V,0"//0-tracker not charged,1-charged
                // ",122,13990,310,01,0AB0,345A" //
            ;

            // lat: 5722.5915 -> 57 + (22.5915 / 60) = 57.376525
        }

        private static void SendTCP(string IP, int Port, string data)
        {
            try
            {
                TcpClient tc = new TcpClient();
                tc.Connect(IP, Port);
                byte[] buf = System.Text.Encoding.GetEncoding(1251).GetBytes(data);
                tc.GetStream().Write(buf, 0, buf.Length);
                tc.Close();
            }
            catch (Exception ex) { throw ex; };
        }

        private static void SendHTTP(string query)
        {
            try
            {
                System.Net.HttpWebRequest wr = (System.Net.HttpWebRequest)System.Net.HttpWebRequest.Create(query);
                System.Net.WebResponse rp = wr.GetResponse();
                System.IO.Stream ss = rp.GetResponseStream();
                System.IO.StreamReader sr = new System.IO.StreamReader(ss);
                string rte = sr.ReadToEnd();
                sr.Close();
                ss.Close();
                rp.Close();
            }
            catch (Exception ex) { throw ex; };
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

        private static void HTTPClientSendFile(TcpClient Client, string fileName, string subdir)
        {
            string ffn = OruxPalsServerConfig.GetCurrentDir() + @"\" + subdir + @"\" + fileName.Replace("..", "00").Replace("%20"," ");
            if (!File.Exists(ffn))
            {
                HTTPClientSendError(Client, 404);
                return;
            };

            string ctype = "text/html; charset=utf-8";
            System.IO.FileStream fs = new FileStream(ffn, FileMode.Open, FileAccess.Read);
            string ext = Path.GetExtension(ffn).ToLower();
            if (ext == ".css") ctype = "";// "text/css; charset=windows-1251";
            if (ext == ".js") ctype = "text/javascript; charset=windows-1251";
            if (ext == ".png") ctype = "image/png";
            if (ext == ".gif") ctype = "image/gif";
            if ((ext == ".txt") || (ext == ".csv") || (ext == ".readme")) ctype = "text/plain";
            if ((ext == ".jpg") || (ext == ".jpeg")) ctype = "image/jpeg";
            if ((ext == ".xml") || (ext == ".kml")) ctype = "text/xml; charset=utf-8";
            if ((ext == ".apk") || (ext == ".exe") || (ext == ".rar") || (ext == ".zip") || (ext == ".bin")) ctype = "application/octet-stream";

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

        private static void HTTPClientSendError(TcpClient Client, int Code)
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

        public static string HeadingToText(int hdg)
        {
            int d = (int)Math.Round(hdg / 22.5);
            switch (d)
            {
                case 0: return "N";
                case 1: return "NNE";
                case 2: return "NE";
                case 3: return "NEE";
                case 4: return "E";
                case 5: return "SEE";
                case 6: return "SE";
                case 7: return "SSE";
                case 8: return "S";
                case 9: return "SSW";
                case 10: return "SW";
                case 11: return "SWW";
                case 12: return "W";
                case 13: return "NWW";
                case 14: return "NW";
                case 15: return "NNW";
                case 16: return "N";
                default: return "";
            };
        }

        private class ClientData
        {
            public byte state; // 0 - undefined; 1 - listen (AIS); 2 - gpsgate; 3 - mapmytracks; 4 - APRS; 5 - FRS (GPSGate by TCP); 6 - listen (APRS)
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

            public string user = "unknown";
            public double[] lastFixYX = new double[] { 0, 0, 0 };

            public ClientAPRSFilter filter = null;

            public string SetFilter(string filter)
            {
                this.filter = new ClientAPRSFilter(filter);
                return this.filter.ToString();
            }
        }

        public class ClientAPRSFilter
        {
            private string filter = "";
            public int inMyRadiusKM = -1;
            public int maxStaticObjectsCount = -1;
            public string[] allowStartsWith = new string[0];
            public string[] allowEndsWith = new string[0];
            public string[] allowFullName = new string[0];
            public string[] denyStartsWith = new string[0];
            public string[] denyEndsWith = new string[0];
            public string[] denyFullName = new string[0];

            public ClientAPRSFilter(string filter)
            {
                this.filter = filter;
                Init();
            }

            private void Init()
            {
                string ffparsed = "";
                Match m = Regex.Match(filter, @"me/([\d\/]+)");
                if (m.Success)
                {
                    string[] rc = m.Groups[1].Value.Split(new char[] { '/' }, 2);
                    if (rc.Length > 0)
                    {
                        inMyRadiusKM = int.Parse(rc[0]);
                        if (rc.Length > 1) maxStaticObjectsCount = int.Parse(rc[1]);
                        ffparsed += m.ToString() + " ";
                    };
                };
                m = Regex.Match(filter, @"\+sw/([A-Z\d/\-]+)");
                if (m.Success) 
                {
                    allowStartsWith = m.Groups[1].Value.Split(new char[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                    ffparsed += m.ToString() + " ";
                };
                m = Regex.Match(filter, @"\+ew/([A-Z\d/\-]+)");
                if (m.Success) 
                {
                    allowEndsWith = m.Groups[1].Value.Split(new char[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                    ffparsed += m.ToString() + " ";
                };
                m = Regex.Match(filter, @"\+fn/([A-Z\d/\-]+)");
                if (m.Success) 
                {
                    allowFullName = m.Groups[1].Value.Split(new char[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                    ffparsed += m.ToString() + " ";
                };
                m = Regex.Match(filter, @"\-sw/([A-Z\d/\-]+)");
                if (m.Success) 
                {
                    denyStartsWith = m.Groups[1].Value.Split(new char[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                    ffparsed += m.ToString() + " ";
                };
                m = Regex.Match(filter, @"\-ew/([A-Z\d/\-]+)");
                if (m.Success) 
                {
                    denyEndsWith = m.Groups[1].Value.Split(new char[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                    ffparsed += m.ToString() + " ";
                };
                m = Regex.Match(filter, @"\-fn/([A-Z\d/\-]+)");
                if (m.Success) 
                {
                    denyFullName = m.Groups[1].Value.Split(new char[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                    ffparsed += m.ToString() + " ";
                };
                filter = ffparsed.Trim();
            }

            public bool PassName(string name)
            {
                if((name == null)||(name == "")) return true;
                if (filter == "") return true;
                name = name.ToUpper();
                bool pass = true;
                if ((allowStartsWith != null) && (allowStartsWith.Length > 0))
                {
                    pass = false;
                    foreach (string sw in allowStartsWith)
                        if (name.StartsWith(sw)) return true;
                };
                if ((allowEndsWith != null) && (allowEndsWith.Length > 0))
                {
                    pass = false;
                    foreach (string ew in allowEndsWith)
                        if (name.EndsWith(ew)) return true;
                };
                if ((allowFullName != null) && (allowFullName.Length > 0))
                {
                    pass = false;
                    foreach (string fn in allowFullName)
                        if (name == fn)
                            return true;
                };
                //
                if ((denyStartsWith != null) && (denyStartsWith.Length > 0))
                    foreach (string sw in denyStartsWith)
                        if (name.StartsWith(sw)) return false;
                if ((denyEndsWith != null) && (denyEndsWith.Length > 0))
                    foreach (string ew in denyEndsWith)
                        if (name.EndsWith(ew)) return false;
                if ((denyFullName != null) && (denyFullName.Length > 0))
                    foreach (string fn in denyFullName)
                        if (name == fn)
                            return false;
                return pass;
            }

            public override string ToString()
            {
                return filter;
            }
        }
    }

    [Serializable]
    public class OruxPalsServerConfig
    {
        public class RegUserSvc
        {
            [XmlAttribute]
            public string names = "";
            [XmlAttribute]
            public string id = "";
        }

        public class RegUsers
        {            
            [XmlElement("u")]
            public RegUser[] users;
        }

        public class RegUser
        {
            [XmlAttribute]
            public string name;
            [XmlAttribute]
            public string phone;
            [XmlAttribute]
            public string forward;
            [XmlAttribute]
            public string aprssymbol = "/>";
            [XmlAttribute]
            public string comment = "";            
            [XmlElement("service")]
            public RegUserSvc[] services;
        }

        public class FwdSvcs
        {
            [XmlElement("service")]
            public FwdSvc[] services;
        }

        public class FwdSvc
        {
            [XmlAttribute]
            public string name = "";
            [XmlAttribute]
            public string type = "?";
            [XmlAttribute]
            public string forward = "no";
            [XmlText]
            public string ipp = "127.0.0.1:0";
        }

        public string ServerName = "OruxPalsServer";
        public int ListenPort = 12015;
        public ushort maxClientAlive = 60;
        public byte maxHours = 48;
        public ushort greenMinutes = 60;
        public int KMLObjectsRadius = 5;
        public int KMLObjectsLimit = 50;
        public string urlPath = "oruxpals";
        public string adminName = "ADMIN";
        public string sendBack = "no";
        public string callsignToUser = "yes";
        public string infoIP = "127.0.0.1";
        public string banlist = "";
        [XmlElement("users")]
        public RegUsers users;
        [XmlElement("APRSIS")]
        public APRSISConfig aprsis;
        [XmlElement("forwardServices")]
        public FwdSvcs forwardServices;

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
