## Build release ZIP (Windows)

Run this from the repo root after your dependencies are restored; it rebuilds the Angular client, publishes `RdtClient.Web` for Windows x64, and zips the `Publish` folder just like the release archives do:

```
(cd client && npm install && npm run build) && dotnet publish server/RdtClient.Web/RdtClient.Web.csproj -c Release -r win-x64 --self-contained true -o Publish && (cd Publish && zip -r ../RdtClient.Web.zip .)
```

`RdtClient.Web.zip` lands in the repo root and contains `RdtClient.Web.exe` plus all supporting files from the publish output.

## Fresh checkout quick start
1. `cd client && npm install` (the Angular client is defined by `client/package.json`, so this restores the dependencies needed for `ng serve` or `ng build`).
2. Confirm or customize `.localdata` in the repo root before running the server: it already places logs at `.localdata/rdtclient.log`, stores the SQLite file at `.localdata/rdtclient.db`, and listens on port 6500, but you can update the `Logging.File.Path` and `Database.Path` entries to host paths that exist on your machine.
3. `cd server` so that `dotnet` can resolve the solution and the `server/RdtClient.Web/RdtClient.Web.csproj` project file.
4. `dotnet run --project RdtClient.Web` to start the backend API on the configured port (6500 by default), which is what the Angular client expects.

## ShikiDashboard API

The backend exposes two authenticated endpoints under `/Api/ShikiDashboard` so that external dashboards or automation tools can watch the active queue and submit magnets. Both endpoints use Basic authentication (the same username/password pair you set inside the main UI). The server will issue a `WWW-Authenticate: Basic realm="ShikiDashboard"` challenge if no credentials arrive. Include a header such as `Authorization: Basic <base64(username:password)>`.

### GET `/Api/ShikiDashboard/Queue/Public`

- **Purpose:** Returns the current queue of torrents that are not yet completed and have no errors. Internally the controller limits the list to entries where `Completed == null` and `Error` is empty, then orders them alphabetically by name.
- **Success response:** `200 OK` with a JSON array of `PublicTorrentQueueItemDto`. Example:
  ```json
  [
    {
      "name": "Example.Torrent.S01E01",
      "totalSizeBytes": 2147483648,
      "downloadedPercent": 42.5,
      "currentDownloadSpeedBytesPerSecond": 1048576,
      "rawStatus": "Downloading 1/2 files (85.00% - 1 MB/s)",
      "status": "Downloading",
      "totalFilesToDownload": 2,
      "completedFilesCount": 0,
      "activeFilesCount": 1,
      "queuedFilesCount": 1,
      "torrentIsCached": true
    }
  ]
  ```
- **Example curl:**  
  `curl -u USERNAME:PASSWORD -X GET http://HOSTNAME/Api/ShikiDashboard/Queue/Public`
- **Field meanings:**
  - `name`: Display name (`RdName` or fallback to hash).
  - `totalSizeBytes`: Sum of all tracked downloads in bytes (falls back to the provider size when no downloads exist).
  - `downloadedPercent`: Progress between `0` and `100`. During local file downloads, this is calculated across the full file set, so `Downloading 1/2 files (50.00% - ...)` yields `25` instead of `50`.
  - `currentDownloadSpeedBytesPerSecond`: Current download speed in bytes per second. During the debrid torrent phase it uses the same `rdSpeed` value shown in the main UI status text; during local host downloads it sums the active local download speeds.
  - `rawStatus`: The literal text shown in the UI status column (it mirrors the Angular `TorrentStatusPipe`). Expect strings like `Downloading 1/2 files (25.00% - 500 B/s)`, `Torrent downloading (15% - 630 kB/s)`, `Queued for downloading`, `Queued for unpacking`, `Torrent finished, waiting for download links`, and other progress or queue messages the UI renders. During host downloads and extraction, the `X/Y` value is the count of completed plus active files out of the total, not a sequential file index.
  - `status`: A normalized label derived from `rawStatus`. For literal texts (like `Queued for downloading` or `Torrent finished, waiting for download links`) it matches the input string, while the pattern-based strings collapse to one of `Downloading`, `Extracting`, `Torrent Downloading`, or `Error`.
  - `status` can therefore be any of the explicit values `Finished`, `Queued for unpacking`, `Queued for downloading`, `Files unpacked`, `Files downloaded to host`, `Not Yet Added to Provider`, `Torrent stalled`, `Torrent processing`, `Torrent waiting for file selection`, `Torrent finished, waiting for download links`, `Torrent uploading`, `Unknown status`, or the normalized tokens derived from `Downloading X/Y files (P% - S/s)`, `Extracting X/Y files (P%)`, `Torrent downloading (P% - S/s)`, and `Torrent error: <rdStatusRaw or "Unknown">`.
  - `totalFilesToDownload`: The total number of tracked files in the local host-download phase. Otherwise `null`.
  - `completedFilesCount`: Number of files whose host download has finished. Otherwise `null`.
  - `activeFilesCount`: Number of files currently being host-downloaded. Otherwise `null`.
  - `queuedFilesCount`: Number of files queued for host download but not started yet. Otherwise `null`.
  - `torrentIsCached`: `true` once the torrent has finished on the provider and is cached locally on the debrid side, including host-download phases like `Downloading ... files`; otherwise `false`.
- **Errors:** `401 Unauthorized` when credentials are missing/bad.

### POST `/Api/ShikiDashboard/IngestMagnetLink`

- **Purpose:** Adds a magnet link to the queue using the default torrent settings configured in the GUI.
- **Request body:** JSON object with a `MagnetLink` string, e.g. `{"magnetLink": "magnet:?xt=urn:btih:..."}`. The controller trims whitespace before submission.
- **Success response:** `200 OK` with a boolean body:
  - `true` when the magnet was successfully inserted into the queue.
  - `false` when ingestion failed (invalid payload, torrent add error, etc.). The server also logs a warning on failure, but still replies with `false`.
- **Example curl:**  
  `curl -H "Authorization: Basic YWRtaW46c2VjcmV0" -H "Content-Type: application/json" -d '{"magnetLink":"magnet:?xt=urn:btih:..."}' https://localhost:port/Api/ShikiDashboard/IngestMagnetLink`
- **Errors:** `401 Unauthorized` when authentication fails; the endpoint will add the Basic challenge header before returning `401`.
- **Notes:** Existing default GUI settings (category, download client, retry counts, regex filters, etc.) are applied automatically so that the new torrent behaves exactly like a manual upload.

# Real-Debrid Torrent & Usenet Client
This is a web interface to manage your torrents on Real-Debrid, AllDebrid, Premiumize TorBox or DebridLink. It supports the following features:

- Add new torrents through magnets or files
- Add usenet downloads through NZB files (TorBox only)
- Download all files from Real-Debrid, AllDebrid, Premiumize or TorBox to your local machine automatically
- Unpack all files when finished downloading
- Implements a fake qBittorrent API so you can hook up other applications like Sonarr, Radarr or Couchpotato.
- Built with Angular 21 and .NET 10

**You will need a Premium service at Real-Debrid, AllDebrid, Premiumize or Torbox!**

[Click here to sign up for Real-Debrid.](https://real-debrid.com/?id=1348683)


## Docker Setup

Please see our separate Docker setup Read Me.

[Readme for Docker](README-DOCKER.md)

## Run as a Service

Instead of running in Docker you can install it as a service in Windows or Linux.

## Windows Service

1. Make sure you have the **ASP.NET Core Runtime 10.0.0** and the **SDK** installed: [https://dotnet.microsoft.com/download/dotnet/10.0](https://dotnet.microsoft.com/download/dotnet/10.0)
2. Get the latest zip file from the Releases page and extract it to your host.
3. Open the `appsettings.json` file and replace the `LogLevel` `Path` to a path on your host.
4. In `appsettings.json` replace the `Database` `Path` to a path on your host.
5. When using Windows paths, make sure to escape the slashes. For example: `D:\\RdtClient\\db\\rdtclient.db`
6. Do one of these:
	* Run `RdtClient.Web.exe` to start the client.
 	* Run `service-install.bat` to install the client as a service. This will install `RdtClient.Web.exe` as a service which make the client start in the backgorund when the computer starts. (You probably want to do this if you are going to use this with Sonarr etc...)

## Linux Service

Instead of running in Docker you can install it as a service in Linux.

1. Install .NET: [https://docs.microsoft.com/en-us/dotnet/core/install/linux](https://docs.microsoft.com/en-us/dotnet/core/install/linux)

    Ubuntu 20.04 example:  
    ```wget https://packages.microsoft.com/config/ubuntu/20.04/packages-microsoft-prod.deb -O packages-microsoft-prod.deb```  
    
    ```sudo dpkg -i packages-microsoft-prod.deb```  

    ```rm packages-microsoft-prod.deb```  

    ```sudo apt-get update && sudo apt-get install -y dotnet-sdk-10.0```  

2. Get latest archive from [releases](https://github.com/rogerfar/rdt-client/releases):  
```wget <zip_url>```
3. Extract to path of your choice (~/rtdc in this example):  
```unzip RealDebridClient.zip -d ~/rdtc && cd ~/rdtc```
4. In appsettings.json replace the Database Path to a path on your host. Any directories in path must already exist. Or just remove "/data/db/" for ease.
5. Test rdt client runs ok:  
```dotnet RdtClient.Web.dll```   
navigate to http://<ipaddress>:6500, if all is good then we'll create a service
6. Create a service (systemd in this example):  
```sudo nano /etc/systemd/system/rdtc.service```  

    paste in this service file content and edit path of your directory:

    ```
    [Unit]
    Description=RdtClient Service

    [Service]

    WorkingDirectory=/home/<username>/rdtc
    ExecStart=/usr/bin/dotnet RdtClient.Web.dll
    SyslogIdentifier=RdtClient
    User=<username>

    [Install]
    WantedBy=multi-user.target
    ```

7. enable and start the service:   
```sudo systemctl daemon-reload```  
```sudo systemctl enable rdtc```  
```sudo systemctl start rdtc```  

## Proxmox LXC

If you use Proxmox for your homelab, you can run rdt-client in a linux container (LXC), check it here:
[https://tteck.github.io/Proxmox/](https://tteck.github.io/Proxmox/) 

## Setup

### First Login

1. Browse to [http://127.0.0.1:6500](http://127.0.0.1:6500) (or the path of your server).
1. The very first credentials you enter in will be remembered for future logins.
1. Click on `Settings` on the top and enter your Real-Debrid API key (found here: [https://real-debrid.com/apitoken](https://real-debrid.com/apitoken).
1. If you are using docker then the `Download path` setting needs to be the same as in your docker file mapping. By default this is `/data/downloads`. If you are using Windows, this is a path on your host.
1. Same goes for `Mapped path`, but this is the destination path from your docker mapping. This is a path on your host. For Windows, this will most likely be the same as the `Download path`.
1. Save your settings.

### Download Clients

Currently there 4 available download clients:

#### Bezzad Downloader

This [downloader](https://github.com/bezzad/Downloader) can be used to download files in parallel and with multiple chunks.

It has the following options:

- Download speed (in MB/s): This number indicates the speed in MB/s per download over all parallel downloads and chunks.
- Parallel connections per download: This number indicates how many parallel it will use per download. This can increase speed, recommended is no more than 8.
- Parallel chunks per download: This number indicates in how many chunks each download is split, recommended is no more than 8.
- Connection Timeout: This number indicates the timeout in milliseconds before a download chunk times out. It will retry each chunk 5 times before completely failing.

#### Aria2c downloader

This will use an external Aria2c downloader client. You will need to install this client yourself on your host, it is not included in the docker image.

It has the following options:

- Url: The full URL to your Aria2c service. This must end in /jsonrpc. A standard path is `http://192.168.10.2:6800/jsonrpc`.
- Secret: Optional secret to connecto to your Aria2c service.

If Aria2c is selected, none of the above options for `Internal Downloader` are used, you have to configure Aria2c manually.

#### Symlink downloader

Symlink downloader requires a rclone mount to be mounted into your filesystem. Be sure to keep the exact path to mounted files in other apps exactly
the same as used by rdt-client. Otherwise the symlinks wont resolve the file its trying to point to.

If the mount path folder cant be found the client wont start downloading anything.

Required configuration:
- Post Download Action = DO NOT SELECT REMOVE FROM PROVIDER
- Rclone mount path = /PATH_TO_YOUR_RCLONE_MOUNT/torrents/

Suggested configuration:
- Automatic retry downloads > 3

#### Synology Download Station

The Synology Download Station downloader uses an external Download Station server. You will need to set this up yourself.

It has the following options:

- Url: The URL to the Synology DownloadStation. A common URL is `http://127.0.0.1:5000`
- Username: The username to use when connecting to the Synology DownloadStation.
- Password: The password to use when connecting to the Synology DownloadStation.
- Download Path: The root path to download the file on the Synology DownloadStation host. If left empty, the default path configured on your Download Station server will be used.

### Troubleshooting

- If you forgot your logins simply delete the `rdtclient.db` and restart the service.
- A log file is written to your persistent path as `rdtclient.log`. When you run into issues please change the loglevel in your docker script to `Debug`.

### Connecting Sonarr/Radarr

RdtClient emulates the qBittorrent web protocol and allow applications to use those APIs. This way you can use Sonarr and Radarr to download directly from RealDebrid.

1. Login to Sonarr or Radarr and click `Settings`.
1. Go to the `Download Client` tab and click the plus to add.
1. Click `qBittorrent` in the list.
1. Enter the IP or hostname of the RealDebridClient in the `Host` field.
1. Enter the 6500 in the `Port` field.
1. Enter your Username/Password you setup above in the Username/Password field.
1. Set the category to `sonarr` for Sonarr or `radarr` for Radarr.
1. Leave the other settings as is.
1. Hit `Test` and then `Save` if all is well.
1. Sonarr will now think you have a regular Torrent client hooked up.

When downloading files it will append the `category` setting in the Sonarr/Radarr Download Client setting. For example if your Remote Path setting is set to `C:\Downloads` and your Sonarr Download Client setting `category` is set to `sonarr` files will be downloaded to `C:\Downloads\sonarr`.

Notice: the progress and ETA reported in Sonarr's Activity tab will not be accurate, but it will report the torrent as completed so it can be processed after it is done downloading.

### Running within a folder

By default the application runs in the root of your hosted address (i.e. https://rdt.myserver.com/), but if you want to run it as a relative folder (i.e. https://myserver.com/rdt) you will have to change the `BasePath` setting in the `appsettings.json` file. You can set the `BASE_PATH` environment variable for docker enviroments.

## Build instructions

### Prerequisites

- NodeJS
- NPM
- Angular CLI
- .NET 10
- Visual Studio 2025
- (optional) Resharper

1. Open the client folder project in VS Code and run `npm install`.
1. To debug run `ng serve`, to build run `ng build -c production`.
1. Open the Visual Studio 2025 project `RdtClient.sln` and `Publish` the `RdtClient.Web` to the given `PublishFolder` target.
1. When debugging, make sure to run `RdtClient.Web.dll` and not `IISExpress`.
1. The result is found in `Publish`.

## Build docker container

1. In the root of the project run `docker build --tag rdtclient .`
1. To create the docker container run `docker run --publish 6500:6500 --detach --name rdtclientdev rdtclient:latest`
1. To stop: `docker stop rdtclient`
1. To remove: `docker rm rdtclient`
1. Or use `docker-build.bat`

## Misc Install Notes

### Rootless Podman, Linux Host, and CIFS Connections

RDT Client read and write permission tests fail if the CIFS connection is not setup properly, despite permissions working inspection.  In the Web GUI, it will report access denied, and in the log file you will see exceptions like this ([dotnet issue](https://github.com/dotnet/runtime/issues/42790)): 
```
System.IO.IOException: Permission denied
```
The **nobrl** has to be specified in your CIFS connection - [man page](https://linux.die.net/man/8/mount.cifs). 
Example: ```Options=_netdev,credentials=/etc/samba/credentials/600file,rw,uid=SUBUID,gid=SBUGID,nobrl,file_mode=0770,dir_mode=0770,noperm```
