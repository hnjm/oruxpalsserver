using System;
using System.IO;
using System.Threading;
using System.Net;
using System.Net.Sockets;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Serialization;

namespace OruxPals
{
    public class Buddies
    {        
        private List<Buddie> buddies = new List<Buddie>();
        private List<byte[]> broadcast = new List<byte[]>();

        private bool keepAlive = true;

        private byte maxHours = 48;
        private ushort greenMinutes = 60;

        public delegate void BroadcastMethod(byte[] data);
        public BroadcastMethod onBroadcast;

        public Buddies(byte maxHours, ushort greenMinutes)
        {
            this.maxHours = maxHours;
            this.greenMinutes = greenMinutes;

            (new Thread(ClearThread)).Start();
            (new Thread(BroadcastThread)).Start();
        }

        public void Dispose()
        {
            keepAlive = false;
        }

        ~Buddies()
        {
            Dispose();
        }

        public void Update(Buddie buddie)
        {
            if(buddie == null) return;            

            lock (buddies)
            {
                if (buddies.Count > 0)
                    for (int i = buddies.Count - 1; i >= 0; i--)
                        if (buddie.name == buddies[i].name)
                            buddies.RemoveAt(i);

                buddies.Add(buddie);                
            };

            buddie.SetAIS();
            
            lock (broadcast) 
                broadcast.Add(buddie.AISNMEA);
        }

        public Buddie[] Current
        {
            get
            {
                lock(buddies)
                    return buddies.ToArray();
            }
        }

        public Buddie GetBuddie(string name)
        {
            lock (buddies)
            {
                if (buddies.Count > 0)
                    for (int i = buddies.Count - 1; i >= 0; i--)
                        if (name == buddies[i].name)
                            return buddies[i];
            };
            return null;
        }

        public void Clear()
        {
            lock (buddies) buddies.Clear();
        }

        private void ClearThread()
        {
            byte counter = 0;
            while (keepAlive)
            {
                if (++counter == 15)
                {
                    lock (buddies)
                        if (buddies.Count > 0)
                            for (int i = buddies.Count - 1; i >= 0; i--)
                            {
                                if (!buddies[i].green)
                                    if (DateTime.UtcNow.Subtract(buddies[i].last).TotalMinutes >= greenMinutes)
                                        buddies[i].green = true;

                                if (DateTime.UtcNow.Subtract(buddies[i].last).TotalHours >= maxHours)
                                    buddies.RemoveAt(i);
                            };
                    counter = 0;
                };
                Thread.Sleep(1000);
            };
        }

        private void BroadcastThread()
        {
            while (keepAlive)
            {
                int bc = broadcast.Count;
                while (bc > 0)
                {
                    byte[] data;
                    lock (broadcast)
                    {
                        data = broadcast[0];
                        broadcast.RemoveAt(0);                        
                    };
                    bc--;
                    BroadcastData(data);
                };
                Thread.Sleep(1000);
            };
        }

        private void BroadcastData(byte[] data)
        {
            if (onBroadcast != null)
                onBroadcast(data);
        }
    }

    public class Buddie
    {
        public static Regex BuddieNameRegex = new Regex("^([A-Z0-9]{3,9})$");
        
        public byte source; // 0 - unknown; 1 - GPSGate Format; 2 - MapMyTracks Format
        public string name;                        
        public double lat;
        public double lon;
        public short speed;
        public short course;

        public DateTime last;        
        public bool green;

        private string aAIS = "";
        private byte[] aAISNMEA = null;
        private string bAIS = "";
        private byte[] bAISNMEA = null;

        public string AIS
        {
            get
            {
                return green ? bAIS : aAIS;
            }
        }
        public byte[] AISNMEA
        {
            get
            {
                return green ? bAISNMEA : aAISNMEA;
            }
        }

        public object data;

        public Buddie(byte source, string name, double lat, double lon, short speed, short course)
        {
            this.source = source;
            this.name = name;            
            this.lat = lat;
            this.lon = lon;
            this.speed = speed;
            this.course = course;
            this.last = DateTime.UtcNow;
            this.green = false;
        }

        internal void SetAIS()
        {
            CNBAsentense a = CNBAsentense.FromBuddie(this);            
            string ln1 = "!AIVDM,1,1,,A," + a.ToString() + ",0";
            ln1 += "*" + AISTransCoder.Checksum(ln1);
            AIVDMSentense ai = AIVDMSentense.FromBuddie(this);
            string ln2 = "!AIVDM,1,1,,A," + ai.ToString() + ",0";
            ln2 += "*" + AISTransCoder.Checksum(ln2);
            aAIS = ln1 + "\r\n" + ln2 + "\r\n";
            aAISNMEA = Encoding.ASCII.GetBytes(aAIS);

            CNBBEsentense be = CNBBEsentense.FromBuddie(this);
            string ln0 = "!AIVDM,1,1,,A," + be.ToString() + ",0";
            ln0 += "*" + AISTransCoder.Checksum(ln0);
            bAIS = ln0 + "\r\n";
            bAISNMEA = Encoding.ASCII.GetBytes(bAIS);
        }

        public override string ToString()
        {
            return String.Format("{0} at {1}, {2} {3} {4}, {5}", new object[] { name, source, lat, lon, speed, course });
        }

        public static int Hash(string name)
        {
            string upname = name == null ? "" : name;
            while (upname.Length < 9) upname += " ";
            int hash = 0x2017;
            int i = 0;
            while (i < 9)
            {
                hash ^= (int)(upname.Substring(i, 1))[0] << 16;
                hash ^= (int)(upname.Substring(i + 1, 1))[0] << 8;
                hash ^= (int)(upname.Substring(i + 2, 1))[0];
                i += 3;
            };
            return hash & 0x7FFFFF;
        }

        public static uint MMSI(string name)
        {
            string upname = name == null ? "" : name;
            while (upname.Length < 9) upname += " ";
            int hash = 2017;
            int i = 0;
            while (i < 9)
            {
                hash ^= (int)(upname.Substring(i, 1))[0] << 16;
                hash ^= (int)(upname.Substring(i + 1, 1))[0] << 8;
                hash ^= (int)(upname.Substring(i + 2, 1))[0];
                i += 3;
            };
            return (uint)(hash & 0xFFFFFF);
        }
    }

    public enum ShipType : int
    {
        Default = 0,
        WIG_AllShips = 20,
        Fishing = 30,
        Towing = 31,
        TowingBig = 32,
        DredgingOrUnderwater = 33,
        Diving = 34,
        Military = 35,
        Sailing = 36,
        PleasureCraft = 37,
        HighSpeedCraft_All = 40,
        HighSpeedCraft_A = 41,
        HighSpeedCraft_B = 42,
        HighSpeedCraft_NoInfo = 49,
        PilotVessel = 50,
        SearchRescue = 51,
        Tug = 52,
        PortTender = 53,
        MedicalTransport = 58,
        Passenger_All = 60,
        Passenger_A = 61,
        Passenger_B = 62,
        Passenger_NoInfo = 69,
        Cargo_All = 70,
        Cargo_A = 71,
        Cargo_B = 72,
        Cargo_NoInfo = 79,
        Tanker_All = 80,
        Tanker_A = 81,
        Tanker_B = 82,
        Tanker_NoInfo = 89
    }

    // 1, 2, 3
    public class CNBAsentense
    {
        private const byte length = 168;

        public uint MMSI;
        private uint NavigationStatus = 15;
        private int ROT = 0;
        public uint SOG = 0;
        public bool Accuracy = false;
        public double Longitude = 0;
        public double Latitude = 0;
        public double COG = 0;
        public ushort HDG = 0;
        private uint TimeStamp = 60;
        private uint ManeuverIndicator = 1;
        private uint RadioStatus = 0;

        public static CNBAsentense FromAIS(byte[] unpackedBytes)
        {
            CNBAsentense res = new CNBAsentense();
            int stype = AISTransCoder.GetBitsAsUnsignedInt(unpackedBytes, 0, 6);
            if ((stype < 1) || (stype > 3)) return null;

            res.MMSI = (uint)AISTransCoder.GetBitsAsUnsignedInt(unpackedBytes, 8, 30);
            res.NavigationStatus = (uint)AISTransCoder.GetBitsAsUnsignedInt(unpackedBytes, 38, 4);
            res.ROT = AISTransCoder.GetBitsAsSignedInt(unpackedBytes, 42, 8);
            res.SOG = (uint)(AISTransCoder.GetBitsAsUnsignedInt(unpackedBytes, 50, 10) / 10 * 1.852);
            res.Accuracy = (byte)AISTransCoder.GetBitsAsUnsignedInt(unpackedBytes, 60, 1) == 1 ? true : false;
            res.Longitude = AISTransCoder.GetBitsAsSignedInt(unpackedBytes, 61, 28) / 600000.0;
            res.Latitude = AISTransCoder.GetBitsAsSignedInt(unpackedBytes, 89, 27) / 600000.0;
            res.COG = AISTransCoder.GetBitsAsUnsignedInt(unpackedBytes, 116, 12) / 10.0;
            res.HDG = (ushort)AISTransCoder.GetBitsAsUnsignedInt(unpackedBytes, 128, 9);
            res.TimeStamp = (uint)AISTransCoder.GetBitsAsUnsignedInt(unpackedBytes, 137, 6);
            res.ManeuverIndicator = (uint)AISTransCoder.GetBitsAsUnsignedInt(unpackedBytes, 143, 2);
            res.RadioStatus = (uint)AISTransCoder.GetBitsAsUnsignedInt(unpackedBytes, 149, 19);
            return res;
        }

        public static CNBAsentense FromAIS(string ais)
        {
            byte[] unp = AISTransCoder.UnpackAISString(ais);
            return FromAIS(unp);
        }

        public static CNBAsentense FromBuddie(Buddie buddie)
        {
            CNBAsentense res = new CNBAsentense();
            res.ROT = buddie.source;
            res.Accuracy = true;
            res.Latitude = buddie.lat;
            res.Longitude = buddie.lon;
            res.COG = res.HDG = (ushort)buddie.course;
            res.SOG = (uint)buddie.speed;
            res.MMSI = Buddie.MMSI(buddie.name);
            return res;
        }

        public byte[] ToAIS()
        {
            byte[] unpackedBytes = new byte[21];
            AISTransCoder.SetBitsAsUnsignedInt(unpackedBytes, 0, 6, 3); // type
            AISTransCoder.SetBitsAsUnsignedInt(unpackedBytes, 6, 2, 0); // repeat
            AISTransCoder.SetBitsAsUnsignedInt(unpackedBytes, 8, 30, (int)MMSI);
            AISTransCoder.SetBitsAsUnsignedInt(unpackedBytes, 38, 4, (int)NavigationStatus);
            AISTransCoder.SetBitsAsSignedInt(unpackedBytes, 42, 8, (int)ROT);
            AISTransCoder.SetBitsAsUnsignedInt(unpackedBytes, 50, 10, (int)(SOG / 1.852 * 10)); // speed                                                
            AISTransCoder.SetBitsAsUnsignedInt(unpackedBytes, 60, 1, Accuracy ? 1 : 0);
            AISTransCoder.SetBitsAsSignedInt(unpackedBytes, 61, 28, (int)(Longitude * 600000));
            AISTransCoder.SetBitsAsSignedInt(unpackedBytes, 89, 27, (int)(Latitude * 600000));
            AISTransCoder.SetBitsAsUnsignedInt(unpackedBytes, 116, 12, (int)(COG * 10)); // course
            AISTransCoder.SetBitsAsUnsignedInt(unpackedBytes, 128, 9, (int)HDG); // heading
            AISTransCoder.SetBitsAsUnsignedInt(unpackedBytes, 137, 6, (int)TimeStamp); // timestamp (not available (default))
            AISTransCoder.SetBitsAsUnsignedInt(unpackedBytes, 143, 2, (int)ManeuverIndicator); // no Maneuver 
            AISTransCoder.SetBitsAsUnsignedInt(unpackedBytes, 149, 19, (int)RadioStatus);
            return unpackedBytes;
        }

        public override string ToString()
        {
            return AISTransCoder.EnpackAISString(ToAIS());
        }
    }

    // 5
    public class AIVDMSentense
    {
        private const short length = 424;

        public uint MMSI;
        public uint IMOShipID;
        public string CallSign;
        public string VesselName;
        public int ShipType = 0;
        public string Destination = "";

        public static AIVDMSentense FromAIS(byte[] unpackedBytes)
        {
            AIVDMSentense res = new AIVDMSentense();
            int stype = AISTransCoder.GetBitsAsUnsignedInt(unpackedBytes, 0, 6);
            if (stype != 5) return null;
            
            res.MMSI = (uint)AISTransCoder.GetBitsAsUnsignedInt(unpackedBytes, 8, 30);
            res.IMOShipID = (uint)AISTransCoder.GetBitsAsUnsignedInt(unpackedBytes, 40, 30);
            res.CallSign = AISTransCoder.GetAisString(unpackedBytes, 70, 42);
            res.VesselName = AISTransCoder.GetAisString(unpackedBytes, 112, 120);
            res.ShipType = AISTransCoder.GetBitsAsUnsignedInt(unpackedBytes, 232, 8);
            res.Destination = AISTransCoder.GetAisString(unpackedBytes, 302, 120);

            return res;
        }

        public static AIVDMSentense FromAIS(string ais)
        {
            byte[] unp = AISTransCoder.UnpackAISString(ais);
            return FromAIS(unp);
        }

        public static AIVDMSentense FromBuddie(Buddie buddie)
        {
            AIVDMSentense res = new AIVDMSentense();
            res.CallSign = res.VesselName = buddie.name;
            res.Destination = DateTime.Now.ToString("HHmmss ddMMyy");            
            res.ShipType = 0;
            res.MMSI = res.IMOShipID = Buddie.MMSI(buddie.name);
            return res;
        }

        public byte[] ToAIS()
        {
            byte[] unpackedBytes = new byte[54];
            AISTransCoder.SetBitsAsUnsignedInt(unpackedBytes, 0, 6, 5);
            AISTransCoder.SetBitsAsUnsignedInt(unpackedBytes, 6, 2, 0);
            AISTransCoder.SetBitsAsUnsignedInt(unpackedBytes, 8, 30, (int)MMSI);
            AISTransCoder.SetBitsAsUnsignedInt(unpackedBytes, 38, 2, 0);
            AISTransCoder.SetBitsAsUnsignedInt(unpackedBytes, 40, 30, (int)IMOShipID);
            AISTransCoder.SetAisString(unpackedBytes, 70, 42, CallSign);
            AISTransCoder.SetAisString(unpackedBytes, 112, 120, VesselName);
            AISTransCoder.SetBitsAsUnsignedInt(unpackedBytes, 232, 8, (int)ShipType);
            AISTransCoder.SetBitsAsUnsignedInt(unpackedBytes, 240, 9, 4); //A
            AISTransCoder.SetBitsAsUnsignedInt(unpackedBytes, 249, 9, 1); //B
            AISTransCoder.SetBitsAsUnsignedInt(unpackedBytes, 258, 6, 1); //C
            AISTransCoder.SetBitsAsUnsignedInt(unpackedBytes, 264, 6, 2); //D
            AISTransCoder.SetBitsAsUnsignedInt(unpackedBytes, 270, 4, 1); //PostFix
            AISTransCoder.SetBitsAsUnsignedInt(unpackedBytes, 274, 4, DateTime.UtcNow.Month);
            AISTransCoder.SetBitsAsUnsignedInt(unpackedBytes, 278, 5, DateTime.UtcNow.Day);
            AISTransCoder.SetBitsAsUnsignedInt(unpackedBytes, 283, 5, DateTime.UtcNow.Hour);
            AISTransCoder.SetBitsAsUnsignedInt(unpackedBytes, 288, 6, DateTime.UtcNow.Minute);
            AISTransCoder.SetAisString(unpackedBytes, 302, 120, Destination);
            AISTransCoder.SetBitsAsUnsignedInt(unpackedBytes, 422, 1, 0);
            return unpackedBytes;
        }

        public override string ToString()
        {
            return AISTransCoder.EnpackAISString(ToAIS());
        }
    }

    // 18
    public class CNBBsentense
    {
        private const byte length = 168;

        public uint MMSI;
        public uint SOG;
        public bool Accuracy;
        public double Longitude;
        public double Latitude;
        public double COG = 0;
        public ushort HDG = 0;
        private uint TimeStamp = 60;

        public static CNBBsentense FromAIS(byte[] unpackedBytes)
        {
            CNBBsentense res = new CNBBsentense();
            int stype = AISTransCoder.GetBitsAsUnsignedInt(unpackedBytes, 0, 6);
            if (stype != 18) return null;

            res.MMSI = (uint)AISTransCoder.GetBitsAsUnsignedInt(unpackedBytes, 8, 30);
            res.SOG = (uint)(AISTransCoder.GetBitsAsUnsignedInt(unpackedBytes, 46, 10) / 10 * 1.852);
            res.Accuracy = (byte)AISTransCoder.GetBitsAsUnsignedInt(unpackedBytes, 56, 1) == 1 ? true : false;
            res.Longitude = AISTransCoder.GetBitsAsSignedInt(unpackedBytes, 57, 28) / 600000.0;
            res.Latitude = AISTransCoder.GetBitsAsSignedInt(unpackedBytes, 85, 27) / 600000.0;
            res.COG = AISTransCoder.GetBitsAsUnsignedInt(unpackedBytes, 112, 12) / 10.0;
            res.HDG = (ushort)AISTransCoder.GetBitsAsUnsignedInt(unpackedBytes, 124, 9);
            res.TimeStamp = (uint)AISTransCoder.GetBitsAsUnsignedInt(unpackedBytes, 133, 6);
            return res;
        }

        public static CNBBsentense FromAIS(string ais)
        {
            byte[] unp = AISTransCoder.UnpackAISString(ais);
            return FromAIS(unp);
        }

        public static CNBBsentense FromBuddie(Buddie buddie)
        {
            CNBBsentense res = new CNBBsentense();
            res.Accuracy = true;
            res.COG = res.HDG = (ushort)buddie.speed;
            res.Latitude = buddie.lat;
            res.Longitude = buddie.lon;
            res.SOG = (uint)buddie.speed;
            res.MMSI = Buddie.MMSI(buddie.name);
            return res;
        }

        public byte[] ToAIS()
        {
            byte[] unpackedBytes = new byte[21];
            AISTransCoder.SetBitsAsUnsignedInt(unpackedBytes, 0, 6, 18); // type
            AISTransCoder.SetBitsAsUnsignedInt(unpackedBytes, 6, 2, 0);
            AISTransCoder.SetBitsAsUnsignedInt(unpackedBytes, 8, 30, (int)MMSI);
            AISTransCoder.SetBitsAsUnsignedInt(unpackedBytes, 46, 10, (int)(SOG / 1.852 * 10)); // speed            
            AISTransCoder.SetBitsAsUnsignedInt(unpackedBytes, 56, 1, Accuracy ? 1 : 0);
            AISTransCoder.SetBitsAsSignedInt(unpackedBytes, 57, 28, (int)(Longitude * 600000));
            AISTransCoder.SetBitsAsSignedInt(unpackedBytes, 85, 27, (int)(Latitude * 600000));
            AISTransCoder.SetBitsAsUnsignedInt(unpackedBytes, 112, 12, (int)(COG * 10.0));
            AISTransCoder.SetBitsAsUnsignedInt(unpackedBytes, 124, 9, HDG);
            AISTransCoder.SetBitsAsUnsignedInt(unpackedBytes, 133, 6, 60);
            AISTransCoder.SetBitsAsUnsignedInt(unpackedBytes, 142, 1, 1);
            return unpackedBytes;
        }

        public override string ToString()
        {
            return AISTransCoder.EnpackAISString(ToAIS());
        }

    }

    // 19
    public class CNBBEsentense
    {
        private const short length = 312;

        public uint MMSI;
        public uint SOG;
        public bool Accuracy;
        public double Longitude;
        public double Latitude;
        public double COG = 0;
        public ushort HDG = 0;
        private uint Timestamp = 60;
        public string VesselName;
        public int ShipType = 0;

        public static CNBBEsentense FromAIS(byte[] unpackedBytes)
        {
            CNBBEsentense res = new CNBBEsentense();
            int stype = AISTransCoder.GetBitsAsUnsignedInt(unpackedBytes, 0, 6);
            if (stype != 19) return null;

            res.MMSI = (uint)AISTransCoder.GetBitsAsUnsignedInt(unpackedBytes, 8, 30);
            res.SOG = (uint)(AISTransCoder.GetBitsAsUnsignedInt(unpackedBytes, 46, 10) / 10 * 1.852);
            res.Accuracy = (byte)AISTransCoder.GetBitsAsUnsignedInt(unpackedBytes, 56, 1) == 1 ? true : false;
            res.Longitude = AISTransCoder.GetBitsAsSignedInt(unpackedBytes, 57, 28) / 600000.0;
            res.Latitude = AISTransCoder.GetBitsAsSignedInt(unpackedBytes, 85, 27) / 600000.0;
            res.COG = AISTransCoder.GetBitsAsUnsignedInt(unpackedBytes, 112, 12) / 10.0;
            res.HDG = (ushort)AISTransCoder.GetBitsAsUnsignedInt(unpackedBytes, 124, 9);
            res.Timestamp = (uint)AISTransCoder.GetBitsAsUnsignedInt(unpackedBytes, 133, 6);
            res.VesselName = AISTransCoder.GetAisString(unpackedBytes, 143, 120);
            res.ShipType = AISTransCoder.GetBitsAsUnsignedInt(unpackedBytes, 263, 8);
            AISTransCoder.SetBitsAsUnsignedInt(unpackedBytes, 271, 9, 4); // A
            AISTransCoder.SetBitsAsUnsignedInt(unpackedBytes, 280, 9, 1); // B
            AISTransCoder.SetBitsAsUnsignedInt(unpackedBytes, 289, 6, 1); // C
            AISTransCoder.SetBitsAsUnsignedInt(unpackedBytes, 295, 6, 2); // D
            AISTransCoder.SetBitsAsUnsignedInt(unpackedBytes, 301, 4, 1);
            AISTransCoder.SetBitsAsUnsignedInt(unpackedBytes, 306, 6, 1);
            return res;
        }

        public static CNBBEsentense FromAIS(string ais)
        {
            byte[] unp = AISTransCoder.UnpackAISString(ais);
            return FromAIS(unp);
        }

        public static CNBBEsentense FromBuddie(Buddie buddie)
        {
            CNBBEsentense res = new CNBBEsentense();
            res.Accuracy = true;
            res.COG = res.HDG = (ushort)buddie.course;
            res.Latitude = buddie.lat;
            res.Longitude = buddie.lon;
            res.SOG = (uint)buddie.speed;
            res.VesselName = buddie.name;
            res.MMSI = Buddie.MMSI(buddie.name);
            return res;
        }

        public byte[] ToAIS()
        {
            byte[] unpackedBytes = new byte[39];

            AISTransCoder.SetBitsAsUnsignedInt(unpackedBytes, 0, 6, 19); // type
            AISTransCoder.SetBitsAsUnsignedInt(unpackedBytes, 6, 2, 0);
            AISTransCoder.SetBitsAsUnsignedInt(unpackedBytes, 8, 30, (int)MMSI);
            AISTransCoder.SetBitsAsUnsignedInt(unpackedBytes, 46, 10, (int)(SOG / 1.852 * 10)); // speed            
            AISTransCoder.SetBitsAsUnsignedInt(unpackedBytes, 56, 1, Accuracy ? 1 : 0);
            AISTransCoder.SetBitsAsSignedInt(unpackedBytes, 57, 28, (int)(Longitude * 600000));
            AISTransCoder.SetBitsAsSignedInt(unpackedBytes, 85, 27, (int)(Latitude * 600000));
            AISTransCoder.SetBitsAsUnsignedInt(unpackedBytes, 112, 12, (int)(COG * 10.0));
            AISTransCoder.SetBitsAsUnsignedInt(unpackedBytes, 124, 9, HDG);
            AISTransCoder.SetBitsAsUnsignedInt(unpackedBytes, 133, 6, 60);
            AISTransCoder.SetAisString(unpackedBytes, 143, 120, VesselName);
            AISTransCoder.SetBitsAsUnsignedInt(unpackedBytes, 263, 8, ShipType);
            AISTransCoder.SetBitsAsUnsignedInt(unpackedBytes, 301, 4, 1);
            return unpackedBytes;
        }

        public override string ToString()
        {            
            return AISTransCoder.EnpackAISString(ToAIS());
        }
    }

    // 24
    public class StaticDataReport
    {
        private const int length = 168;

        public uint MMSI;
        public string VesselName;
        public int ShipType = 0;
        public uint IMOShipID;
        public string CallSign;

        public static StaticDataReport FromAIS(byte[] unpackedBytes)
        {
            StaticDataReport res = new StaticDataReport();
            int stype = AISTransCoder.GetBitsAsUnsignedInt(unpackedBytes, 0, 6);
            if (stype != 24) return null;
            
            res.MMSI = (uint)AISTransCoder.GetBitsAsUnsignedInt(unpackedBytes, 8, 30);
            res.VesselName = AISTransCoder.GetAisString(unpackedBytes, 40, 120);
            res.ShipType = AISTransCoder.GetBitsAsUnsignedInt(unpackedBytes, 40, 8);
            res.CallSign = AISTransCoder.GetAisString(unpackedBytes, 90, 42);
            return res;
        }

        public static StaticDataReport FromAIS(string ais)
        {
            byte[] unp = AISTransCoder.UnpackAISString(ais);
            return FromAIS(unp);
        }

        public static StaticDataReport FromBuddie(Buddie buddie)
        {
            StaticDataReport res = new StaticDataReport();
            res.VesselName = res.CallSign = buddie.name;
            res.MMSI = res.IMOShipID = Buddie.MMSI(buddie.name);
            return res;
        }

        public override string ToString()
        {
            return ToStringA();
        }

        public byte[] ToAISa()
        {
            byte[] unpackedBytes = new byte[21];
            AISTransCoder.SetBitsAsUnsignedInt(unpackedBytes, 0, 6, 24);
            AISTransCoder.SetBitsAsUnsignedInt(unpackedBytes, 6, 2, 0);
            AISTransCoder.SetBitsAsUnsignedInt(unpackedBytes, 8, 30, (int)MMSI);
            AISTransCoder.SetBitsAsUnsignedInt(unpackedBytes, 38, 2, 0); // partA
            AISTransCoder.SetAisString(unpackedBytes, 40, 120, VesselName);
            return unpackedBytes;
        }

        public string ToStringA()
        {
            return AISTransCoder.EnpackAISString(ToAISa());
        }

        public byte[] ToAISb()
        {
            byte[] unpackedBytes = new byte[21];
            AISTransCoder.SetBitsAsUnsignedInt(unpackedBytes, 0, 6, 24);
            AISTransCoder.SetBitsAsUnsignedInt(unpackedBytes, 6, 2, 0);
            AISTransCoder.SetBitsAsUnsignedInt(unpackedBytes, 8, 30, (int)MMSI);
            AISTransCoder.SetBitsAsUnsignedInt(unpackedBytes, 38, 2, 1); // partB            
            AISTransCoder.SetBitsAsUnsignedInt(unpackedBytes, 40, 8, (int)ShipType);
            AISTransCoder.SetAisString(unpackedBytes, 90, 42, CallSign);
            AISTransCoder.SetBitsAsUnsignedInt(unpackedBytes, 132, 9, 4); // A
            AISTransCoder.SetBitsAsUnsignedInt(unpackedBytes, 141, 9, 1); // B
            AISTransCoder.SetBitsAsUnsignedInt(unpackedBytes, 150, 6, 1); // C
            AISTransCoder.SetBitsAsUnsignedInt(unpackedBytes, 156, 6, 2); // D
            return unpackedBytes;
        }

        public string ToStringB()
        {
            return AISTransCoder.EnpackAISString(ToAISb());
        }

    }

    public class AISTransCoder
    {       
        public static string Checksum(string sentence)
        {
            int iFrom = 0;
            if (sentence.IndexOf('$') == 0) iFrom++;
            if (sentence.IndexOf('!') == 0) iFrom++;
            int iTo = sentence.Length;
            if (sentence.LastIndexOf('*') == (sentence.Length - 3))
                iTo = sentence.IndexOf('*');
            int checksum = Convert.ToByte(sentence[iFrom]);
            for (int i = iFrom + 1; i < iTo; i++)
                checksum ^= Convert.ToByte(sentence[i]);
            return checksum.ToString("X2");
        }

        public static byte[] UnpackAISString(string s)
        {
            return UnpackAISBytes(Encoding.UTF8.GetBytes(s));
        }

        private static byte[] UnpackAISBytes(byte[] data)
        {
            int outputLen = ((data.Length * 6) + 7) / 8;
            byte[] result = new byte[outputLen];

            int iSrcByte = 0;
            byte nextByte = ToSixBit(data[iSrcByte]);
            for (int iDstByte = 0; iDstByte < outputLen; ++iDstByte)
            {
                byte currByte = nextByte;
                if (iSrcByte < data.Length - 1)
                    nextByte = ToSixBit(data[++iSrcByte]);
                else
                    nextByte = 0;

                switch (iDstByte % 3)
                {
                    case 0:
                        result[iDstByte] = (byte)((currByte << 2) | (nextByte >> 4));
                        break;
                    case 1:
                        result[iDstByte] = (byte)((currByte << 4) | (nextByte >> 2));
                        break;
                    case 2:
                        result[iDstByte] = (byte)((currByte << 6) | (nextByte));
                        
                        if (iSrcByte < data.Length - 1)
                            nextByte = ToSixBit(data[++iSrcByte]);
                        else
                            nextByte = 0;
                        break;
                }
            }

            return result;
        }

        public static string EnpackAISString(byte[] ba)
        {
            return Encoding.UTF8.GetString(EnpackAISBytes(ba));
        }

        private static byte[] EnpackAISBytes(byte[] ba)
        {
            List<byte> res = new List<byte>();
            for (int i = 0; i < ba.Length; i++)
            {
                int val = 0;
                int val2 = 0;
                switch (i % 3)
                {
                    case 0:
                        val = (byte)((ba[i] >> 2) & 0x3F);
                        break;
                    case 1:
                        val = (byte)((ba[i - 1] & 0x03) << 4) | (byte)((ba[i] & 0xF0) >> 4);
                        break;
                    case 2:
                        val = (byte)((ba[i - 1] & 0x0F) << 2) | (byte)((ba[i] & 0xC0) >> 6);
                        val2 = (byte)((ba[i] & 0x3F)) + 48;
                        if (val2 > 87) val2 += 8;
                        break;
                };
                val += 48;
                if (val > 87) val += 8;
                res.Add((byte)val);
                if ((i % 3) == 2) res.Add((byte)val2);
            };
            return res.ToArray();
        }

        public static byte ToSixBit(byte b)
        {
            byte res = (byte)(b - 48);
            if (res > 39) res -= 8;
            return res;
        }

        public static string GetAisString(byte[] source, int start, int len)
        {
            string key = "@ABCDEFGHIJKLMNOPQRSTUVWXYZ[\\]^_ !\"#$%&'()*+,-./0123456789:;<=>?";
            int l = key.Length;
            string val = "";
            for (int i = 0; i < len; i += 6)
            {
                byte c = (byte)(GetBitsAsSignedInt(source, start + i, 6) & 0x3F);
                val += key[c];
            };
            return val.Trim();
        }

        public static void SetAisString(byte[] source, int start, int len, string val)
        {
            if (val == null) val = "";
            string key = "@ABCDEFGHIJKLMNOPQRSTUVWXYZ[\\]^_ !\"#$%&'()*+,-./0123456789:;<=>?;";
            int strlen = len / 6;
            if (val.Length > strlen) val = val.Substring(0, strlen);
            while (val.Length < strlen) val += " ";
            int s = 0;
            for (int i = 0; i < len; i += 6, s++)
            {
                byte c = (byte)key.IndexOf(val[s]);
                SetBitsAsSignedInt(source, start + i, 6, c);
            };
        }

        public static int GetBitsAsSignedInt(byte[] source, int start, int len)
        {
            int value = GetBitsAsUnsignedInt(source, start, len);
            if ((value & (1 << (len - 1))) != 0)
            {
                // perform 32 bit sign extension
                for (int i = len; i < 32; ++i)
                {
                    value |= (1 << i);
                }
            };
            return value;
        }

        public static void SetBitsAsSignedInt(byte[] source, int start, int len, int val)
        {
            int value = val;
            if (value < 0)
            {
                value = ~value;
                for (int i = len; i < 32; ++i)
                {
                    value |= (1 << i);
                };
            }
            SetBitsAsUnsignedInt(source, start, len, val);
        }

        public static int GetBitsAsUnsignedInt(byte[] source, int start, int len)
        {
            int result = 0;

            for (int i = start; i < (start + len); ++i)
            {
                int iByte = i / 8;
                int iBit = 7 - (i % 8);
                result = result << 1 | (((source[iByte] & (1 << iBit)) != 0) ? 1 : 0);
            };

            return result;
        }

        public static void SetBitsAsUnsignedInt(byte[] source, int start, int len, int val)
        {
            int bit = len - 1;
            for (int i = start; i < (start + len); ++i, --bit)
            {
                int iByte = i / 8;
                int iBit = 7 - (i % 8);
                byte mask = (byte)(0xFF - (byte)(1 << iBit));
                byte b = (byte)(((val >> bit) & 0x01) << iBit);
                source[iByte] = (byte)((source[iByte] & mask) | b);
            }
        }
    }
}
