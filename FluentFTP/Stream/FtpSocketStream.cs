﻿using System;
using System.IO;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;

#if (CORE || NETFX45)
using System.Threading.Tasks;
#endif

namespace FluentFTP {

	/// <summary>
	/// Stream class used for talking. Used by FtpClient, extended by FtpDataStream
	/// </summary>
	public class FtpSocketStream : Stream, IDisposable {
		/// <summary>
		/// Used for tacking read/write activity on the socket
		/// to determine if Poll() should be used to test for
		/// socket connectivity. The socket in this class will
		/// not know it has been disconnected if the remote host
		/// closes the connection first. Using Poll() avoids 
		/// the exception that would be thrown when trying to
		/// read or write to the disconnected socket.
		/// </summary>
		private DateTime m_lastActivity = DateTime.Now;

		private Socket m_socket = null;
		/// <summary>
		/// The socket used for talking
		/// </summary>
		protected Socket Socket {
			get {
				return m_socket;
			}
			private set {
				m_socket = value;
			}
		}

		int m_socketPollInterval = 15000;
		/// <summary>
		/// Gets or sets the length of time in milliseconds
		/// that must pass since the last socket activity
		/// before calling Poll() on the socket to test for
		/// connectivity. Setting this interval too low will
		/// have a negative impact on performance. Setting this
		/// interval to 0 disables Poll()'ing all together.
		/// The default value is 15 seconds.
		/// </summary>
		public int SocketPollInterval {
			get { return m_socketPollInterval; }
			set { m_socketPollInterval = value; }
		}

		/// <summary>
		/// Gets the number of available bytes on the socket, 0 if the
		/// socket has not been initialized. This property is used internally
		/// by FtpClient in an effort to detect disconnections and gracefully
		/// reconnect the control connection.
		/// </summary>
		internal int SocketDataAvailable {
			get {
				if (m_socket != null)
					return m_socket.Available;
				return 0;
			}
		}

		/// <summary>
		/// Gets a value indicating if this socket stream is connected
		/// </summary>
		public bool IsConnected {
			get {
				try {
					if (m_socket == null)
						return false;

					if (!m_socket.Connected) {
						Close();
						return false;
					}

					if (!CanRead || !CanWrite) {
						Close();
						return false;
					}

					if (m_socketPollInterval > 0 && DateTime.Now.Subtract(m_lastActivity).TotalMilliseconds > m_socketPollInterval) {
						FtpTrace.WriteStatus(FtpTraceLevel.Verbose, "Testing connectivity using Socket.Poll()...");
						if (m_socket.Poll(500000, SelectMode.SelectRead) && m_socket.Available == 0) {
							Close();
							return false;
						}
					}
				} catch (SocketException sockex) {
					Close();
					FtpTrace.WriteStatus(FtpTraceLevel.Warn, "FtpSocketStream.IsConnected: Caught and discarded SocketException while testing for connectivity: " + sockex.ToString());
					return false;
				} catch (IOException ioex) {
					Close();
					FtpTrace.WriteStatus(FtpTraceLevel.Warn, "FtpSocketStream.IsConnected: Caught and discarded IOException while testing for connectivity: " + ioex.ToString());
					return false;
				}

				return true;
			}
		}

		/// <summary>
		/// Gets a value indicating if encryption is being used
		/// </summary>
		public bool IsEncrypted {
			get {
#if NO_SSL
				return false;
#else
				return m_sslStream != null;
#endif
			}
		}

		NetworkStream m_netStream = null;
		/// <summary>
		/// The non-encrypted stream
		/// </summary>
		private NetworkStream NetworkStream {
			get {
				return m_netStream;
			}
			set {
				m_netStream = value;
			}
		}

#if !NO_SSL
		SslStream m_sslStream = null;
		/// <summary>
		/// The encrypted stream
		/// </summary>
		private SslStream SslStream {
			get {
				return m_sslStream;
			}
			set {
				m_sslStream = value;
			}
		}
#endif

		/// <summary>
		/// Gets the underlying stream, could be a NetworkStream or SslStream
		/// </summary>
		protected Stream BaseStream {
			get {
#if NO_SSL
				if (m_netStream != null)
					return m_netStream;
#else
				if (m_sslStream != null)
					return m_sslStream;
				else if (m_netStream != null)
					return m_netStream;
#endif

				return null;
			}
		}

		/// <summary>
		/// Gets a value indicating if this stream can be read
		/// </summary>
		public override bool CanRead {
			get {
				if (m_netStream != null)
					return m_netStream.CanRead;
				return false;
			}
		}

		/// <summary>
		/// Gets a value indicating if this stream if seekable
		/// </summary>
		public override bool CanSeek {
			get {
				return false;
			}
		}

		/// <summary>
		/// Gets a value indicating if this stream can be written to
		/// </summary>
		public override bool CanWrite {
			get {
				if (m_netStream != null)
					return m_netStream.CanWrite;

				return false;
			}
		}

		/// <summary>
		/// Gets the length of the stream
		/// </summary>
		public override long Length {
			get {
				return 0;
			}
		}

		/// <summary>
		/// Gets the current position of the stream. Trying to
		/// set this property throws an InvalidOperationException()
		/// </summary>
		public override long Position {
			get {
				if (BaseStream != null)
					return BaseStream.Position;
				return 0;
			}
			set {
				throw new InvalidOperationException();
			}
		}

		event FtpSocketStreamSslValidation m_sslvalidate = null;
		/// <summary>
		/// Event is fired when a SSL certificate needs to be validated
		/// </summary>
		public event FtpSocketStreamSslValidation ValidateCertificate {
			add {
				m_sslvalidate += value;
			}
			remove {
				m_sslvalidate -= value;
			}
		}

		int m_readTimeout = Timeout.Infinite;
		/// <summary>
		/// Gets or sets the amount of time to wait for a read operation to complete. Default
		/// value is Timeout.Infinite.
		/// </summary>
		public override int ReadTimeout {
			get {
				return m_readTimeout;
			}
			set {
				m_readTimeout = value;
			}
		}

		int m_connectTimeout = 30000;
		/// <summary>
		/// Gets or sets the length of time milliseconds to wait
		/// for a connection succeed before giving up. The default
		/// is 30000 (30 seconds).
		/// </summary>
		public int ConnectTimeout {
			get {
				return m_connectTimeout;
			}
			set {
				m_connectTimeout = value;
			}
		}

		/// <summary>
		/// Gets the local end point of the socket
		/// </summary>
		public IPEndPoint LocalEndPoint {
			get {
				if (m_socket == null)
					return null;
				return (IPEndPoint)m_socket.LocalEndPoint;
			}
		}

		/// <summary>
		/// Gets the remote end point of the socket
		/// </summary>
		public IPEndPoint RemoteEndPoint {
			get {
				if (m_socket == null)
					return null;
				return (IPEndPoint)m_socket.RemoteEndPoint;
			}
		}

		/// <summary>
		/// Fires the SSL certificate validation event
		/// </summary>
		/// <param name="certificate">Certificate being validated</param>
		/// <param name="chain">Certificate chain</param>
		/// <param name="errors">Policy errors if any</param>
		/// <returns>True if it was accepted, false otherwise</returns>
		protected bool OnValidateCertificate(X509Certificate certificate, X509Chain chain, SslPolicyErrors errors) {
			FtpSocketStreamSslValidation evt = m_sslvalidate;

			if (evt != null) {
				FtpSslValidationEventArgs e = new FtpSslValidationEventArgs() {
					Certificate = certificate,
					Chain = chain,
					PolicyErrors = errors,
					Accept = (errors == SslPolicyErrors.None)
				};

				evt(this, e);
				return e.Accept;
			}

			// if the event was not handled then only accept
			// the certificate if there were no validation errors
			return (errors == SslPolicyErrors.None);
		}

		/// <summary>
		/// Throws an InvalidOperationException
		/// </summary>
		/// <param name="offset">Ignored</param>
		/// <param name="origin">Ignored</param>
		/// <returns></returns>
		public override long Seek(long offset, SeekOrigin origin) {
			throw new InvalidOperationException();
		}

		/// <summary>
		/// Throws an InvalidOperationException
		/// </summary>
		/// <param name="value">Ignored</param>
		public override void SetLength(long value) {
			throw new InvalidOperationException();
		}

		/// <summary>
		/// Flushes the stream
		/// </summary>
		public override void Flush() {
			if (!IsConnected)
				throw new InvalidOperationException("The FtpSocketStream object is not connected.");

			if (BaseStream == null)
				throw new InvalidOperationException("The base stream of the FtpSocketStream object is null.");

			BaseStream.Flush();
		}

#if (CORE || NETFX45)

		/// <summary>
		/// Flushes the stream asynchronously
		/// </summary>
		/// <param name="token">The <see cref="CancellationToken"/> for this task</param>
		public override async Task FlushAsync(CancellationToken token) {
			if (!IsConnected)
				throw new InvalidOperationException("The FtpSocketStream object is not connected.");

			if (BaseStream == null)
				throw new InvalidOperationException("The base stream of the FtpSocketStream object is null.");

			await BaseStream.FlushAsync(token);
		}

#endif

		/// <summary>
		/// Bypass the stream and read directly off the socket.
		/// </summary>
		/// <param name="buffer">The buffer to read into</param>
		/// <returns>The number of bytes read</returns>
		internal int RawSocketRead(byte[] buffer) {
			int read = 0;

			if (m_socket != null && m_socket.Connected) {
				read = m_socket.Receive(buffer, buffer.Length, 0);
			}

			return read;
		}

		/// <summary>
		/// Reads data from the stream
		/// </summary>
		/// <param name="buffer">Buffer to read into</param>
		/// <param name="offset">Where in the buffer to start</param>
		/// <param name="count">Number of bytes to be read</param>
		/// <returns>The amount of bytes read from the stream</returns>
		public override int Read(byte[] buffer, int offset, int count) {
#if !CORE
			IAsyncResult ar = null;
#endif

			if (BaseStream == null)
				return 0;

			m_lastActivity = DateTime.Now;
#if CORE
			return BaseStream.ReadAsync(buffer, offset, count).Result;
#else
			ar = BaseStream.BeginRead(buffer, offset, count, null, null);
			if (!ar.AsyncWaitHandle.WaitOne(m_readTimeout, true)) {
				Close();
				throw new TimeoutException("Timed out trying to read data from the socket stream!");
			}
			return BaseStream.EndRead(ar);
#endif
		}

#if (CORE || NETFX45)

		/// <summary>
		/// Reads data from the stream
		/// </summary>
		/// <param name="buffer">Buffer to read into</param>
		/// <param name="offset">Where in the buffer to start</param>
		/// <param name="count">Number of bytes to be read</param>
		/// <param name="token">The <see cref="CancellationToken"/> for this task</param>
		/// <returns>The amount of bytes read from the stream</returns>
		public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) {
			if (BaseStream == null)
				return 0;

			m_lastActivity = DateTime.Now;
			return await BaseStream.ReadAsync(buffer, offset, count, token);
		}
#endif

		/// <summary>
		/// Reads a line from the socket
		/// </summary>
		/// <param name="encoding">The type of encoding used to convert from byte[] to string</param>
		/// <returns>A line from the stream, null if there is nothing to read</returns>
		public string ReadLine(System.Text.Encoding encoding) {
			List<byte> data = new List<byte>();
			byte[] buf = new byte[1];
			string line = null;

			while (Read(buf, 0, buf.Length) > 0) {
				data.Add(buf[0]);
				if ((char)buf[0] == '\n') {
					line = encoding.GetString(data.ToArray()).Trim('\r', '\n');
					break;
				}
			}

			return line;
		}

#if (CORE || NETFX45)
		/// <summary>
		/// Reads a line from the socket asynchronously
		/// </summary>
		/// <param name="encoding">The type of encoding used to convert from byte[] to string</param>
		/// <param name="token">The <see cref="CancellationToken"/> for this task</param>
		/// <returns>A line from the stream, null if there is nothing to read</returns>
		public async Task<string> ReadLineAsync(System.Text.Encoding encoding, CancellationToken token) {
			List<byte> data = new List<byte>();
			byte[] buf = new byte[1];
			string line = null;

			while (await ReadAsync(buf, 0, buf.Length, token) > 0) {
				data.Add(buf[0]);
				if ((char)buf[0] == '\n') {
					line = encoding.GetString(data.ToArray()).Trim('\r', '\n');
					break;
				}
			}

			return line;
		}

		/// <summary>
		/// Reads a line from the socket asynchronously
		/// </summary>
		/// <param name="encoding">The type of encoding used to convert from byte[] to string</param>
		/// <returns>A line from the stream, null if there is nothing to read</returns>
		public async Task<string> ReadLineAsync(System.Text.Encoding encoding) {
			return await ReadLineAsync(encoding, CancellationToken.None);
		}
#endif

		/// <summary>
		/// Writes data to the stream
		/// </summary>
		/// <param name="buffer">Buffer to write to stream</param>
		/// <param name="offset">Where in the buffer to start</param>
		/// <param name="count">Number of bytes to be read</param>
		public override void Write(byte[] buffer, int offset, int count) {
			if (BaseStream == null)
				return;

			BaseStream.Write(buffer, offset, count);
			m_lastActivity = DateTime.Now;
		}

#if (CORE || NETFX45)
		/// <summary>
		/// Writes data to the stream asynchronously
		/// </summary>
		/// <param name="buffer">Buffer to write to stream</param>
		/// <param name="offset">Where in the buffer to start</param>
		/// <param name="count">Number of bytes to be read</param>
		/// <param name="token">The <see cref="CancellationToken"/> for this task</param>
		public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token) {
			if (BaseStream == null)
				return;

			await BaseStream.WriteAsync(buffer, offset, count, token);
			m_lastActivity = DateTime.Now;
		}
#endif

		/// <summary>
		/// Writes a line to the stream using the specified encoding
		/// </summary>
		/// <param name="encoding">Encoding used for writing the line</param>
		/// <param name="buf">The data to write</param>
		public void WriteLine(System.Text.Encoding encoding, string buf) {
			byte[] data;
			data = encoding.GetBytes((buf + "\r\n"));
			Write(data, 0, data.Length);
		}

#if (CORE || NETFX45)
		/// <summary>
		/// Writes a line to the stream using the specified encoding asynchronously
		/// </summary>
		/// <param name="encoding">Encoding used for writing the line</param>
		/// <param name="buf">The data to write</param>
		/// <param name="token">The <see cref="CancellationToken"/> for this task</param>
		public async Task WriteLineAsync(System.Text.Encoding encoding, string buf, CancellationToken token) {
			byte[] data = encoding.GetBytes(buf + "\r\n");
			await WriteAsync(data, 0, data.Length, token);
		}

		/// <summary>
		/// Writes a line to the stream using the specified encoding asynchronously
		/// </summary>
		/// <param name="encoding">Encoding used for writing the line</param>
		/// <param name="buf">The data to write</param>
		public async Task WriteLineAsync(System.Text.Encoding encoding, string buf) {
			await WriteLineAsync(encoding, buf, CancellationToken.None);
		}
#endif

		/// <summary>
		/// Disposes the stream
		/// </summary>
		public new void Dispose() {
			FtpTrace.WriteStatus(FtpTraceLevel.Verbose, "Disposing FtpSocketStream...");
			Close();
		}

		/// <summary>
		/// Disconnects from server
		/// </summary>
#if CORE
		public void Close() {
#else
		public override void Close() {
#endif
			if (m_socket != null) {
				try {
					if (m_socket.Connected) {
						////
						// Calling Shutdown() with mono causes an
						// exception if the remote host closed first
						//m_socket.Shutdown(SocketShutdown.Both);
#if CORE
						m_socket.Dispose();
#else
						m_socket.Close();
#endif
					}

#if !NET2
					m_socket.Dispose();
#endif
				} catch (SocketException ex) {
					FtpTrace.WriteStatus(FtpTraceLevel.Warn, "Caught and discarded a SocketException while cleaning up the Socket: " + ex.ToString());
				} finally {
					m_socket = null;
				}
			}

			if (m_netStream != null) {
				try {
					m_netStream.Dispose();
				} catch (IOException ex) {
					FtpTrace.WriteStatus(FtpTraceLevel.Warn, "Caught and discarded an IOException while cleaning up the NetworkStream: " + ex.ToString());
				} finally {
					m_netStream = null;
				}
			}

#if !NO_SSL
			if (m_sslStream != null) {
				try {
					m_sslStream.Dispose();
				} catch (IOException ex) {
					FtpTrace.WriteStatus(FtpTraceLevel.Warn, "Caught and discarded an IOException while cleaning up the SslStream: " + ex.ToString());
				} finally {
					m_sslStream = null;
				}
			}
#endif

#if CORE
            base.Dispose();
#endif
		}

		/// <summary>
		/// Sets socket options on the underlying socket
		/// </summary>
		/// <param name="level">SocketOptionLevel</param>
		/// <param name="name">SocketOptionName</param>
		/// <param name="value">SocketOptionValue</param>
		public void SetSocketOption(SocketOptionLevel level, SocketOptionName name, bool value) {
			if (m_socket == null)
				throw new InvalidOperationException("The underlying socket is null. Have you established a connection?");
			m_socket.SetSocketOption(level, name, value);
		}

		/// <summary>
		/// Connect to the specified host
		/// </summary>
		/// <param name="host">The host to connect to</param>
		/// <param name="port">The port to connect to</param>
		/// <param name="ipVersions">Internet Protocol versions to support during the connection phase</param>
		public void Connect(string host, int port, FtpIpVersion ipVersions) {
#if CORE
			IPAddress[] addresses = Dns.GetHostAddressesAsync(host).Result;
#else
			IAsyncResult ar = null;
			IPAddress[] addresses = Dns.GetHostAddresses(host);
#endif

			if (ipVersions == 0)
				throw new ArgumentException("The ipVersions parameter must contain at least 1 flag.");

			for (int i = 0; i < addresses.Length; i++) {
#if DEBUG
				FtpTrace.WriteStatus(FtpTraceLevel.Verbose, "Checking : " + addresses[i].AddressFamily + ": " + addresses[i].ToString());
#endif
				// we don't need to do this check unless
				// a particular version of IP has been
				// omitted so we won't.
				if (ipVersions != FtpIpVersion.ANY) {
					switch (addresses[i].AddressFamily) {
						case AddressFamily.InterNetwork:
							if ((ipVersions & FtpIpVersion.IPv4) != FtpIpVersion.IPv4) {
#if DEBUG
								FtpTrace.WriteStatus(FtpTraceLevel.Verbose, "Skipped IPV4 address : " + addresses[i].ToString());
#endif
								continue;
							}
							break;
						case AddressFamily.InterNetworkV6:
							if ((ipVersions & FtpIpVersion.IPv6) != FtpIpVersion.IPv6) {
#if DEBUG
								FtpTrace.WriteStatus(FtpTraceLevel.Verbose, "Skipped IPV6 address : " + addresses[i].ToString());
#endif
								continue;
							}
							break;
					}
				}

				m_socket = new Socket(addresses[i].AddressFamily, SocketType.Stream, ProtocolType.Tcp);
#if CORE
				m_socket.ConnectAsync(addresses[i], port).Wait();
#else
				ar = m_socket.BeginConnect(addresses[i], port, null, null);
				if (!ar.AsyncWaitHandle.WaitOne(m_connectTimeout, true)) {
					Close();

					// check to see if we're out of addresses, and throw a TimeoutException
					if ((i + 1) == addresses.Length) {
						throw new TimeoutException("Timed out trying to connect!");
					}
				} else {
					m_socket.EndConnect(ar);
					// we got a connection, break out
					// of the loop.
					break;
				}
#endif
			}

			// make sure that we actually connected to
			// one of the addresses returned from GetHostAddresses()
			if (m_socket == null || !m_socket.Connected) {
				Close();
				throw new IOException("Failed to connect to host.");
			}

			m_netStream = new NetworkStream(m_socket);
			m_lastActivity = DateTime.Now;
		}

#if !NO_SSL
		/// <summary>
		/// Activates SSL on this stream using default protocols. Fires the ValidateCertificate event. 
		/// If this event is not handled and there are SslPolicyErrors present, the certificate will 
		/// not be accepted.
		/// </summary>
		/// <param name="targethost">The host to authenticate the certificate against</param>
		public void ActivateEncryption(string targethost) {
#if CORE
			ActivateEncryption(targethost, null, SslProtocols.Tls11 | SslProtocols.Ssl3);
#else
			ActivateEncryption(targethost, null, SslProtocols.Default);
#endif
		}

		/// <summary>
		/// Activates SSL on this stream using default protocols. Fires the ValidateCertificate event.
		/// If this event is not handled and there are SslPolicyErrors present, the certificate will 
		/// not be accepted.
		/// </summary>
		/// <param name="targethost">The host to authenticate the certificate against</param>
		/// <param name="clientCerts">A collection of client certificates to use when authenticating the SSL stream</param>
		public void ActivateEncryption(string targethost, X509CertificateCollection clientCerts) {
#if CORE
			ActivateEncryption(targethost, clientCerts, SslProtocols.Tls11 | SslProtocols.Ssl3);
#else
			ActivateEncryption(targethost, clientCerts, SslProtocols.Default);
#endif
		}

		/// <summary>
		/// Activates SSL on this stream using the specified protocols. Fires the ValidateCertificate event.
		/// If this event is not handled and there are SslPolicyErrors present, the certificate will 
		/// not be accepted.
		/// </summary>
		/// <param name="targethost">The host to authenticate the certificate against</param>
		/// <param name="clientCerts">A collection of client certificates to use when authenticating the SSL stream</param>
		/// <param name="sslProtocols">A bitwise parameter for supported encryption protocols.</param>
		/// <exception cref="AuthenticationException">Thrown when authentication fails</exception>
		public void ActivateEncryption(string targethost, X509CertificateCollection clientCerts, SslProtocols sslProtocols) {
			if (!IsConnected)
				throw new InvalidOperationException("The FtpSocketStream object is not connected.");

			if (m_netStream == null)
				throw new InvalidOperationException("The base network stream is null.");

			if (m_sslStream != null)
				throw new InvalidOperationException("SSL Encryption has already been enabled on this stream.");

			try {
				DateTime auth_start;
				TimeSpan auth_time_total;

				m_sslStream = new SslStream(NetworkStream, true, new RemoteCertificateValidationCallback(
					delegate(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors) {
						return OnValidateCertificate(certificate, chain, sslPolicyErrors);
					}
				));

				auth_start = DateTime.Now;
#if CORE
				m_sslStream.AuthenticateAsClientAsync(targethost, clientCerts, sslProtocols, true).Wait();
#else
				m_sslStream.AuthenticateAsClient(targethost, clientCerts, sslProtocols, true);
#endif

				auth_time_total = DateTime.Now.Subtract(auth_start);
				FtpTrace.WriteStatus(FtpTraceLevel.Info, "FTPS Authentication Successful");
				FtpTrace.WriteStatus(FtpTraceLevel.Verbose, "Time to activate encryption: " + auth_time_total.Hours + "h " + auth_time_total.Minutes + "m " + auth_time_total.Seconds + "s.  Total Seconds: " + auth_time_total.TotalSeconds + ".");

			} catch (AuthenticationException) {
				// authentication failed and in addition it left our 
				// ssl stream in an unusable state so cleanup needs
				// to be done and the exception can be re-thrown for
				// handling down the chain. (Add logging?)
				Close();
				FtpTrace.WriteStatus(FtpTraceLevel.Error, "FTPS Authentication Failed");
				throw;
			}
		}
#endif

		/// <summary>
		/// Instructs this stream to listen for connections on the specified address and port
		/// </summary>
		/// <param name="address">The address to listen on</param>
		/// <param name="port">The port to listen on</param>
		public void Listen(IPAddress address, int port) {
			if (!IsConnected) {
				if (m_socket == null)
					m_socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

				m_socket.Bind(new IPEndPoint(address, port));
				m_socket.Listen(1);
			}
		}

		/// <summary>
		/// Accepts a connection from a listening socket
		/// </summary>
		public void Accept() {
			if (m_socket != null)
				m_socket = m_socket.Accept();
		}

#if CORE
		public async Task AcceptAsync() {
			if (m_socket != null) {
				m_socket = await m_socket.AcceptAsync();
			}
		}
#else
		/// <summary>
		/// Asynchronously accepts a connection from a listening socket
		/// </summary>
		/// <param name="callback"></param>
		/// <param name="state"></param>
		/// <returns></returns>
		public IAsyncResult BeginAccept(AsyncCallback callback, object state) {
			if (m_socket != null)
				return m_socket.BeginAccept(callback, state);
			return null;
		}

		/// <summary>
		/// Completes a BeginAccept() operation
		/// </summary>
		/// <param name="ar">IAsyncResult returned from BeginAccept</param>
		public void EndAccept(IAsyncResult ar) {
			if (m_socket != null) {
				m_socket = m_socket.EndAccept(ar);
				m_netStream = new NetworkStream(m_socket);
			}
		}
#endif
	}
}