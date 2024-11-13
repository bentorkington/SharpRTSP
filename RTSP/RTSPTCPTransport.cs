using System;
using System.Diagnostics.Contracts;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;

namespace Rtsp
{
    /// <summary>
    /// TCP Connection for Rtsp
    /// </summary>
    public class RtspTcpTransport : IRtspTransport, IDisposable
    {
        private readonly IPEndPoint _currentEndPoint;
        private readonly IPEndPoint _localEndPoint;
        private TcpClient _RtspServerClient;
        private uint _commandCounter;

        /// <summary>
        /// Initializes a new instance of the <see cref="RtspTcpTransport"/> class.
        /// </summary>
        /// <param name="tcpConnection">The underlying TCP connection.</param>
        public RtspTcpTransport(TcpClient tcpConnection)
        {
            if (tcpConnection == null)
                throw new ArgumentNullException(nameof(tcpConnection));
            Contract.EndContractBlock();

            _currentEndPoint = tcpConnection.Client.RemoteEndPoint as IPEndPoint ?? throw new InvalidOperationException("The local endpoint can not be determined.");
            _localEndPoint = tcpConnection.Client.LocalEndPoint as IPEndPoint ?? throw new InvalidOperationException("The remote endpoint can not be determined.");
            _RtspServerClient = tcpConnection;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="RtspTcpTransport"/> class.
        /// </summary>
        /// <param name="uri">The RTSP uri to connect to.</param>
        public RtspTcpTransport(Uri uri)
            : this(new TcpClient(uri.Host, uri.Port))
        { }

        #region IRtspTransport Membres

        /// <summary>
        /// Gets the stream of the transport.
        /// </summary>
        /// <returns>A stream</returns>
        public virtual Stream GetStream() => _RtspServerClient.GetStream();

        /// <summary>
        /// Gets the remote address.
        /// </summary>
        /// <value>The remote address.</value>
        public string RemoteAddress => _currentEndPoint.ToString();

        /// <summary>
        /// Gets the remote endpoint.
        /// </summary>
        /// <value>The remote endpoint.</value>
        public IPEndPoint RemoteEndPoint => _currentEndPoint;

        /// <summary>
        /// Gets the local endpoint.
        /// </summary>
        /// <value>The local endpoint.</value>
        public IPEndPoint LocalEndPoint => _localEndPoint;


        public uint NextCommandIndex() => ++_commandCounter;

        /// <summary>
        /// Closes this instance.
        /// </summary>
        public void Close()
        {
            Dispose(true);
        }

        /// <summary>
        /// Gets a value indicating whether this <see cref="IRtspTransport"/> is connected.
        /// </summary>
        /// <value><see langword="true"/> if connected; otherwise, <see langword="false"/>.</value>
        public bool Connected => _RtspServerClient.Client != null && _RtspServerClient.Connected;


        /// <summary>
        /// Reconnect this instance.
        /// <remarks>Must do nothing if already connected.</remarks>
        /// </summary>
        /// <exception cref="System.Net.Sockets.SocketException">Error during socket </exception>
        public void Reconnect()
        {
            if (Connected)
                return;
            _RtspServerClient = new TcpClient();
            _RtspServerClient.Connect(_currentEndPoint);
        }

        #endregion

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                _RtspServerClient.Close();
            }
        }
    }
}
