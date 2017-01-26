This is the OruxPalsServer specially written for OruxMaps Android Application.
The server allow to watch any users geo positions as vessels on the map by AIS.
Users can upload their positions to the server by GPSGate or MapMyTracks protocols.

To view server info in browser:
	http://127.0.0.1:12015/oruxpals/info

To connect for view positions use AIS IP Server:
    GPS-AIS-NMEA source: IP
	AIS IP URL:  www.mypals.com:12015
 
To connect for upload positions (with speed & course) use GPSGate:
	URL: http://www.mypals.com:12015/oruxpals/@user/
	IMEI:  user's password

To connect for upload positions (without speed & course) use MapMyTracks:
	URL: http://www.mypals.com:12015/oruxpals/m/
	
To online generate user hashsum (xml tag <adminName/>):
	http://127.0.0.1:12015/oruxpals/$admin
 
 
user - must be from 3 to 9 symbols of any [A-Z0-9]
password - calculating by hashsum for user by console or browser

How to launch:
    OruxPalsServer.xml - configuration
	
	Run at console:
		OruxPalsServer.exe
		
	Install as service:
		OruxPalsServer.exe /install
		
	Uninstall service:
		OruxPalsServer.exe /uninstall
		
	Start service:
		OruxPalsServer.exe /start
	
	Stop service:
		OruxPalsServer.exe /stop
		
	Restart service:
		OruxPalsServer.exe /restart
		
	Service status:
		OruxPalsServer.exe /status
	
	Generate passcode:
		OruxPalsServer.exe userName
	
	