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
        private readonly RemoteCertificateValidationCallback? _userCertificateValidationCallback;

        /// <summary>
        /// Initializes a new instance of the <see cref="RtspTcpTlsTransport"/> class.
        /// </summary>
        /// <param name="tcpConnection">The underlying TCP connection.</param>
        /// <param name="userCertificateValidationCallback">The user certificate validation callback, <see langword="null"/> if default should be used.</param>
        public RtspTcpTlsTransport(TcpClient tcpConnection, RemoteCertificateValidationCallback? userCertificateValidationCallback = null) : base(tcpConnection)
        {
            _userCertificateValidationCallback = userCertificateValidationCallback;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="RtspTcpTransport"/> class.
        /// </summary>
        /// <param name="uri">The RTSP uri to connect to.</param>
        /// <param name="userCertificateValidationCallback">The user certificate validation callback, <see langword="null"/> if default should be used.</param>
        public RtspTcpTlsTransport(Uri uri, RemoteCertificateValidationCallback? userCertificateValidationCallback)
            : this(new TcpClient(uri.Host, uri.Port), userCertificateValidationCallback)
        {
        }

        /// <summary>
        /// Gets the stream of the transport.
        /// </summary>
        /// <returns>A stream</returns>
        public override Stream GetStream()
        {
            var sslStream = new SslStream(base.GetStream(), leaveInnerStreamOpen: true, _userCertificateValidationCallback);

            sslStream.AuthenticateAsClient(RemoteAddress);
            return sslStream;
        }
    }
}
