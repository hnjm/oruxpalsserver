This is the OruxPalsServer specially written for OruxMaps Android Application.
The server allow to watch any users geo positions as vessels on the map by AIS.
Users can upload their positions to the server by GPSGate or MapMyTracks protocols.

Server can send nearest (in raidus range) static objects to each APRS client 
(defined on his APRS position). Static objects could be used from XML, KML or SQLite.


WEB ONLINE:


	To view server info in browser:
		http://127.0.0.1:12015/oruxpals/info
	
	To view online map:
	http://127.0.0.1:12015/oruxpals/view
	
	To online generate user hashsum (xml tag <adminName/>):
		http://127.0.0.1:12015/oruxpals/$admin


		
CONFIGURE ORUXMAPS:		


	To connect for upload positions (without speed & course) use MapMyTracks:
		URL: http://www.mypals.com:12015/oruxpals/m/
		USER: user - must be from 3 to 9 symbols of any [A-Z0-9]
		PASSWORD:  calculating by hashsum for user by console or browser
		
	To connect for upload positions (with speed & course) use GPSGate:
		URL: http://www.mypals.com:12015/oruxpals/@user/
			 ! user - must be from 3 to 9 symbols of any [A-Z0-9]
		IMEI:  user's password, calculating by hashsum for user by console or browser
				
	To connect for view positions use AIS IP Server:
		GPS-AIS-NMEA source: IP
		AIS IP URL:  127.0.0.1:12015

		
CONFIGURE APRS Client (APRSDroid)   

	To connect for view & upload data use APRS Client:
		URL: 127.0.0.1:12015    
		
	filter supported (in APRS auth string):	
		Static objects filter (filter is not apply for users position):
			me/10 - static objects from me in 10 km range
			me/0 - no static objects
		; user can use filter me/range, if he doesn't want to use specified range by xml config file
		; if user doesn't use me/range filter, static objects will display within range from xml config file
		User (Group) filter (filter is not apply for static objects):
			+sw/A/B/C - allow user pos with name starts with A or B or C
			+ew/A/B/C - allow user pos with name ends with A or B or C
			+fn/ULKA/RUZA -  allow user pos with name ULKA or RUZA
			-sw/A/B/C - deny user pos with name starts with A or B or C		
			-ew/A/B/C - deny user pos with name ends with A or B or C		
			-fn/ULKA/RUZA -  deny user pos with name ULKA or RUZA
		; allow filters are first processed, then deny.
		; by default is allow all. But if you use any + filters, by default is deny.
		
	SUPPORTED COMMANDS FROM APRSDroid to Server
		msg to ORXPLS-GW: forward   - get forward state for user (<u forward="???"/> tag)
		msg to ORXPLS-GW: forward A  - set forward state to A (send users data to global APRS)
		msg to ORXPLS-GW: forward 0  - set forward state to 0 (zero, no forward)
		msg to ORXPLS-GW: forward ALH  - set forward state to ALH
		msg to ORXPLS-GW: forward ALHDO  - set forward state to ALHDO
		msg to ORXPLS-GW: kill UserName  - delete from server info about user
		msg to ORXPLS-GW: admin NewUserName   - get APRS password & OruxPalsServer password for NewUserName (admin from xml tag <adminName/>)
		msg to ORXPLS-ST: global status here  - send status message to global APRS `:>`
		msg to ORXPLS-CM: ?  - get comment for position report forwarding data to APRS-IS when client connected not via APRS 
		msg to ORXPLS-CM: comment for position report here  - set comment for position report forwarding data to APRS-IS when client connected not via APRS 
		// when client connected via APRS client all data from him will directly send to global APRS-IS (if forwarding setting allows to user forward him data)

		
CONFIGURE OTHER CLIENTS:		
		
		
	To connect for upload positions user GPSGate Tracker:
		URL: 127.0.0.1:12015
		PHONE: user's phone number (defined by xml tag <u phone="+???"/>)

 
AUTHORIZATION: 
 
	user - must be from 3 to 9 symbols of any [A-Z0-9]
	password - calculating by hashsum for user by console or browser
	! for APRS Clients password if default for they Callsign


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

	Import kml file to SQLite `RouteObjects.db`
		OruxPalsServer.exe /kml2sql
		OruxPalsServer.exe /kml2sql <file>

SQLite DB:
  To manage Data you can user SQLiteStudio or SQLiteBrowser.
  Static objects are in Table `OBJECTS`
  To clean all data from SQLite just copy `Empty.db` to `StaticObjects.db`
	
	