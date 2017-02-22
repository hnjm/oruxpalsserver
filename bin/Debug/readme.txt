This is the OruxPalsServer specially written for OruxMaps Android Application 
(6.5.5+ for AIS and 7.0.0rc9+ for APRS). The server can receive position from
OruxMaps application by GPSGate (HTTP GET) protocol, MapMyTracks protocol or
APRS protocol. Server stores received positions and send it to all clients connected 
by AIS or APRS. So you can watch on the map user position in real time as vessels 
(by AIS) or as aprs icons (by APRS) with names.

Server also can filter sending data to each client with specified user filters:
- range filter for static objects (me/10/50);
- name filters to pass or block incoming positions from users or static objects 
  (+sw/ +ew/ +fn/ -sw/ -ew/ -fn/).


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
				
	To connect for view positions using AIS:
		AIS URL:  127.0.0.1:12015
		
	To connect for view and upload position using APRS:
		APRS URL: 127.0.0.1:12015

		
CONFIGURE APRS Client (APRSDroid / OruxMaps)   

	To connect for view & upload data use APRS Client:
		URL: 127.0.0.1:12015    
		
	filter supported (in APRS auth string):	
		Static objects range/limit filter (filter is not apply for users positions):
			me/10/30 - maximum 30 static objects from me in 10 km range
			me/10 - static objects from me in 10 km range			
			me/0 - no static objects
		; user can use filter me/range/limit, if he doesn't want to use specified range/limit by xml config file
		; if user doesn't use me/range/limit filter, static objects will display within range/limit from xml config file
		Name (Group) filter:
			+sw/A/B/C - pass users/objects pos with name starts with A or B or C
			+ew/A/B/C - pass users/objects pos with name ends with A or B or C
			+fn/ULKA/RUZA - pass users/objects pos with name ULKA or RUZA
			-sw/A/B/C - block users/objects pos with name starts with A or B or C		
			-ew/A/B/C - block users/objects pos with name ends with A or B or C		
			-fn/ULKA/RUZA -  block users/objects pos with name ULKA or RUZA
		; pass filters are first processed, then block.
		; by default pass all; but if you use any + filters, default is block.
		You can use Name filter to create separeted groups that receive positions only from itself group users, ex:
			for users: USERAG1, USER2G1,USERBG1,USER2G1 set filter: +ew/G1
			for users: G2ANNA,G2ALEX,G2VICTOR set filter: +sw/G2
	filter examples:
		me/10/30 +sw/M4/MOBL +fn/M4OLGA
		me/5/15 -sw/M4ASZ/MSKAZS -fn/BOAT
		+ew/G1 +sw/G2
		-ew/G1 -sw/G3
		
	SUPPORTED COMMANDS FROM APRSDroid to Server (APRS packet type Message):
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

	Import kml file to SQLite `StaticObjects.db`
		OruxPalsServer.exe /kml2sql
		OruxPalsServer.exe /kml2sql <file>

SQLite DB:
  To manage Data you can user SQLiteStudio or SQLiteBrowser.
  Static objects are in Table `OBJECTS`
  To clean all data from SQLite just copy `Empty.db` to `StaticObjects.db`
	
	