This is the OruxPalsServer specially written for OruxMaps Android Application. The server allow to watch any users geo positions as vessels on the map by AIS or APRS. Users can upload their positions to the server by GPSGate (HTTP GET & TCP FRS), MapMyTracks or APRS protocols.

Server can send nearest (in raidus range) static objects to each APRS client (defined on his APRS position). Static objects could be used from XML, KML or SQLite.
APRS users can use filter for static objects (me/10) and filter to allow or deny receiving position from filtered users (+sw/ +ew/ +fn/ -sw/ -ew/ -fn/)