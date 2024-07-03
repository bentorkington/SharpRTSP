using System;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;

namespace Rtsp
{
    /// <summary>
    /// TCP Connection with TLS for Rtsp
    /// </summary>
    public class RtspTcpTlsTransport : RtspTcpTransport
    {
        private readonly RemoteCertificateValidationCallback? _userCertificateSelectionCallback;

        /// <summary>
        /// Initializes a new instance of the <see cref="RtspTcpTlsTransport"/> class.
        /// </summary>
        /// <param name="tcpConnection">The underlying TCP connection.</param>
        public RtspTcpTlsTransport(TcpClient tcpConnection, RemoteCertificateValidationCallback? userCertificateSelectionCallback = null) : base(tcpConnection)
        {
            _userCertificateSelectionCallback = userCertificateSelectionCallback;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="RtspTcpTransport"/> class.
        /// </summary>
        /// <param name="uri">The RTSP uri to connect to.</param>
        public RtspTcpTlsTransport(Uri uri, RemoteCertificateValidationCallback? userCertificateSelectionCallback)
            : this(new TcpClient(uri.Host, uri.Port), userCertificateSelectionCallback)
        {
        }

        /// <summary>
        /// Gets the stream of the transport.
        /// </summary>
        /// <returns>A stream</returns>
        public override Stream GetStream()
        {
            var sslStream = new SslStream(base.GetStream(), leaveInnerStreamOpen: true, _userCertificateSelectionCallback);

            sslStream.AuthenticateAsClient(RemoteAddress);
            return sslStream;
        }
    }
}
