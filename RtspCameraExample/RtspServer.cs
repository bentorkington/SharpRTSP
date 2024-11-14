﻿using Microsoft.Extensions.Logging;
using Rtsp;
using Rtsp.Messages;
using Rtsp.Rtcp;
using Rtsp.Rtp;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Contracts;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RtspCameraExample
{
    // RTSP Server Example (c) Roger Hardiman, 2016, 2018, 2020
    // Released uder the MIT Open Source Licence
    //
    // Re-uses some code from the Multiplexer example of SharpRTSP
    //
    // Creates a server to listen for RTSP Commands (eg OPTIONS, DESCRIBE, SETUP, PLAY)
    // Accepts SPS/PPS/NAL H264 video data and sends out to RTSP clients

    public class RtspServer : IDisposable
    {
        const uint global_ssrc = 0x4321FADE; // 8 hex digits
        const int rtspTimeOut = 60; // 60 seconds

        private readonly TcpListener _RTSPServerListener;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger _logger;
        private CancellationTokenSource? _Stopping;
        private Thread? _ListenTread;

        const int video_payload_type = 96; // = user defined payload, requuired for H264
        byte[]? raw_sps;
        byte[]? raw_pps;

        const int audio_payload_type = 0; // = Hard Coded to PCMU audio
        private ushort audioSequenceNumber = (ushort)Random.Shared.Next();
        private ushort videoSequenceNumber = (ushort)Random.Shared.Next();

        private readonly List<RTSPConnection> rtspConnectionList = []; // list of RTSP Listeners

        int session_handle = 1;
        private readonly NetworkCredential credential;
        private readonly Authentication? auth;

        private bool _useRTSPS = false;
        private string _pfxFile = "";

        /// <summary>
        /// Initializes a new instance of the <see cref="RTSPServer"/> class.
        /// </summary>
        /// <param name="portNumber">A numero port.</param>
        /// <param name="username">username.</param>
        /// <param name="password">password.</param>
        public RtspServer(int portNumber, string username, string password, ILoggerFactory loggerFactory)
        {
            if (portNumber < IPEndPoint.MinPort || portNumber > IPEndPoint.MaxPort)
            {
                throw new ArgumentOutOfRangeException(nameof(portNumber), portNumber, "Port number must be between System.Net.IPEndPoint.MinPort and System.Net.IPEndPoint.MaxPort");
            }

            Contract.EndContractBlock();

            if (!string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password))
            {
                const string realm = "SharpRTSPServer";
                credential = new(username, password);
                auth = new AuthenticationDigest(credential, realm, new Random().Next(100000000, 999999999).ToString(), string.Empty);
            }
            else
            {
                credential = new();
                auth = null;
            }

            RtspUtils.RegisterUri();
            _RTSPServerListener = new TcpListener(IPAddress.Any, portNumber);
            _loggerFactory = loggerFactory;
            _logger = loggerFactory.CreateLogger<RtspServer>();
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="RTSPServer"/> class in RTSPS (TLS) Mode.
        /// </summary>
        /// <param name="portNumber">A numero port.</param>
        /// <param name="username">username.</param>
        /// <param name="password">password.</param>
        /// <param name="pfxFile">pfxFile used for RTSPS TLS Server Certificate.</param>
        public RtspServer(int portNumber, string username, string password, string pfxFile, ILoggerFactory loggerFactory)
            : this(portNumber, username, password, loggerFactory)
        {
            if (string.IsNullOrEmpty(pfxFile))
            {
                throw new ArgumentOutOfRangeException("PFX File must not be null or empty for RTSPS mode");
            }
            _useRTSPS = true;
            _pfxFile = pfxFile;
        }

        /// <summary>
        /// Starts the listen.
        /// </summary>
        public void StartListen()
        {
            _RTSPServerListener.Start();

            _Stopping = new CancellationTokenSource();
            _ListenTread = new Thread(new ThreadStart(AcceptConnection));
            _ListenTread.Start();
        }

        /// <summary>
        /// Accepts the connection.
        /// </summary>
        private void AcceptConnection()
        {
            try
            {
                while (_Stopping?.IsCancellationRequested == false)
                {
                    // Wait for an incoming TCP Connection
                    TcpClient oneClient = _RTSPServerListener.AcceptTcpClient();
                    _logger.LogDebug("Connection from {remoteEndPoint}", oneClient.Client.RemoteEndPoint);

                    // Hand the incoming TCP connection over to the RTSP classes
                    IRtspTransport rtsp_socket;
                    if (!_useRTSPS)
                    {
                        rtsp_socket = new RtspTcpTransport(oneClient);
                    }
                    else
                    {
                        var certificate = new X509Certificate2(_pfxFile);
                        rtsp_socket = new RtspTcpTlsTransport(oneClient, certificate); // NOTE - we can add a callback where we can validate the TLS Certificates here
                    }

                    try
                    {
                        RtspListener newListener = new(rtsp_socket, _loggerFactory.CreateLogger<RtspListener>());
                        newListener.MessageReceived += RTSPMessageReceived;

                        // Add the RtspListener to the RTSPConnections List
                        lock (rtspConnectionList)
                        {
                            RTSPConnection new_connection = new()
                            {
                                Listener = newListener,
                            };
                            rtspConnectionList.Add(new_connection);
                        }

                        newListener.Start();
                    }
                    catch (AuthenticationException)
                    {
                        _logger.LogWarning("Invalid client (maybe RTSP on RTSPS socket)");
                    }
                }
            }
            catch (SocketException)
            {
                // _logger.Warn("Got an error listening, I have to handle the stopping which also throw an error", error);
            }
            catch (Exception)
            {
                // _logger.Error("Got an error listening...", error);
                throw;
            }
        }

        public void StopListen()
        {
            _RTSPServerListener.Stop();
            _Stopping?.Cancel();
            _ListenTread?.Join();
        }

        #region IDisposable Membres

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                StopListen();
                _Stopping?.Dispose();
            }
        }

        #endregion

        // Process each RTSP message that is received
        private void RTSPMessageReceived(object? sender, RtspChunkEventArgs e)
        {
            // Cast the 'sender' and 'e' into the RTSP Listener (the Socket) and the RTSP Message
            RtspListener listener = sender as RtspListener ?? throw new ArgumentException("Invalid sender", nameof(sender));

            if (e.Message is not RtspRequest message)
            {
                _logger.LogWarning("RTSP message is not a request. Invalid dialog.");
                return;
            }

            _logger.LogDebug("RTSP message received {message}", message);

            // Check if the RTSP Message has valid authentication (validating against username,password,realm and nonce)
            // skip authentication for OPTIONS for VLC
            if (auth != null && message is not RtspRequestOptions)
            {
                if (message.Headers.ContainsKey("Authorization"))
                {
                    // The Header contained Authorization
                    // Check the message has the correct Authorization
                    // If it does not have the correct Authorization then close the RTSP connection
                    if (!auth.IsValid(message))
                    {
                        // Send a 401 Authentication Failed reply, then close the RTSP Socket
                        RtspResponse authorization_response = message.CreateResponse();
                        authorization_response.AddHeader("WWW-Authenticate: " + auth.GetServerResponse()); // 'Basic' or 'Digest'
                        authorization_response.ReturnCode = 401;
                        listener.SendMessage(authorization_response);

                        lock (rtspConnectionList)
                        {
                            rtspConnectionList.RemoveAll(c => c.Listener == listener);
                        }
                        listener.Dispose();
                        return;
                    }
                }
                else
                {
                    // Send a 401 Authentication Failed with extra info in WWW-Authenticate
                    // to tell the Client if we are using Basic or Digest Authentication
                    RtspResponse authorization_response = message.CreateResponse();
                    authorization_response.AddHeader("WWW-Authenticate: " + auth.GetServerResponse());
                    authorization_response.ReturnCode = 401;
                    listener.SendMessage(authorization_response);
                    return;
                }
            }

            // Update the RTSP Keepalive Timeout
            lock (rtspConnectionList)
            {
                foreach (var oneConnection in rtspConnectionList.Where(connection => connection.Listener == listener))
                {
                    // found the connection
                    oneConnection.UpdateKeepAlive();
                    break;
                }
            }

            // Handle message without session
            switch (message)
            {
                case RtspRequestOptions:
                    listener.SendMessage(message.CreateResponse());
                    return;
                case RtspRequestDescribe describeMessage:
                    HandleDescribe(listener, message);
                    return;
                case RtspRequestSetup setupMessage:
                    HandleSetup(listener, setupMessage);
                    return;
            }

            // handle message needing session from here
            var connection = ConnectionBySessionId(message.Session);
            if (connection is null)
            {
                // Session ID was not found in the list of Sessions. Send a 454 error
                RtspResponse notFound = message.CreateResponse();
                notFound.ReturnCode = 454; // Session Not Found
                listener.SendMessage(notFound);
                return;
            }

            switch (message)
            {
                case RtspRequestPlay playMessage:
                    // Search for the Session in the Sessions List. Change the state to "PLAY"
                    const string range = "npt=0-";   // Playing the 'video' from 0 seconds until the end
                    string rtp_info = "url=" + message.RtspUri + ";seq=" + videoSequenceNumber; // TODO Add rtptime  +";rtptime="+session.rtp_initial_timestamp;
                                                                                                // Add audio too
                    rtp_info += ",url=" + message.RtspUri + ";seq=" + audioSequenceNumber; // TODO Add rtptime  +";rtptime="+session.rtp_initial_timestamp;

                    //    'RTP-Info: url=rtsp://192.168.1.195:8557/h264/track1;seq=33026;rtptime=3014957579,url=rtsp://192.168.1.195:8557/h264/track2;seq=42116;rtptime=3335975101'

                    // Send the reply
                    RtspResponse play_response = message.CreateResponse();
                    play_response.AddHeader("Range: " + range);
                    play_response.AddHeader("RTP-Info: " + rtp_info);
                    listener.SendMessage(play_response);

                    connection.video.must_send_rtcp_packet = true;
                    connection.audio.must_send_rtcp_packet = true;

                    // Allow video and audio to go to this client
                    connection.play = true;
                    return;
                case RtspRequestPause pauseMessage:
                    connection.play = false;
                    RtspResponse pause_response = message.CreateResponse();
                    listener.SendMessage(pause_response);
                    return;
                case RtspRequestGetParameter getParameterMessage:
                    // Create the reponse to GET_PARAMETER
                    RtspResponse getparameter_response = message.CreateResponse();
                    listener.SendMessage(getparameter_response);
                    return;
                case RtspRequestTeardown teardownMessage:
                    RemoveSession(connection);
                    listener.Dispose();
                    return;
            }
        }

        private void HandleSetup(RtspListener listener, RtspRequestSetup setupMessage)
        {
            // Check the RTSP transport
            // If it is UDP or Multicast, create the sockets
            // If it is RTP over RTSP we send data via the RTSP Listener

            // FIXME client may send more than one possible transport.
            // very rare
            RtspTransport transport = setupMessage.GetTransports()[0];

            // Construct the Transport: reply from the Server to the client
            RtspTransport? transport_reply = null;
            IRtpTransport? rtpTransport = null;

            if (transport.LowerTransport == RtspTransport.LowerTransportType.TCP)
            {
                Debug.Assert(transport.Interleaved != null, "If transport.Interleaved is null here the program did not handle well connection problem");
                rtpTransport = new RtpTcpTransport(listener)
                {
                    DataChannel = transport.Interleaved.First,
                    ControlChannel = transport.Interleaved.Second,
                };
                // RTP over RTSP mode
                transport_reply = new()
                {
                    SSrc = global_ssrc.ToString("X8"), // Convert to Hex, padded to 8 characters
                    LowerTransport = RtspTransport.LowerTransportType.TCP,
                    Interleaved = new PortCouple(transport.Interleaved.First, transport.Interleaved.Second)
                };
            }
            else if (transport.LowerTransport == RtspTransport.LowerTransportType.UDP && !transport.IsMulticast)
            {
                Debug.Assert(transport.ClientPort != null, "If transport.ClientPort is null here the program did not handle well connection problem");

                // RTP over UDP mode
                // Create a pair of UDP sockets - One is for the Data (eg Video/Audio), one is for the RTCP
                var udp_pair = new UDPSocket(50000, 51000); // give a range of 500 pairs (1000 addresses) to try incase some address are in use
                udp_pair.SetDataDestination(listener.RemoteEndPoint.Address.ToString(), transport.ClientPort.First);
                udp_pair.SetControlDestination(listener.RemoteEndPoint.Address.ToString(), transport.ClientPort.Second);
                udp_pair.ControlReceived += (local_sender, local_e) =>
                {
                    // RTCP data received
                    _logger.LogDebug("RTCP data received {local_sender} {local_e.Data.Data.Length}", local_sender, local_e.Data.Data.Length);
                    var connection = ConnectionByRtpTransport(local_sender as IRtpTransport);
                    connection?.UpdateKeepAlive();
                    local_e.Data.Dispose();
                };
                udp_pair.Start(); // start listening for data on the UDP ports

                // Pass the Port of the two sockets back in the reply
                transport_reply = new()
                {
                    SSrc = global_ssrc.ToString("X8"), // Convert to Hex, padded to 8 characters
                    LowerTransport = RtspTransport.LowerTransportType.UDP,
                    IsMulticast = false,
                    ServerPort = new PortCouple(udp_pair.DataPort, udp_pair.ControlPort),
                    ClientPort = transport.ClientPort
                };

                rtpTransport = udp_pair;
            }
            else if (transport.LowerTransport == RtspTransport.LowerTransportType.UDP && transport.IsMulticast)
            {
                // RTP over Multicast UDP mode}
                // Create a pair of UDP sockets in Multicast Mode
                // Pass the Ports of the two sockets back in the reply
                transport_reply = new()
                {
                    SSrc = global_ssrc.ToString("X8"), // Convert to Hex, padded to 8 characters
                    LowerTransport = RtspTransport.LowerTransportType.UDP,
                    IsMulticast = true,
                    Port = new PortCouple(7000, 7001)  // FIX
                };

                // for now until implemented
                transport_reply = null;
            }

            if (transport_reply != null)
            {
                // Update the stream within the session with transport information
                // If a Session ID is passed in we should match SessionID with other SessionIDs but we can match on RemoteAddress
                string copy_of_session_id = "";
                lock (rtspConnectionList)
                {
                    foreach (var setupConnection in rtspConnectionList.Where(connection => connection.Listener.RemoteEndPoint.Equals(listener.RemoteEndPoint)))
                    {
                        // Check the Track ID to determine if this is a SETUP for the Video Stream
                        // or a SETUP for an Audio Stream.
                        // In the SDP the H264 video track is TrackID 0
                        // and the Audio Track is TrackID 1
                        RTPStream stream;
                        if (setupMessage.RtspUri!.AbsolutePath.EndsWith("trackID=0")) stream = setupConnection.video;
                        else if (setupMessage.RtspUri.AbsolutePath.EndsWith("trackID=1")) stream = setupConnection.audio;
                        else continue;// error case - track unknown
                                      // found the connection
                                      // Add the transports to the stream
                        stream.rtpChannel = rtpTransport;
                        // When there is Video and Audio there are two SETUP commands.
                        // For the first SETUP command we will generate the connection.session_id and return a SessionID in the Reply.
                        // For the 2nd command the client will send is the SessionID.
                        if (string.IsNullOrEmpty(setupConnection.session_id))
                        {
                            setupConnection.session_id = session_handle.ToString();
                            session_handle++;
                        }
                        // ELSE, could check the Session passed in matches the Session we generated on last SETUP command
                        // Copy the Session ID, as we use it in the reply
                        copy_of_session_id = setupConnection.session_id;
                        break;
                    }
                }

                RtspResponse setup_response = setupMessage.CreateResponse();
                setup_response.Headers[RtspHeaderNames.Transport] = transport_reply.ToString();
                setup_response.Session = copy_of_session_id;
                setup_response.Timeout = rtspTimeOut;
                listener.SendMessage(setup_response);
            }
            else
            {
                RtspResponse setup_response = setupMessage.CreateResponse();
                // unsuported transport
                setup_response.ReturnCode = 461;
                listener.SendMessage(setup_response);
            }
        }

        private void HandleDescribe(RtspListener listener, RtspRequest message)
        {
            _logger.LogDebug("Request for {RtspUri}", message.RtspUri);

            // TODO. Check the requsted_url is valid. In this example we accept any RTSP URL

            // if the SPS and PPS are not defined yet, we have to return an error
            if (raw_sps == null || raw_pps == null)
            {
                RtspResponse describe_response2 = message.CreateResponse();
                describe_response2.ReturnCode = 400; // 400 Bad Request
                listener.SendMessage(describe_response2);
                return;
            }

            // Make the Base64 SPS and PPS
            // raw_sps has no 0x00 0x00 0x00 0x01 or 32 bit size header
            // raw_pps has no 0x00 0x00 0x00 0x01 or 32 bit size header
            string sps_str = Convert.ToBase64String(raw_sps);
            string pps_str = Convert.ToBase64String(raw_pps);

            // Make the profile-level-id
            // Eg a string of profile-level-id=42A01E is
            // a Profile eg Constrained Baseline, Baseline, Extended, Main, High. This defines which features in H264 are used
            // a Level eg 1,2,3 or 4. This defines a max resoution for the video. 2=up to SD, 3=upto 1080p. Decoders can then reserve sufficient RAM for frame buffers
            const int profile_idc = 77; // Main Profile
            const int profile_iop = 0; // bit 7 (msb) is 0 so constrained_flag is false
            const int level = 42; // Level 4.2

            string profile_level_id_str = profile_idc.ToString("X2") // convert to hex, padded to 2 characters
                                        + profile_iop.ToString("X2")
                                        + level.ToString("X2");

            StringBuilder sdp = new();

            // Generate the SDP
            // The sprop-parameter-sets provide the SPS and PPS for H264 video
            // The packetization-mode defines the H264 over RTP payloads used but is Optional
            sdp.Append("v=0\n");
            sdp.Append("o=user 123 0 IN IP4 0.0.0.0\n");
            sdp.Append("s=SharpRTSP Test Camera\n");
            sdp.Append($"m=video 0 RTP/AVP {video_payload_type}\n");
            sdp.Append("c=IN IP4 0.0.0.0\n");
            sdp.Append("a=control:trackID=0\n");
            sdp.Append($"a=rtpmap:{video_payload_type} H264/90000\n");
            sdp.Append($"a=fmtp:{video_payload_type} profile-level-id=").Append(profile_level_id_str)
                .Append("; sprop-parameter-sets=").Append(sps_str).Append(',').Append(pps_str).Append(";\n");

            // AUDIO
            sdp.Append("m=audio 0 RTP/AVP 0\n"); // <---- 0 means G711 ULAW
            sdp.Append("a=control:trackID=1\n");
            sdp.Append("a=rtpmap:0 PCMU/8000\n");
            // sdp.Append(media header info if we had AAC or other audio codec)

            byte[] sdp_bytes = Encoding.ASCII.GetBytes(sdp.ToString());

            // Create the reponse to DESCRIBE
            // This must include the Session Description Protocol (SDP)
            RtspResponse describe_response = message.CreateResponse();

            describe_response.AddHeader("Content-Base: " + message.RtspUri);
            describe_response.AddHeader("Content-Type: application/sdp");
            describe_response.Data = sdp_bytes;
            describe_response.AdjustContentLength();
            listener.SendMessage(describe_response);
        }

        private RTSPConnection? ConnectionByRtpTransport(IRtpTransport? rtpTransport)
        {
            if (rtpTransport is null) return null;
            lock (rtspConnectionList)
            {
                return rtspConnectionList.Find(c => c.video.rtpChannel == rtpTransport || c.audio.rtpChannel == rtpTransport);
            }
        }

        private RTSPConnection? ConnectionBySessionId(string? sessionId)
        {
            if (sessionId is null) return null;
            lock (rtspConnectionList)
            {
                return rtspConnectionList.Find(c => c.session_id == sessionId);
            }
        }

        public void CheckTimeouts(out int current_rtsp_count, out int current_rtsp_play_count)
        {
            DateTime now = DateTime.UtcNow;
            current_rtsp_count = rtspConnectionList.Count;
            var timeOut = now.AddSeconds(-rtspTimeOut);

            RTSPConnection[] connectionsToCheck;
            lock (rtspConnectionList)
            {
                // Convert to Array to allow us to delete from rtspConnectionList
                connectionsToCheck = rtspConnectionList.Where(c => timeOut > c.TimeSinceLastRtspKeepalive).ToArray();
            }

            foreach (RTSPConnection connection in connectionsToCheck)
            {
                _logger.LogDebug("Removing session {sessionId} due to TIMEOUT", connection.session_id);
                RemoveSession(connection);
            }

            lock (rtspConnectionList)
            {
                current_rtsp_play_count = rtspConnectionList.Count(c => c.play);
            }
        }

        // Feed in Raw SPS/PPS data - no 32 bit headers, no 00 00 00 01 headers
        public void FeedInRawSPSandPPS(byte[] sps_data, byte[] pps_data) // SPS data without any headers (00 00 00 01 or 32 bit lengths)
        {
            raw_sps = sps_data;
            raw_pps = pps_data;
        }

        // Feed in Raw NALs - no 32 bit headers, no 00 00 00 01 headers
        public void FeedInRawNAL(uint timestamp_ms, List<byte[]> nal_array)
        {
            CheckTimeouts(out int current_rtsp_count, out int current_rtsp_play_count);

            if (current_rtsp_play_count == 0) return;

            uint rtp_timestamp = timestamp_ms * 90; // 90kHz clock

            // Build a list of 1 or more RTP packets
            // The last packet will have the M bit set to '1'
            (List<Memory<byte>> rtp_packets, List<IMemoryOwner<byte>> memoryOwners) = PrepareVideoRtpPackets(nal_array, rtp_timestamp);

            RTSPConnection[] rtspConnectionListCopy;
            lock (rtspConnectionList)
            {
                // ToArray makes a temp copy of the list.
                // This lets us delete items in the foreach
                // eg when there is Write Error
                rtspConnectionListCopy = rtspConnectionList.ToArray();
            }
            // Go through each RTSP connection and output the NAL on the Video Session
            var tasks = rtspConnectionList.Select(async (connection) =>
            {
                // Only process Sessions in Play Mode
                if (!connection.play) return;

                if (connection.video.rtpChannel is null) return;
                _logger.LogDebug("Sending video session {sessionId} {TransportLogName} Timestamp(ms)={timestamp_ms}. RTP timestamp={rtp_timestamp}. Sequence={sequenceNumber}",
                    connection.session_id, TransportLogName(connection.video.rtpChannel), timestamp_ms, rtp_timestamp, videoSequenceNumber);

                if (connection.video.must_send_rtcp_packet && !await SendRTCP(rtp_timestamp, connection, connection.video))
                {
                    RemoveSession(connection);
                    return;
                }

                // There could be more than 1 RTP packet (if the data is fragmented)
                foreach (var rtp_packet in rtp_packets)
                {
                    Debug.Assert(connection.video.rtpChannel != null, "If connection.video.rptChannel is null here the program did not handle well connection problem");
                    try
                    {
                        // send the whole NAL. ** We could fragment the RTP packet into smaller chuncks that fit within the MTU
                        // Send to the IP address of the Client
                        // Send to the UDP Port the Client gave us in the SETUP command
                        await connection.video.rtpChannel.WriteToDataPortAsync(rtp_packet);
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine("UDP Write Exception " + e);
                        Console.WriteLine("Error writing to listener " + connection.Listener.RemoteEndPoint);
                        Console.WriteLine("Removing session " + connection.session_id + " due to write error");
                        RemoveSession(connection);
                        break; // exit out of foreach loop
                    }
                }
                connection.video.octet_count += (uint)nal_array.Sum(nal => nal.Length); // QUESTION - Do I need to include the RTP header bytes/fragmenting bytes
            }).ToArray();

            Task.WaitAll(tasks);

            foreach (var owner in memoryOwners)
            {
                owner.Dispose();
            }
        }

        private async Task<bool> SendRTCP(uint rtp_timestamp, RTSPConnection connection, RTPStream stream)
        {
            using var rtcp_owner = MemoryPool<byte>.Shared.Rent(28);
            var rtcpSenderReport = rtcp_owner.Memory[..28];
            const bool hasPadding = false;
            const int reportCount = 0; // an empty report
            int length = (rtcpSenderReport.Length / 4) - 1; // num 32 bit words minus 1
            RtcpPacketUtil.WriteHeader(rtcpSenderReport.Span, RtcpPacketUtil.RTCP_VERSION, hasPadding, reportCount,
                RtcpPacketUtil.RTCP_PACKET_TYPE_SENDER_REPORT, length, global_ssrc);
            RtcpPacketUtil.WriteSenderReport(rtcpSenderReport.Span, DateTime.UtcNow, rtp_timestamp, stream.rtp_packet_count, stream.octet_count);

            try
            {
                Debug.Assert(stream.rtpChannel != null, "If stream.rtpChannel is null here the program did not handle well connection problem");
                // Send to the IP address of the Client
                // Send to the UDP Port the Client gave us in the SETUP command
                await stream.rtpChannel.WriteToControlPortAsync(rtcpSenderReport);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error writing RTCP to listener {remoteAdress}", connection.Listener.RemoteEndPoint);
                return false;
            }
            return true;
            // Clear the flag. A timer may set this to True again at some point to send regular Sender Reports
            //HACK  connection.must_send_rtcp_packet = false; // A Timer may set this to true again later in case it is used as a Keepalive (eg IndigoVision)

        }

        private (List<Memory<byte>>, List<IMemoryOwner<byte>>) PrepareVideoRtpPackets(List<byte[]> nal_array, uint rtp_timestamp)
        {
            List<Memory<byte>> rtp_packets = [];
            List<IMemoryOwner<byte>> memoryOwners = [];
            for (int x = 0; x < nal_array.Count; x++)
            {
                var raw_nal = nal_array[x];
                bool last_nal = false;
                if (x == nal_array.Count - 1)
                {
                    last_nal = true; // last NAL in our nal_array
                }

                // The H264 Payload could be sent as one large RTP packet (assuming the receiver can handle it)
                // or as a Fragmented Data, split over several RTP packets with the same Timestamp.
                bool fragmenting = false;

                int packetMTU = 1400; // 65535; 
                packetMTU += -8 - 20 - 16; // -8 for UDP header, -20 for IP header, -16 normal RTP header len. ** LESS RTP EXTENSIONS !!!

                if (raw_nal.Length > packetMTU) fragmenting = true;

                // INDIGO VISION DOES NOT SUPPORT FRAGMENTATION. Send as one jumbo RTP packet and let OS split over MTUs.
                // NOTE TO SELF... perhaps this was because the SDP did not have the extra packetization flag
                //  fragmenting = false;

                if (!fragmenting)
                {
                    // Put the whole NAL into one RTP packet.
                    // Note some receivers will have maximum buffers and be unable to handle large RTP packets.
                    // Also with RTP over RTSP there is a limit of 65535 bytes for the RTP packet.

                    // 12 is header size when there are no CSRCs or extensions
                    var owner = MemoryPool<byte>.Shared.Rent(12 + raw_nal.Length);
                    memoryOwners.Add(owner);
                    var rtp_packet = owner.Memory[..(12 + raw_nal.Length)];

                    // Create an single RTP fragment

                    // RTP Packet Header
                    // 0 - Version, P, X, CC, M, PT and Sequence Number
                    //32 - Timestamp. H264 uses a 90kHz clock
                    //64 - SSRC
                    //96 - CSRCs (optional)
                    //nn - Extension ID and Length
                    //nn - Extension header

                    const bool rtpPadding = false;
                    const bool rtpHasExtension = false;
                    const int rtp_csrc_count = 0;

                    RtpPacketUtil.WriteHeader(rtp_packet.Span,
                        RtpPacketUtil.RTP_VERSION,
                        rtpPadding,
                        rtpHasExtension, rtp_csrc_count, last_nal, video_payload_type);

                    RtpPacketUtil.WriteSequenceNumber(rtp_packet.Span, videoSequenceNumber++);
                    RtpPacketUtil.WriteSSRC(rtp_packet.Span, global_ssrc);

                    RtpPacketUtil.WriteTimestamp(rtp_packet.Span, rtp_timestamp);

                    // Now append the raw NAL
                    raw_nal.CopyTo(rtp_packet[12..]);

                    rtp_packets.Add(rtp_packet);
                }
                else
                {
                    int data_remaining = raw_nal.Length;
                    int nal_pointer = 0;
                    int start_bit = 1;
                    int end_bit = 0;

                    // consume first byte of the raw_nal. It is used in the FU header
                    byte first_byte = raw_nal[0];
                    nal_pointer++;
                    data_remaining--;

                    while (data_remaining > 0)
                    {
                        int payload_size = Math.Min(packetMTU, data_remaining);
                        if (data_remaining == payload_size) end_bit = 1;

                        // 12 is header size. 2 bytes for FU-A header. Then payload
                        var destSize = 12 + 2 + payload_size;
                        var owner = MemoryPool<byte>.Shared.Rent(destSize);
                        memoryOwners.Add(owner);
                        var rtp_packet = owner.Memory[..destSize];

                        // RTP Packet Header
                        // 0 - Version, P, X, CC, M, PT and Sequence Number
                        //32 - Timestamp. H264 uses a 90kHz clock
                        //64 - SSRC
                        //96 - CSRCs (optional)
                        //nn - Extension ID and Length
                        //nn - Extension header

                        const bool rtpPadding = false;
                        const bool rtpHasExtension = false;
                        const int rtp_csrc_count = 0;

                        RtpPacketUtil.WriteHeader(rtp_packet.Span, RtpPacketUtil.RTP_VERSION,
                            rtpPadding, rtpHasExtension, rtp_csrc_count, last_nal && end_bit == 1, video_payload_type);

                        RtpPacketUtil.WriteSequenceNumber(rtp_packet.Span, videoSequenceNumber++);
                        RtpPacketUtil.WriteSSRC(rtp_packet.Span, global_ssrc);
                        RtpPacketUtil.WriteTimestamp(rtp_packet.Span, rtp_timestamp);

                        // Now append the Fragmentation Header (with Start and End marker) and part of the raw_nal
                        const byte f_bit = 0;
                        byte nri = (byte)(first_byte >> 5 & 0x03); // Part of the 1st byte of the Raw NAL (NAL Reference ID)
                        const byte type = 28; // FU-A Fragmentation

                        rtp_packet.Span[12] = (byte)((f_bit << 7) + (nri << 5) + type);
                        rtp_packet.Span[13] = (byte)((start_bit << 7) + (end_bit << 6) + (0 << 5) + (first_byte & 0x1F));

                        raw_nal.AsSpan(nal_pointer, payload_size).CopyTo(rtp_packet[14..].Span);
                        nal_pointer += payload_size;
                        data_remaining -= payload_size;

                        rtp_packets.Add(rtp_packet);

                        start_bit = 0;
                    }
                }
            }

            return (rtp_packets, memoryOwners);
        }

        private void RemoveSession(RTSPConnection connection)
        {
            connection.play = false; // stop sending data
            connection.video.rtpChannel?.Dispose();
            connection.video.rtpChannel = null;
            connection.audio.rtpChannel?.Dispose();
            connection.audio.rtpChannel = null;
            connection.Listener.Dispose();
            lock (rtspConnectionList)
            {
                rtspConnectionList.Remove(connection);
            }
        }

        public void FeedInAudioPacket(uint timestamp_ms, ReadOnlyMemory<byte> audio_packet)
        {
            CheckTimeouts(out int currentRtspCount, out int currentRtspPlayCount);

            // Console.WriteLine(current_rtsp_count + " RTSP clients connected. " + current_rtsp_play_count + " RTSP clients in PLAY mode");

            if (currentRtspPlayCount == 0) return;

            uint rtp_timestamp = timestamp_ms * 8; // 8kHz clock

            // Put the whole Audio Packet into one RTP packet.
            // 12 is header size when there are no CSRCs or extensions
            var size = 12 + audio_packet.Length;
            using var owner = MemoryPool<byte>.Shared.Rent(size);
            var rtp_packet = owner.Memory[..size];
            // Create an single RTP fragment

            // RTP Packet Header
            // 0 - Version, P, X, CC, M, PT and Sequence Number
            //32 - Timestamp. H264 uses a 90kHz clock
            //64 - SSRC
            //96 - CSRCs (optional)
            //nn - Extension ID and Length
            //nn - Extension header

            const bool rtp_padding = false;
            const bool rtpHasExtension = false;
            int rtp_csrc_count = 0;
            const bool rtpMarker = true; // always 1 as this is the last (and only) RTP packet for this audio timestamp

            RtpPacketUtil.WriteHeader(rtp_packet.Span,
                RtpPacketUtil.RTP_VERSION, rtp_padding, rtpHasExtension, rtp_csrc_count, rtpMarker, audio_payload_type);

            RtpPacketUtil.WriteSequenceNumber(rtp_packet.Span, audioSequenceNumber++);
            RtpPacketUtil.WriteSSRC(rtp_packet.Span, global_ssrc);
            RtpPacketUtil.WriteTimestamp(rtp_packet.Span, rtp_timestamp);

            // Now append the audio packet
            audio_packet.CopyTo(rtp_packet[12..]);

            RTSPConnection[] listConnectionCopy;
            // SEND THE RTSP PACKET
            lock (rtspConnectionList)
            {
                // Makes a temp copy of the list.
                // This lets us delete items in the foreach
                // eg when there is Write Error
                listConnectionCopy = [.. rtspConnectionList];
            }
            // Go through each RTSP connection and output the Audio data to the Audio Session
            var tasks = listConnectionCopy.Select(async (connection) =>
            {
                // Only process Sessions in Play Mode
                if (!connection.play) return;

                // The client may have only subscribed to Video. Check if the client wants audio
                if (connection.audio.rtpChannel is null) return;

                Console.WriteLine("Sending audio session " + connection.session_id + " " + TransportLogName(connection.audio.rtpChannel) + " Timestamp(ms)=" + timestamp_ms + ". RTP timestamp=" + rtp_timestamp + ". Sequence=" + audioSequenceNumber);
                bool write_error = false;

                if (connection.audio.must_send_rtcp_packet)
                {
                    if (!await SendRTCP(rtp_timestamp, connection, connection.audio))
                    {
                        RemoveSession(connection);
                    }
                }

                // There could be more than 1 RTP packet (if the data is fragmented)
                {
                    try
                    {
                        // send the whole RTP packet
                        await connection.audio.rtpChannel.WriteToDataPortAsync(rtp_packet);
                    }
                    catch (Exception e)
                    {
                        _logger.LogWarning(e, "UDP Write Exception");
                        _logger.LogWarning("Error writing to listener {address}", connection.Listener.RemoteEndPoint);
                        write_error = true;
                    }
                }
                if (write_error)
                {
                    Console.WriteLine("Removing session " + connection.session_id + " due to write error");
                    RemoveSession(connection);
                }

                connection.audio.rtp_packet_count++;
                connection.audio.octet_count += (uint)audio_packet.Length; // QUESTION - Do I need to include the RTP header bytes/fragmenting bytes
            }).ToArray();
            Task.WaitAll(tasks);
        }

        private static string TransportLogName(IRtpTransport? transport)
        {
            return transport switch
            {
                RtpTcpTransport => "TCP",
                MulticastUDPSocket => "Multicast",
                UDPSocket => "UDP",
                _ => "",
            };
        }

        // An RTPStream can be a Video Stream, Audio Stream or a MetaData Stream
        public class RTPStream
        {
            public int trackID;
            public bool must_send_rtcp_packet = false; // when true will send out a RTCP packet to match Wall Clock Time to RTP Payload timestamps
                                                       // 16 bit RTP packet sequence number used with this client connection
            public IRtpTransport? rtpChannel;     // Pair of UDP sockets (data and control) used when sending via UDP
            public DateTime time_since_last_rtcp_keepalive = DateTime.UtcNow; // Time since last RTCP message received - used to spot dead UDP clients
            public uint rtp_packet_count = 0;       // Used in the RTCP Sender Report to state how many RTP packets have been transmitted (for packet loss)
            public uint octet_count = 0;        // number of bytes of video that have been transmitted (for average bandwidth monitoring)
        }

        public class RTSPConnection
        {
            // The RTSP client connection
            public required RtspListener Listener { get; init; }
            // set to true when Session is in Play mode
            public bool play;

            // Time since last RTSP message received - used to spot dead UDP clients
            public DateTime TimeSinceLastRtspKeepalive { get; private set; } = DateTime.UtcNow;
            // Client Hostname/IP Address
            public string session_id = "";             // RTSP Session ID used with this client connection

            public RTPStream video = new();
            public RTPStream audio = new();

            public void UpdateKeepAlive()
            {
                TimeSinceLastRtspKeepalive = DateTime.UtcNow;
            }
        }
    }
}